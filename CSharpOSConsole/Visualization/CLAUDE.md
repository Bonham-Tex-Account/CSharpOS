# CSharpOSConsole/Visualization Quick Reference

## Files

| File | Role |
|------|------|
| VisualizerModel.cs | Pure data snapshot: instruction history, MLFQ rows, register snapshot, output buffers, stats |
| HardwareEventBridge.cs | Subscribes to 9+ Hardware events; updates VisualizerModel; owns the Pacer |
| IVisualizerRenderer.cs | Interface: `Render(VisualizerModel)`, `RenderComplete(VisualizerModel)` |
| PlainTextRenderer.cs | Streaming deterministic text output; reproduces original ConsoleVisualizer output |
| SpectreDashboard.cs | Live Spectre.Console TUI; reads model directly; owns the run loop |
| NoOpRenderer.cs | Used as the bridge's renderer when SpectreDashboard drives directly |
| ConsoleVisualizer.cs | Coordinator used by tests: same public ctor as before; wraps PlainTextRenderer + bridge + pacer |
| FrameHistory.cs | Capped ring of immutable per-instruction snapshots for time-travel scrub |
| InteractionController.cs | Key handling: `HandleKey(ConsoleKey) → bool`; pure, no side effects |
| BuddyHeapView.cs | Reconstructs buddy tree from kernel bitmap + process table |
| FsDiskView.cs | Reconstructs FS state (superblock stats, block map, dir tree) from cache + Bin; static `ReadDisk(hw)` → immutable `Snapshot`; cache-first block reads |
| Pacer.cs | Delay/step pacing for the streaming renderer |

---

## VisualizerModel

Key data the bridge maintains and renderers read:

| Property | Type | Notes |
|----------|------|-------|
| InstructionHistory | list of InstructionStep | Recent instructions tagged by privilege; capped at LookBack |
| CurrentRegisters / PreviousRegisters | RegisterSnapshot | Diff'd per step for change highlighting |
| MlfqRows | list of MlfqProcessRow | One per live process: priority, ticks, state, pid |
| FreeBlocks | list of MemoryRange | For the memory-map bar |
| BuddyTree | BuddyHeapView | Reconstructed tree |
| DiskView | FsDiskView.Snapshot? | Reconstructed FS; rebuilt by the bridge only on an FS OS-routine (IvtFsSyscall/IvtFsOp/IvtCacheOp) + once at boot |
| FocusedProcess | int | Active process index (-1 = none) |
| OutputBuffers | dict int→list<int> | Per-process output history |
| FocusedOutput | list<int> | OutputBuffers[FocusedProcess] |
| RunStats | (instruction count, branch stats, cycles) | |
| TransitionLog | list<string> | Context switch / privilege change log |
| ShowProgramIo | bool | Toggle (key `o`) |

`HardwareEventBridge` REPLACES (never mutates) the process-table/free-block/buddy-tree objects on each event, so FrameHistory snapshots are safe by construction.

`RegisterSnapshot.Shown` = `[EAX, EBX, ECX, EDX, ESI, EDI]` — only these 6 are displayed/diffed.

---

## SpectreDashboard (SpectreDashboard.cs)

Section markers name their own methods — **Grep `// ===== <text>` to jump** (drift-proof); the `:N`
line numbers below are a convenience, last synced 2026-07-05. `BuildScreen` gained **canvas mode** (§3):
if the latest OUTS string contains `\n`, it shows that frame alone (2D block); otherwise it shows the
last `ScreenLines` output entries **one per line** (terminal scrollback — each shell command echo + each
OUT/OUTS on its own row), not joined onto a single row.

| Section | Line |
|---------|------|
| Constructor | :39 |
| Focus + I/O Helpers (ToggleIo, SubmitInput, CycleFocus, EnsureFocus) | :79 |
| Staggered Loading + Run Loop (ScheduleStaggeredLoads, Run, Inject, ForegroundSignal wiring) | :174 |
| Headless Testing Seams (RenderSnapshot, RenderSummary) | :316 |
| Layout + Top-Level Render (BuildLayout, RenderInto, Panel) | :384 |
| MLFQ + Buddy Panels (BuildProcessAndQueues, BuildQueues, BuildBuddyTree) | :534 |
| Registers + Heap Panels (BuildRegisters, BuildHeap, BuildMapBar, BuildStats) | :732 |
| Screen + Status (BuildScreen — **canvas mode** —, FocusedName, BuildStatus) | :913 |

### Constructor
```csharp
SpectreDashboard(Hardware hw, OperatingSystem os, VisualizerMode mode, int delayMs,
    DetailLevel detail = DetailLevel.High, bool showProgramIo = false)
```

### Panel Layout
```
┌─────────────────────────┬────────────────────────────────────────────────────────────┐
│ Program (User)          │ Kernel / OS                                                │
│  instruction stream     │  instruction stream                                         │
├─────────────┬───────────┴────────────┬───────────────────────────────────────────────┤
│ MLFQ table  │ Buddy tree             │ Screen (focused process output + input line)  │
├─────────────┴────────────────────────┴───────────────────────────────────────────────┤
│ Registers (EAX..EDI + flags, change-highlighted)                                     │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Memory map bar (proportional, per-owner colors)                                      │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Run stats + status footer                                                            │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

### Focus Model
- The Screen panel follows the **OS-designated foreground** process. The shell hands the terminal to a foreground child (e.g. `/bin/snake`) via `SETFOCUS`, which sets the hardware's active process; `EnsureFocus()` detects that (hw active process diverging from what the dashboard last set) and adopts it — so a foreground job's output shows **without a keypress**. (Before this, focus only advanced on terminate, so a foreground child never appeared while the shell parent stayed live in `WAIT`.)
- Tab → `CycleFocus()` → next live process, and installs a **manual override** (`manualFocus`) so the panel stays on the inspected process while a foreground job keeps running. The override lapses when its process dies, when the user Tabs back to the foreground, or when the OS moves the foreground (next `SETFOCUS`).
- `SetFocus(index)` points both `model.FocusedProcess` (Screen) and `hw.SetActiveProcess` (keyboard routing) at the same process, and records `lastSeenActive` so a dashboard-initiated change isn't misread as OS-driven.
- **Input echo:** `SubmitInput`/`SubmitStringInput` call `EchoInput(text)` → `model.RecordOutput(hw.GetActiveProcess(), text)` so a typed command/number stays in the scrollback above the fresh prompt after Enter (IN/INS deliver input to the process without echoing it, unlike a real tty). Echoes to the process the input is routed to (= the focused one). Covers both the live keyboard and the auto-script paths.

### Run pacing (frame-pace for full-screen programs)
Auto-run normally advances `renderStride` **user instructions** per tick (paced by `InteractionController.DelayMs`). A full-screen program (e.g. `/bin/snake`) redraws the whole screen with ~1–2k instructions per frame, so per-instruction pacing makes one frame take *minutes*. `SpectreDashboard.ShouldFramePace(focusedProcess, focusedReady, focusedOutput)` (pure, public test seam) detects this — the focused process is **Ready** (gating on Ready, not "some process running", stops a just-terminated process from spinning the burst) and its latest output is a multi-line "canvas" frame, or it is Ready with no output yet (prime the first frame) — and the loop switches to `RunFocusedFrame()`: run the emulator flat out until the focused process emits its **next output (frame)** / blocks / hits `FramePaceInstrCap`, then a fixed `FramePaceExtraMs`(55) pause, so **one tick = one frame** (paced by `DelayMs`+extra → ~6.5fps at 100ms, steerable; the fixed extra also steadies the rate against per-frame paging variance). Speed keys `+`/`-` still apply. Verified: snake advances exactly one frame per tick and steers.

### Process names
Panels label a process via `PlainTextRenderer.ProcessLabel(row, disk)`: OS-registered name (`row.Path` = `NameForBase`, e.g. the boot shell's `Process.DisplayName` "shell") → else the program file resolved from `row.FirstBlock` (`ProcessEntryFirstBlock`, set on spawn/exec) via `FsDiskView.NameByFirstBlock(model.DiskView)` (e.g. exec'd `/bin/snake` → "snake") → else `pN`. This is why forked/exec'd processes show a real program name instead of "(none)". `BuddyHeapView.ProcessRow` carries `FirstBlock`; pass the frame's `DiskView` into the static tree/queue renderers.
- Dashboard drives `hw.RaiseOutputComplete(e.Device)` on each ProgramOutput event (replaces retired ProcessIoRouter)

### Key Bindings (InteractionController)
| Key | Action |
|-----|--------|
| `a` | Auto-run mode |
| `s` | Single step |
| `+` / `-` | Faster / slower auto-run (speed ladder 0/turbo…800ms; `InteractionController.DelayMs`) |
| `→` (Right) | Step forward (live edge) or advance in history |
| `←` (Left) | Scrub back in history (replay, not reverse execution) |
| `Tab` | Cycle focus to next live process |
| `0`–`9` | Append digit to input buffer |
| `Enter` | Submit typed number to focused process stdin |
| `Backspace` | Clear input buffer |
| `o` | Toggle ShowProgramIo |
| `d` | Toggle the Buddy panel slot between the buddy tree and the Disk (filesystem) view (`SpectreDashboard.ShowDisk`) |
| `Ctrl-C` | tty-style: send SigTerm to the foreground process (Shell §2.5 JC-D; always intercepted, `Console.TreatControlCAsInput`) |
| `Ctrl-Z` | tty-style: send SigStop to the foreground process (Shell §2.5 JC-D) |
| `q` | Quit |

### Headless Testing Seam
```csharp
SpectreDashboard.RenderSnapshot(IAnsiConsole console, int maxSteps)
SpectreDashboard.RenderSummary(IAnsiConsole console)
```

Tests create a `NoColors` Spectre console over a `StringWriter` and call these to catch markup/layout exceptions without a TTY. The live TUI (`AnsiConsole`) requires a real terminal — verify manually with `dotnet run --project CSharpOSConsole`.

### Staggered Load
`SpectreDashboard.ScheduleStaggeredLoads(IEnumerable<Process> processes, int intervalSteps)` — queues processes for injection at run time (modes 6–8 in Program.cs). Uses an **empty** terminals dict (no windows).

### Scripted Input (auto-shell demos, modes 14–15)
`SpectreDashboard.SetAutoInputScript(IEnumerable<string> commands)` installs a queue of shell command lines typed in hands-free. The run loop calls `DriveAutoScript()` each iteration: it injects the next line (`SubmitStringInput` → `RaiseStringInputInterrupt`) **only** when `TryFindShellAtPrompt` finds a process `Blocked` on `WaitReason.StringInput` (the shell's `INS` prompt), one at a time, with an `AutoCommandGapFrames`-frame readable pause (frame- not instruction-based, since the shell runs no instructions while blocked). `RunScriptedHeadless(maxSteps)` is the headless test seam (no TTY) — drives the same path until the script drains and the shell settles back at its prompt. Test: `ShellTests.AutoShell_ScriptedInput_DrivesTheShellHandsFree`.

---

## ConsoleVisualizer (coordinator used by tests)

```csharp
ConsoleVisualizer cv = new ConsoleVisualizer(hw, os, writer);
// hw.Run(); hw.Run(); ...
// Output written to `writer` via PlainTextRenderer
```

Same public constructor signature as the original monolithic ConsoleVisualizer. Internally composes `VisualizerModel + HardwareEventBridge + PlainTextRenderer + Pacer`.

---

## FrameHistory

Immutable per-instruction snapshots (capped ring).

```csharp
frames.Push(snapshot);          // add at live edge
frames.CanGoBack                // true when past snapshots exist
frames.CanGoForward             // true when viewing history (not at live edge)
FrameSnapshot snap = frames.Back();    // step backward; returns the snapshot to display
FrameSnapshot snap = frames.Forward(); // step forward toward live edge
FrameSnapshot snap = frames.Current;  // current view (live or replay)
```

Snapshots are safe to store because the bridge REPLACES (never mutates) the objects they reference.

---

## InteractionController

```csharp
// Constructor (trailing Actions are optional; extend here to add new command keys)
InteractionController(FrameHistory frames, bool autoRun, int delayMs,
    Action toggleIo, Action cycleFocus, Action<int> submitInput,
    Action<string>? submitStringInput = null, Action<int>? submitKey = null,
    Action? toggleDisk = null)   // `d` key

// Usage
bool handled = interaction.HandleKey(ConsoleKey key);
bool auto = interaction.IsAuto;
int delayMs = interaction.DelayMs;
```

Pure: `HandleKey` returns true if the key was consumed. No side effects on Hardware; all mutations go through the injected callbacks or FrameHistory.

---

## BuddyHeapView

Reconstructs the buddy tree from:
- Kernel bitmap at `OsLayout.BuddyBitmapOffset` (bit=1 FREE, bit=0 used/split; node i → bit i-1)
- Process-table scan (allocated blocks pinned by (base, size), labeled by owner)

**Fixed bug vs original:** old code did a leaf-only scan, under-reporting free space at internal nodes (fully-free heap showed "(none)"). Now derives from the reconstructed tree.

---

## DetailLevel / VisualizerMode

`DetailLevel` enum: `Low` (renderStride=10), `Medium` (renderStride=3), `High` (renderStride=1).

`VisualizerMode` enum values (1–9): 1–5 = per-process window modes (legacy); 6 = Memory churn; 7 = Fill & drain heap; 8 = Scheduler+memory; 9 = Shell.

Modes 6–8 use `ScheduleStaggeredLoads` with `Programs.BusyThenHalt` jobs of sizes {128, 512, 1024}. Mode 9 (`RunShell`) installs the `/bin` command programs (ls/cat/rm/mkdir/echo/help + counter/average/guess) + a `/note` file into the FS, then boots `Programs.Shell()` — a real fork/exec/wait loop. Type an absolute command like `/bin/ls /`, `/bin/cat /note`, or `/bin/echo hi there`; the shell forks a child that exec-by-paths it, waits, and re-prompts.
