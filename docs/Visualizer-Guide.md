# CSharpOS Visualizer Guide

A practical guide to the console visualizer: how to launch it, what every menu option
shows, how to read each dashboard panel, and — in detail — how to drive the interactive
pieces (the **shell** and **snake**). A final section covers how each component is
implemented, for readers who want to extend it.

> This is a usage/architecture doc. For the instruction set see [`ISA.md`](ISA.md); for the
> kernel/OS internals see [`OS-Architecture.md`](OS-Architecture.md). Exact memory offsets
> and slot numbers should always be taken from the `os-facts` skill, not hardcoded here.

---

## 1. Launching

```
dotnet run --project CSharpOSConsole
```

The live TUI requires a **real terminal** (it uses `Spectre.Console`'s `AnsiConsole`). Piping
stdin or running under a captured console will exit at the menu (end-of-input quits). To point
at a different OS personality plugin:

```
dotnet run --project CSharpOSConsole -- --os-plugin path\to\OtherOSPlugin.dll
```

Default plugin is `BasicOSPlugin.dll` next to the host binary.

### The two prompts before every run

After you pick a menu option you are asked two things:

1. **Detail** (`VisualizerMode`) — how *verbose* the instruction stream / panels are:
   `1) minimal`, `2) normal` (default), `3) verbose`.
2. **Performance** (`DetailLevel`) — the *render stride*, i.e. how many emulator steps between
   redraws: `1) low (fast)` = redraw every 10 steps, `2) medium` = every 3, `3) high (full
   detail, default)` = every step. Lower performance = fewer redraws = faster wall-clock run,
   coarser animation. Interactive modes (shell, snake) are best on **high**.

Machine memory is `OsLayout.TotalSize + 32768` — it tracks the OS region automatically so the
buddy heap always has a clean 32 KB above the kernel (this is derived, never hardcoded, so it
can't go stale when the OS grows).

---

## 2. Menu options — what each run shows

| # | Option | What loads | What to watch |
|---|--------|-----------|---------------|
| 1 | Counter to ten | one process printing 1..10 | Screen panel counts up, then the process terminates and disappears from the MLFQ table |
| 2 | Average of a list | one process building `[10,20,30,40]` in memory, printing 25 | STORE/LOAD traffic in the instruction stream; a single output |
| 3 | Guessing game | one interactive process (secret = 42) | **type a number + Enter** in the Screen panel; it replies higher/lower |
| 4 | Counter + Average | both, round-robin | two rows in the MLFQ table sharing the CPU; Tab to switch which one the Screen shows |
| 5 | All three | counter + average + guess | three processes; scheduler interleaving; focus-cycle with Tab |
| 6 | Memory churn | short "busy then halt" jobs load & exit continuously | the **buddy tree** and **memory-map bar** constantly allocating/freeing |
| 7 | Fill & drain heap | mixed-size jobs fill memory, then drain | reclaim + buddy **merging** as blocks free in order |
| 8 | Scheduler + memory | counter + average run while short jobs churn | scheduling *and* allocation pressure at once |
| 9 | **Shell** | the interactive shell as the only initial process | see §4 — fork/exec/wait/job-control live |
| 10 | Two guessing games | two `guess` processes | **process switching**: Tab flips focus; each keeps its own input/output |
| 11 | Spawn tree | a parent that FORKs children | parent→child relationships in the MLFQ/process rows |
| 12 | String I/O demo | OUTS/INS string process | type a **name** (text, not just digits) + Enter; it echoes a greeting |
| 13 | Filesystem demo | a process that creates a file, FSYS write/read | prints `HI!` read back **from disk**; press `d` to watch the disk view change |

Modes 6–8 use `ScheduleStaggeredLoads`: the churn jobs are injected one at a time during the
run (not all up front), which is what makes the allocator visibly cycle.

---

## 3. The dashboard — reading each panel

```
┌─────────────────────────┬────────────────────────────────────────────────┐
│ Program (User)          │ Kernel / OS                                    │
│  user instruction stream │  kernel/OS instruction stream                  │
├─────────────┬───────────┴──────────────┬─────────────────────────────────┤
│ MLFQ table  │ Buddy tree / Disk view   │ Screen (focused process I/O)    │
├─────────────┴──────────────────────────┴─────────────────────────────────┤
│ Registers (EAX..EDI + flags, changes highlighted)                        │
├──────────────────────────────────────────────────────────────────────────┤
│ Memory-map bar (proportional, per-owner colors)                          │
├──────────────────────────────────────────────────────────────────────────┤
│ Run stats + status footer                                                │
└──────────────────────────────────────────────────────────────────────────┘
```

- **Program (User) / Kernel (OS)** — two instruction streams side by side, split by privilege.
  User-mode program instructions on the left; the OS/kernel ISA (scheduler, syscalls, paging,
  FS) on the right. This is the whole point of the project: you can *watch the OS run as code*.
- **MLFQ table** — one row per live process: PID, MLFQ priority level (0 highest … 3 lowest),
  ticks used at that level, and state (Ready/Blocked/Zombie). Demotion and the periodic boost
  are visible as rows move between levels.
- **Buddy tree** (default) — the buddy allocator reconstructed from the kernel bitmap +
  process table: which blocks are split, free, or owned (labeled by owner). Press **`d`** to
  swap this panel for the **Disk view** (see §6).
- **Screen** — the focused (foreground) process's output, plus the input line you are typing.
  Only **one** process is "focused" at a time; **Tab** cycles focus. In **canvas mode** (when
  the latest output string contains newlines, e.g. snake) this panel shows that single frame
  in place instead of a scrolling log.
- **Registers** — EAX, EBX, ECX, EDX, ESI, EDI and the flags. Values that changed since the
  previous step are highlighted. (Only these 6 GP registers are shown.)
- **Memory-map bar** — a proportional bar of the whole machine memory, colored per owner, so
  you can see fragmentation and reclaim at a glance.
- **Run stats / status** — instruction count, branch-predictor stats, observational cycles,
  and the current mode/pacing.

### Key bindings

| Key | Action |
|-----|--------|
| `a` | **Auto-run** — free-run at the pacing delay |
| `s` | **Single-step** — advance one step per press |
| `→` | Step forward (at the live edge) / move forward through history |
| `←` | **Scrub back** through history (replay of past snapshots — *not* reverse execution) |
| `Tab` | Cycle **focus** to the next live process |
| `0`–`9` | Append a digit to the input buffer |
| text | (in string-input modes) type characters into the input buffer |
| `Enter` | Submit the typed input to the focused process's stdin (as an int or a string, per what it's waiting on) |
| `Backspace` | Clear the input buffer |
| `o` | Toggle showing per-process program I/O |
| `d` | Toggle the middle panel between the **Buddy tree** and the **Disk (FS) view** |
| `Ctrl-C` | tty-style: send **SigTerm** to the foreground process (job control) |
| `Ctrl-Z` | tty-style: send **SigStop** to the foreground process (job control) |
| `q` | Quit the run (back to the menu) |

**Focus model:** the dashboard auto-focuses the first live process and advances focus when
the focused one terminates. Keyboard input only reaches the *focused* process — this is what
makes a shell in the foreground receive your keystrokes, and a backgrounded job not.

**History scrubbing** replays immutable per-step snapshots (`FrameHistory`); it does not undo
execution. `←`/`→` move the *view*; the emulator only advances at the live edge.

---

## 4. The Shell (menu option 9) — full usage

Option 9 boots a **real Unix-style shell** written in the machine's own ISA. It is the most
capable interactive surface, and the least self-explanatory, so this section is exhaustive.

### 4.1 Setup at launch

Before the shell process starts, `RunShell` populates the filesystem (boot-time only) with a
`/bin` directory of command programs and a sample text file:

```
/bin/ls  /bin/cat  /bin/rm  /bin/mkdir  /bin/echo  /bin/help  /bin/edit
/bin/counter  /bin/average  /bin/guess  /bin/snake
/note                       ← text file: "hello from the filesystem"
```

The shell is then loaded with **4096 bytes of DATA memory** — larger than a normal process —
because `exec` preserves the process's `RequiredMemory`, and a program it execs into (snake in
particular, with its grid + render buffer) needs that room.

### 4.2 The basic loop

**Focus the shell first (Tab), then type.** The shell:

1. drains any finished background jobs (prints `done ` for each),
2. prints the `$ ` prompt,
3. reads a command line (INS),
4. dispatches builtins, or forks a child that `exec`-by-paths your command.

There is **no `PATH` and no current directory** in v1 — use **absolute paths**:

```
$ /bin/help
$ /bin/ls /
$ /bin/echo hi there
$ /bin/cat /note
$ /bin/counter
$ /bin/snake
```

A command that doesn't resolve to a file prints `?`.

### 4.3 Arguments (argv)

The whole command line is tokenized on spaces. **Token 0 is the program path**; the rest
become `argv`. A launched program starts with **`EAX = argc`** and **`EBX = argv base`**
(a virtual pointer to an array of virtual string pointers). `argv[0]` is the command as typed.

- `/bin/echo hi there` → argc=3, prints `hi` then `there` (echo skips argv[0]).
- `/bin/ls /` → argc=2, lists the root directory. `/bin/ls` alone defaults to `/`.
- `/bin/cat /note` → prints the file contents in chunks until EOF.
- `/bin/rm /path`, `/bin/mkdir /path` → single-arg FS operations.
- `/bin/edit /path` → a line editor: type lines, a lone `.` on its own line ends input. Each line
  (plus a newline) is appended to the file. This is how you author a source file inside the OS —
  the first piece of the in-OS write→compile→run toolchain (a self-hosted `/bin/as` assembler is
  the eventual companion). Example: `/bin/edit /hi.txt`, type a few lines, `.`, then `/bin/cat /hi.txt`.

Programs that ignore arguments simply ignore EBX.

### 4.4 Foreground vs background

Append **` &`** to run a command in the **background**:

```
$ /bin/counter &          ← forks, records the job, re-prompts immediately
$ /bin/snake &            ← runs snake unfocused (steer it after `fg 1`)
```

- **Foreground** (no `&`): the shell focuses the child and `WAIT`s for it — you interact with
  the child, and the prompt returns when it exits.
- **Background** (`&`): the shell records the child's PID in a jobs table (max 8) and returns
  to the prompt at once. When a background job finishes, the next prompt cycle reaps it and
  prints `done `.

> **Caveat:** a *busy-spinning* background job competes with the shell for the CPU (both demote
> to the lowest MLFQ level), so the prompt can feel starved. Background jobs that **block on
> I/O** (like a program waiting for input) leave the shell responsive.

### 4.5 Job-control builtins

These are shell-internal (they act on the jobs table; they are **not** `/bin` programs), and
are checked *before* fork/exec. Job numbers are **1-based** indices into the jobs table.

| Builtin | Effect |
|---------|--------|
| `jobs` | print the PID of each active background job |
| `kill <n>` | send **SigTerm** to job `n` (terminate) |
| `stop <n>` | send **SigStop** to job `n` (suspend; it stays in the table) |
| `bg <n>` | send **SigCont** — resume job `n` in the background (unfocused) |
| `fg <n>` | send **SigCont**, focus job `n`, and `WAIT` for it — bring it to the foreground |

Example session:

```
$ /bin/snake &
$ jobs
<pid>
$ fg 1            ← snake comes to the foreground; arrow keys steer it
   (Ctrl-Z)       ← suspends it (SigStop)
$ bg 1            ← resumes it in the background
$ kill 1          ← terminates it
```

### 4.6 tty signals

While a foreground job runs, **Ctrl-C** sends **SigTerm** and **Ctrl-Z** sends **SigStop** to
it — exactly like a real terminal. These are delivered by the hardware to the *focused* PID
(no "killer" process, so the kernel skips the usual return-value delivery). This is how you
stop a runaway foreground `/bin/counter` or suspend `/bin/snake`.

### 4.7 What the shell demonstrates in the panels

- The **Kernel/OS stream** shows FORK, the FSYS exec dispatch, WAIT/REAP, and the scheduler
  running as ISA while you type.
- The **MLFQ table** gains a row for each forked child; background jobs persist, foreground
  jobs come and go.
- Pressing **`d`** during shell FS commands (`ls`, `cat`, `rm`, `mkdir`) shows the **disk view**
  updating as the filesystem changes.

---

## 5. Snake (`/bin/snake`) — controls

Launch from the shell: `/bin/snake` (foreground) or `/bin/snake &` then `fg 1`.

- **Arrow keys** steer (non-blocking INPOLL — the snake keeps moving if you don't press).
- **`q`** quits.
- Eat food (`*`) to grow (`O`); hitting a wall (`#`) or yourself ends the game (`GAME OVER`).
- **Ctrl-C** kills it, **Ctrl-Z** suspends it — it's a normal foreground job.

The grid is small (8×8) **on purpose**: the grid + render buffer is a 5-page working set and
the frame pool only has 4 frames, so a larger board thrashes (page eviction on every tick). At
8×8 it stays responsive-but-slow. Each tick the whole grid is emitted as one newline-containing
OUTS string, which the Screen panel's **canvas mode** renders in place as a 2D board.

---

## 6. Disk (filesystem) view — press `d`

`d` swaps the middle Buddy panel for a reconstruction of the on-disk filesystem
(`FsDiskView`):

- **Superblock stats** — block count, free count, root-dir block.
- **Block-allocation map** — which file-blocks are allocated vs free.
- **Directory tree** — a snapshot of directories/files.

It is **cache-first**: it reads each block from the OS write-back cache when present, so it
shows the *true* current state including writes that haven't been flushed to the backing store
yet. The bridge only rebuilds it when an FS-related OS routine runs (or once at boot), so it's
cheap. Best used with menu option 13 or the shell's FS commands.

---

## 7. Implementation notes (how it's put together)

The visualizer is deliberately decoupled from the emulator: **the OS/hardware fire events; the
visualizer only observes.** Nothing in `Visualization/` mutates hardware except through the
explicit key-callback seams.

### 7.1 Data flow

```
Hardware (fires events)
   └─▶ HardwareEventBridge (subscribes to 9+ events)
          └─▶ VisualizerModel (pure data snapshot)
                 ├─▶ SpectreDashboard   (live TUI; owns the run loop)   ← interactive
                 └─▶ PlainTextRenderer   (deterministic text)            ← used by tests
```

- **`VisualizerModel`** is a pure snapshot: instruction history (tagged by privilege), a
  register snapshot (diffed against the previous step for highlighting), MLFQ rows, free-block
  ranges, the reconstructed `BuddyTree`, an optional `DiskView`, per-process output buffers,
  the focused-process index, and run stats. The bridge **replaces** (never mutates) the
  process-table / free-block / buddy-tree objects on each event, which is what makes
  `FrameHistory` snapshots safe to store and replay.
- **`HardwareEventBridge`** is the only subscriber to hardware events. It updates the model and
  owns the `Pacer`. It rebuilds the `DiskView` only on FS OS-routines (`IvtFsSyscall` /
  `IvtFsOp` / `IvtCacheOp`) plus once at boot, so the disk reconstruction cost is paid only
  when the FS actually changes.
- **`SpectreDashboard`** reads the model directly and owns the interactive run loop: it steps
  the emulator (which never blocks — keystrokes arrive through the dashboard's own key loop),
  redraws per the `DetailLevel` stride, and drives `hw.RaiseOutputComplete` on each program
  output. It's also where `ScheduleStaggeredLoads` queues churn processes and where the
  `ForegroundSignal` (Ctrl-C/Ctrl-Z) wiring lives.

### 7.2 Panels (reconstruction, not bookkeeping)

The panels are **reconstructed from kernel memory**, not maintained as side state — this keeps
them honest (they show what the OS actually did):

- **`BuddyHeapView`** rebuilds the allocator tree from the kernel bitmap at
  `OsLayout.BuddyBitmapOffset` (bit=1 → free; node *i* → bit *i*−1) plus a process-table scan to
  label allocated blocks by owner. (It derives free space from the reconstructed tree, fixing
  an old leaf-only scan that under-reported free internal nodes.)
- **`FsDiskView.ReadDisk(hw)`** returns an immutable `Snapshot` of superblock/block-map/dir-tree
  by reading blocks **cache-first** (OS write-back cache, then `Bin`).

### 7.3 Input, focus, and history

- **`InteractionController`** is pure: `HandleKey(ConsoleKey) → bool` (true = consumed). It has
  **no** hardware side effects; all mutations go through injected callbacks (`toggleIo`,
  `cycleFocus`, `submitInput`, `submitStringInput`, `submitKey`, `toggleDisk`) or through
  `FrameHistory`. To add a command key, extend the controller + wire a callback.
- **Focus** is a *hardware* concept (`activeProcess`, set by `SETFOCUS` / `Hardware.SetFocus`),
  not an OS-memory field — "which process the keyboard/screen belongs to" is a device concern.
  The dashboard cycles it (Tab) and auto-advances it on termination.
- **`FrameHistory`** is a capped ring of immutable per-step snapshots enabling `←`/`→` replay.
  Safe because the bridge replaces referenced objects rather than mutating them.

### 7.4 Canvas mode (snake / any 2D output)

`SpectreDashboard.BuildScreen` checks whether the latest OUTS string contains `\n`; if so it
renders **that single frame** as a 2D block in place, instead of the joined output log. This is
what turns snake's whole-grid-per-tick OUTS into an animated board with no special "graphics"
API — it's just a string with newlines.

### 7.5 Testing seam (no TTY)

The live TUI needs a real terminal, so tests never construct `AnsiConsole`. Instead they use
the headless seams `SpectreDashboard.RenderSnapshot(console, maxSteps)` and `RenderSummary`
over a `NoColors` Spectre console wrapping a `StringWriter`, which catch markup/layout
exceptions without a TTY. The interactive TUI is verified manually with
`dotnet run --project CSharpOSConsole`. `ConsoleVisualizer` (the older coordinator) wraps
`VisualizerModel + HardwareEventBridge + PlainTextRenderer + Pacer` and is what most emulator
tests use to get deterministic text output.

---

## 8. Quick reference — file map

| File | Role |
|------|------|
| `Program.cs` | Entry point, mode menu, `Run*` launchers, `RunShell` |
| `Programs.cs` | The program library (one method per assembled image; `/bin` programs, Shell, Snake) |
| `Visualization/SpectreDashboard.cs` | Live TUI + run loop + canvas mode |
| `Visualization/HardwareEventBridge.cs` | Event → model updates; owns the Pacer + DiskView rebuild |
| `Visualization/VisualizerModel.cs` | Pure data snapshot read by renderers |
| `Visualization/InteractionController.cs` | Pure key handling |
| `Visualization/FrameHistory.cs` | Immutable per-step snapshots for scrub |
| `Visualization/BuddyHeapView.cs` | Buddy-tree reconstruction |
| `Visualization/FsDiskView.cs` | Filesystem reconstruction (cache-first) |
| `Visualization/PlainTextRenderer.cs` | Deterministic text renderer (tests) |

See also the per-directory `CLAUDE.md` files in `CSharpOSConsole/` and
`CSharpOSConsole/Visualization/` for Grep-navigable section indexes.
</content>
</invoke>
