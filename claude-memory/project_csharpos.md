---
name: project-csharpos
description: "Learning project — emulated OS in C#: Hardware/OS/Process; MLFQ scheduler fully in ISA, 4 priority levels, I/O boost, periodic global boost; 493 passing tests — paging roadmap COMPLETE. Now on VisualizerImprovements branch."
metadata: 
  node_type: memory
  type: project
  originSessionId: 9c82e5d3-a44f-4eeb-813b-ee8a85186f7d
---

Building an emulated Operating System in C# as a learning project. Located at C:\Users\Tex\OneDrive\Documents\VisualStudio2026\CSharpOS. Solution file: CSharpOS.slnx.

**Goal:** Learn OS internals by building a working emulation. User wants to discover problems naturally — do not pre-correct design decisions.

**Solution projects (4 total):**
- `CSharpOS` — core library (Hardware, Assembler, OS interfaces/base, OsLayout)
- `BasicOSPlugin` — OS personality plugin (BasicOS, OsRoutines, trap providers); loaded dynamically
- `CSharpOSConsole` — console host; uses OsPluginLoader, `--os-plugin <path>` flag
- `OSTests` — xUnit tests; references both CSharpOS and BasicOSPlugin directly

**Folder structure (CSharpOS core):**
- `CPU/` — Hardware.cs, Hardware.Types.cs, Instruction.cs, InstructionFunctions.cs
- `Assembler/` — Assembler.cs, Assembler.Types.cs
- `OS/` — OperatingSystem.cs, IOperatingSystem.cs, OsLayout.cs, ITrapProvider.cs, OsPluginLoader.cs
- `Processes/` — Process.cs
- `Enums/` — RegisterName.cs, PrivilegeLevel.cs, ProcessState.cs, WaitReason.cs
- `Structs/` — MemoryRange.cs, Trap.cs
- `Events/` — InstructionExecutedArgs.cs, MemoryWrittenArgs.cs, ContextSwitchArgs.cs, InvalidInstructionArgs.cs, ProgramOutputArgs.cs

**Folder structure (BasicOSPlugin):**
- `BasicOS.cs` — concrete OS; uses reflection (CollectTraps) to discover ITrapProvider implementations
- `OsRoutines.cs` — ISA emitters for all OS routines (unchanged from before)
- `Traps/IretTrapProvider.cs`, `Traps/LoadBoundsTrapProvider.cs`, `Traps/StoreBoundsTrapProvider.cs` — public sealed classes implementing ITrapProvider

**Key classes:**
- **Hardware** (partial) — core emulation. `(int memorySize, RegisterName[] registerNames, IOperatingSystem os)`.
- **OperatingSystem** (abstract) — boots ISA image, loads processes (via ISA allocator), reads OS memory for liveness. Concrete: `BasicOS`.
- **BasicOS** — supplies `KernelImage` (I/O dispatch) and `BuildTraps()`; `OsMemorySize = OsLayout.TotalSize`, `BuildOsImage = OsRoutines.BuildOsImage`.
- **OsLayout** — data-section layout: IVT→code (CodeBase=36)→data (DataBase=2048)→process table→free-range table→pending queue. TotalSize≈3524.
- **OsRoutines** — `BuildOsImage` packs all routines after IVT; `resume_mlfq` is the shared scheduling tail.

**Register set (24 registers, 96-byte register file):**
EAX, EBX, ECX, EDX, ESI, EDI, ESP, EBP, EIP, EFLAGS, CS, DS, ES, FS, GS, SS, R8–R15.

**Hardware constants (current):**
- `KernelTrapInfoOffset = 96` (after 96-byte register file)
- `KernelHeaderSize = 112` (KernelTrapInfoOffset + KernelTrapInfoSize=16)
- `KernelStackSize = 176` (was 64; now holds the syscall trap frame at its base — see two-mode note below)
- `SchedulerInstructionCount = 30`
- `ProcessEntrySize = 160` — per-process table entry:
  - 0–95: register file
  - 96: Level, 100: State, 104: WaitReason, 108: ProgramAddress, 112: ProgramSize
  - 116: RequiredMemory, 120: RequiredStackSize, 124: TotalSize
  - **128: Priority (MLFQ level 0–3), 132: TicksUsed (ticks at current level)**

**Opcodes:** MOV_REG_REG(0x01), MOV_REG_IMM(0x02), MOV_REG_IMM16(0x03), LOAD(0x05), STORE(0x06), ADD(0x10), SUB(0x11), MUL(0x12), DIV(0x13), CMP(0x14), INC(0x15), DEC(0x16), JMP(0x20), JZ(0x21), JNZ(0x22), CALL(0x23), RET(0x24), JS(0x25), JNS(0x26), OUT(0x30), IN(0x31), HLT(0x32), IRET(0x33), SAVEREGS(0x40), LOADREGS(0x41), SETLAYOUT(0x42), OSRET(0x43).

**OsLayout data section (DataBase=2048):**
- +0: ProcessCountOffset, +4: CurrentIndexOffset, +8: FreeRangeCountOffset, +12: PendingCountOffset
- **+16: BoostTimerOffset (MLFQ countdown), +20: QuantumTableOffset (4×4-byte thresholds)**
- +36: ProcessTableOffset (MaxProcesses=8, ProcessEntrySize=160)
- After process table: FreeRangeTableOffset (MaxFreeRanges=16, 8 bytes each)
- After free ranges: PendingQueueOffset (MaxPending=8)

**MLFQ scheduler (✅ COMPLETE 2026-06-22 — 272 tests green):**

4 priority levels (0=highest, 3=lowest). New processes start at priority 0. Fully in ISA (OsRoutines.cs). Constants: `QueueCount=4`, `BoostInterval=20`.

- **Quantum thresholds** (QuantumTable, seeded in `OperatingSystem.SeedOsData`): L0=1 tick, L1=2 ticks, L2=4 ticks, L3=255 (never demote).
- **ContextSwitch** (EmitContextSwitch):
  1. Save interrupted process regs.
  2. Increment TicksUsed. If priority < 3 and TicksUsed >= QuantumTable[priority]: priority++, TicksUsed=0.
  3. Decrement BoostTimer. If timer reaches 0: iterate all non-Terminated entries (exit when i >= count via Jz+Js on count-i), reset Priority=0, TicksUsed=0, reset timer to BoostInterval.
  4. Jump to `resume_mlfq`.
- **resume_mlfq** (EmitResumeMlfq): outer loop P=0..3; inner round-robin from ECX+1 for a Ready process with Priority==P. First found wins. If nothing found at any level → idle (currentIndex=-1).
- **I/O boost**: EmitWakeBody sets Priority=0 and TicksUsed=0 after marking process Ready, so I/O-bound processes always return to the top queue.
- **OperatingSystem.LoadProcess**: seeds Priority=0, TicksUsed=0 for new processes.
- **Register usage in MLFQ routines**: ECX=currentIndex, EDI=count, ESI=scan counter; R8–R15 used for MLFQ state (TicksUsed, Priority, threshold, BoostTimer address, loop variables).
- **Bug caught + fixed (2026-06-22)**: Boost loop off-by-one — original `Js("done")` exits only when `i > count`; when `i == count` (count-i=0, SF=0) it ran a ghost iteration past the process table into the free-range region. Fixed by adding `Jz("done")` before `Js("done")` so the loop exits when `i >= count`.

**Assembler.Build() idempotency (✅ fixed):** `Build()` now copies `code`→local list and `labels`→local dict; never mutates instance state. Calling Build() twice returns identical bytes.

**OS scheduling model:**
- All scheduling/allocation/lifecycle runs as ISA code in the OS memory region entered via IVT.
- Hardware.Run: Privileged → step in-flight OS routine; else dispatch one pending interrupt, or Schedule when !processRunning, or step current process + dispatch ContextSwitch at quantum.
- `OsRet` atomically commits pending context (LOADREGS staged it), sets level, updates processRunning flag.
- GetProgramBase()=0 in Privileged (absolute addressing).

**Per-process memory layout (since `HardwareUpdate` branch, 2026-06-27):** `[program][memory][user stack][kernel stack]` — **no per-process kernel section anymore**. `TotalSize = ProgramSize + RequiredMemory + RequiredStackSize + KernelStackSize`.
- `KernelStackSize` is now **176** (was 64): the syscall trap frame (96-byte saved regs + 16-byte trap-info) sits at the **base** of the kernel stack; handler stack space above it.
- The syscall handler is **shared OS code** in the OS region (`EmitSyscall` in OsRoutines.cs, reached via IVT slot `IvtSyscall`), NOT copied per-process. `KernelImage`/`BuildKernelImage` removed from the plugin contract; `BuildOsImage` includes the shared handler. KernelImageSlot disk-staging gone.
- ESP initialized to top of user stack. Mode saved/restored in entry.Level.

**Privilege levels (COLLAPSED to TWO on `HardwareUpdate` branch, 2026-06-27 — was three):** `PrivilegeLevel` enum is now just **User + Kernel** (no `Privileged` member). Atomicity is no longer a privilege level — it's the **hardware interrupt-enable flag** (`Hardware.InterruptsEnabled()`). OS routines run in **Kernel mode with interrupts masked** (atomic, never preempted); the shared syscall handler runs in **Kernel mode with interrupts enabled** (preemptible — a blocking syscall survives a context switch because its trap frame lives on the per-process kernel stack). Run-loop gate changed `if (level==Privileged)` → `if (!InterruptsEnabled())`. IVT grew 14→**15 slots** (added `IvtSyscall`), `CodeBase` 56→**60**, OS-data ProcessTable 8244→**8240**. Tests: `InterruptFlagTests.cs` added; ~460 (459) passing. Branch NOT yet merged to master; docs (ISA.md/OS-Architecture.md) + test terminology updated to two-mode.

**Trap system:** Hardware evaluates traps before each instruction. BasicOS trap table: IRET/LOAD/STORE restricted in user mode. FakeOS has no traps.

**Non-blocking I/O:** per-device inputByDevice (dict) + outputBusyDevices (set). Device id == process-table index. Wake routines take device index in EAX (passed by DispatchOsRoutine). Two entry stubs (wakeInput/wakeOutput) each set reason in EBP, jump to shared wk_body.

**Console I/O — foreground/focus model (since 2026-06-26, replaced the old multi-window IPC):** one shared screen bound to a **focused** process. Hardware `activeProcess` (`SetActiveProcess`/`GetActiveProcess`); `RaiseInputInterrupt(value)` routes to the focused process's stdin device (`FocusedInputDevice`). The `SpectreDashboard` has a **Screen** panel (focused output buffer + typed input line), owns focus (auto-focus first live, **Tab** cycles, advances on terminate), drives `RaiseOutputComplete`, and collects input via the key loop (digits + Enter → number to focused process; **Enter no longer steps — Right does**). The named-pipe per-process windows (`ConsoleWindowTerminal`/`TerminalHost`/`IProcessTerminal`/`ProcessIoRouter`/`--terminal`) were DELETED. Three visualizer tiers: Minimal/Normal/Verbose.

**Visualizer refactor (✅ 2026-06-24 — Spectre.Console dashboard):** The old 464-line `ConsoleVisualizer` God-class was split into a render-model + pluggable-renderer architecture under `CSharpOSConsole/Visualization/`:
- `VisualizerModel` (pure data: current process/privilege, instruction history tagged by privilege, MLFQ process rows, free blocks, buddy tree, register snapshot+previous for diff, transition log, run-stat counters) updated by `HardwareEventBridge` (owns the 9 hw event subscriptions). Renderers are decoupled from Hardware.
- `IVisualizerRenderer` with two impls: `PlainTextRenderer` (streaming deterministic text — reproduces the ORIGINAL output, used by tests via `ConsoleVisualizer` coordinator which keeps the same public ctor) and `SpectreDashboard` (live single-window TUI). `NoOpRenderer` used by the dashboard's bridge (dashboard reads the model directly).
- `SpectreDashboard` (Spectre.Console 0.57.1, PackageReference on CSharpOSConsole only — NOT core/tests, but transitively usable in OSTests) panels: split **Program (user) | Kernel/OS** instruction streams with privilege switch markers; **MLFQ** process table (priority+ticks) + ready-queues; **buddy-allocator tree** (`Spectre.Console.Tree`); registers with change highlighting; **linear memory-map bar** (proportional, per-owner colors); run stats; status footer. `Program.cs` `Run()` is now generalized; the dashboard owns the run loop.
- **Time-travel:** `FrameHistory` (capped ring of immutable per-instruction snapshots — safe because the bridge REPLACES, never mutates, the process-table/free-block/buddy-tree objects) + `InteractionController` (pure `HandleKey` core: `a` auto, `s` step, `←/→` scrub back/forward (executes at live edge), `o` I/O, `q` quit). Backward = view replay over frames, NOT reverse CPU execution.
- `Disassembler` (CSharpOS/CPU/, inverse of Assembler, covers ALL opcodes incl. OS-support SAVEREGS/LOADREGS/SETLAYOUT/OSRET + bitwise — old Decode showed those as `??? XX`). `BuddyHeapView` (CSharpOSConsole/Visualization/) reconstructs the buddy tree from the kernel bitmap (bit=1 FREE, bit=0 used/split; node i→bit i-1) + process table (allocated blocks pinned by (base,size), labeled by owner). **Fixed a latent bug**: the old free-map did a leaf-only bitmap scan, under-reporting free space held at internal nodes (a fully-free heap showed `(none)`); now derived from the reconstructed tree.
- **Run modes:** menu now has 8. 1–5 = original (per-process windows). 6 Memory churn, 7 Fill & drain heap, 8 Scheduler+memory — these stagger-load short `Programs.BusyThenHalt` jobs of varied sizes ({128,512,1024}) via `SpectreDashboard.ScheduleStaggeredLoads`, use an EMPTY terminals dict (no windows, I/O mirrored in dashboard) to show buddy alloc/free churn. Console MemorySize bumped 16384→32768.
- **Testing seam:** `SpectreDashboard.RenderSnapshot(IAnsiConsole, maxSteps)` + `RenderSummary` are headless render paths; tests render to a plain (NoColors) Spectre console over a StringWriter to catch markup/layout exceptions without a TTY. The live TUI itself still needs MANUAL verification (`dotnet run --project CSharpOSConsole`, modes 4–8); it can't be driven headlessly. Approved plan: `C:\Users\Tex\.claude\plans\zesty-strolling-rivest.md`.

**Tests:** 454 passing (as of 2026-06-26, after the full 4-step roadmap: Bin disk, foreground/focus, and process spawning fork/exec/wait/setfocus). Spawning test files: PositionIndependenceTests, ForkTests, ExecTests, WaitTests, ShellTests. **ISA addressing is now position-independent** (ESP, saved EIP, CALL/RET return addresses are program-base-relative). Privileged OS routines use a scratch stack (`OsLayout.PrivilegedStackTop`) so the buddy allocator/free are CALL/RET subroutines (`alloc_sub`/`free_sub`). `OsLayout.DataBase` is now 8192. Test files include: HardwareTests, InstructionTests, SyscallTests, OsContextSwitchRoutineTests, OsSchedulingRoutineTests, OsAllocatorRoutineTests, MlfqTests (41 tests), MissingCoverageTests, ConsoleVisualizerTests, ProcessIoRouterTests, OsPluginLoaderTests, TrapProviderTests, DeviceTableTests, FdTableTests, BinTests, DiskInstructionTests, etc. New folder `CSharpOS/Disk/` holds `Bin.cs` (flat block store backing the disk block device). See [[project-spawning-filesystem-plans]] for the disk's as-built details.

**Test hygiene (2026-06-23):** Swept all test files to replace hardcoded numeric literals with named constants. Key changes: local `const byte EAX=0`, `EIP=8` etc. removed from 5 test classes → replaced with `hw.GetRegisterOffset(RegisterName.X)` (byte-address use) and `(byte)RegisterName.X` (register-index use). Boundary addresses in MissingCoverageTests that depended on `KernelHeaderSize`/`KernelStackSize` are now computed from those constants. Tests are now resilient to register file size changes, field offset changes, and OS layout shifts.

**Test shared constants + resilience (2026-06-24):** Centralized cross-cutting test literals into the `Test` static class (OSTests/TestSupport.cs): `WordSize=4`, `RegisterFileBytes()`, `ZeroFlagMask`/`SignFlagMask` + `ZeroFlag(hw)`/`SignFlag(hw)`, and `ReadWord(hw,addr)`/`WriteWord(hw,addr,val)` (the per-file little-endian word helpers in ~10 files now delegate to these). Added `Test.MachineWithHeap(int heapBytes) => OsLayout.TotalSize + heapBytes`: **every test that loads/seeds the OS region now sizes its machine relative to `OsLayout.TotalSize`** instead of a bare literal (was 8192/16384), so growing the OS or kernel never silently outgrows a test's machine, and bumping a machine size is one edit. Bare instruction tests (FakeOS, no OS image) keep literal sizes like 512/1024 — not OS-coupled. Kernel-layout assertions (HardwareTests, SyscallTests, MissingCoverageTests, TrapProviderTests) derive expected sizes from `program.Length`/`process.RequiredMemory`/`process.RequiredStackSize` + `KernelHeaderSize`/`KernelImage.Length`/`KernelStackSize`; per-file layout dims lifted to local consts (`LayoutUserMemory`, `LayoutProgramAddress`, etc.). Self-contained arithmetic literals (e.g. `5+7=12`, opcode encodings, jump offsets) intentionally left inline — they ARE the test. Production `* 4` word-size convention left untouched; `Test.WordSize` is test-side only.

**MlfqTests** (OSTests/MlfqTests.cs — 41 isolation tests):
- Demotion: L0 demotes after 1 tick; L1 no-demote below threshold; L1 demotes at threshold; L3 never demotes.
- Priority ordering: higher priority runs first; round-robin within same level; demoted process outranked by fresh process at higher level.
- I/O boost: WakeInput/WakeOutput reset priority to 0; spurious wake leaves priority unchanged.
- Periodic boost: resets all non-Terminated to priority 0; skips Terminated slots; resets timer to BoostInterval; non-expired timer does not boost.
- **Edge cases added by audit agent**: boost off-by-one (1/2/full-table ghost write), idle path skips boost timer, OsLayout overlap assertions (QuantumTable end vs ProcessTableOffset), LoadProcess seeds MLFQ fields for new and recycled slots, Block/Halt preserve MLFQ fields, Wake cross-reason mismatch is no-op, boosted process found at level 0 on next switch, ConsoleVisualizer regression guards for shifted ProcessTableOffset and FreeRangeTableOffset.

**Dead-code sweep (✅ COMPLETE 2026-06-24 — 346 tests green):**
- Removed `Process.ModeStateAddress` (field written once in `Hardware.cs`, never read).
- `InstructionFunctions.cs`: `0x1F` shift-count mask → `const int ShiftCountMask = 0x1F` (x86 32-bit shift semantics; used in `Shl` and `Shr`).
- `ConsoleVisualizer.cs`: `& 1` / `& 2` EFLAGS checks → local `const int ZeroFlagMask = 1` / `const int SignFlagMask = 2`.
- Intentionally kept: reflection-loaded types (`BasicOS`, `IretTrapProvider`, `LoadBoundsTrapProvider`, `StoreBoundsTrapProvider`); `* 4` word-size idiom and buddy-tree `2` literals in `OsRoutines.cs` (established conventions).

**Dynamic OS plugin loading (✅ COMPLETE 2026-06-23 — 301 tests green):**

- `ITrapProvider` (in CSharpOS core): single method `Trap GetTrap()`. Any new trap handler implements this.
- `OsPluginLoader.Load(string dllPath, TextWriter log)` (in CSharpOS core): reflects over a .dll, finds the first non-abstract `OperatingSystem` subclass that has a `(TextWriter)` constructor (checked via `GetConstructor` — NOT `Activator.CreateInstance` blindly, which throws `MissingMethodException` on wrong-signature ctors). Throws `InvalidOperationException` if none found.
- `BasicOS.CollectTraps()`: uses `Assembly.GetExecutingAssembly().GetTypes()` to discover all non-abstract `ITrapProvider` implementations, instantiates each, and collects their traps. Replaces the old hardcoded `BuildTraps()` list.
- `CSharpOSConsole/Program.cs`: accepts `--os-plugin <path>` CLI arg; defaults to `BasicOSPlugin.dll` next to the exe (via `AppContext.BaseDirectory`). Displays plugin path in the menu header.
- **Lesson from implementation**: after a trap fires, Hardware enters Privileged mode (OS routine entered). Tests that fire multiple traps sequentially on one hardware instance will see the second/third trap condition return false (they gate on `PrivilegeLevel.User`). Use a separate Hardware instance per trap check in tests.
- **Lesson**: when `OSTests.dll` is loaded by `OsPluginLoader`, it finds `TrappingOS : OperatingSystem` with constructor `(List<Trap>, TextWriter)` — wrong signature. Must check constructor explicitly before instantiating.

**Why:** Learning project to understand OS internals.
**How to apply:** Follow the user's design, don't pre-correct. Ask before each decision point during implementation. [[feedback-codestyle]] [[feedback-design-decisions]]
