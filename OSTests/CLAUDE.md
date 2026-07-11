# OSTests Quick Reference

xUnit suite for CSharpOS + BasicOSPlugin (references both directly). **737 tests across 65 files.**
Goal of this file: pick the right test file *without* grepping the whole suite. Find the subsystem
below, open that file, then grep the `[Fact]`/`[Theory]` method name inside it (names are full
sentences, e.g. `Write_ThenReopenAndRead_RoundTripsTheData`).

---

## Where to Find a Test (subsystem → file)

| If you're testing… | File(s) |
|--------------------|---------|
| **FS block layer** (fs_format/alloc/free/chain via `IvtFsOp`) | `FsBlockAllocatorTests` |
| **FS directories** (fs_hash/root_dir/lookup/insert/remove) | `FsDirectoryTests` |
| **FS nested dirs + path resolve** (fs_mkdir/path_resolve) | `FsPathTests` |
| **FS open/close cores** (fs_open_core/close_core) | `FsOpenCloseTests` |
| **FS byte read/write cores** (fs_read_core/write_core) | `FsReadWriteTests` |
| **FS exec-by-path + boot auto-format + FsImage** (Inc 6) | `FsExecTests` |
| **FS maintenance** (unlink/mkdir/readdir, FreeCount, single-open, pin) | `FsMaintTests` |
| **FSYS syscall end-to-end** (through a live scheduler) | `FsSyscallTests` |
| **Boot-from-FS** (LoadProcess installs to /bin, FS-backed spawn, fork/EXEC-slot, Bin persistence) | `FsBootTests` |
| **FS write-back cache** (cache_* via `IvtCacheOp`) | `CacheManagerTests` |
| **FBREAD/FBWRITE instructions** | `FileBlockInstructionTests` |
| **Bin disk** (host block store: store/load/free/file-blocks) | `BinTests` |
| **Per-process fds + device wait queue** | `FdTableTests` |
| **MLFQ scheduling** (demote/priority/boost) | `MlfqTests` |
| **Schedule/Block/Wake/Halt routines** (isolation) | `OsSchedulingRoutineTests` |
| **Context-switch routine** (isolation) | `OsContextSwitchRoutineTests` |
| **Buddy allocator + free reclaim** | `OsAllocatorRoutineTests` |
| **SeedOsData math** (heap sizing, header, MLFQ seed) | `OsSeedDataTests` |
| **Hardware.Run dispatch branches** | `OsRunLoopTests` |
| **FORK / EXEC(slot) / WAIT / EXIT / zombies** | `ForkTests`, `ExecTests`, `WaitTests` |
| **Job control: KILL / signals** (TERM/KILL/STOP/CONT + foreground Ctrl-C/Ctrl-Z) | `KillTests` |
| **Non-blocking reap** (REAP / waitpid-WNOHANG) | `ReapTests` |
| **Catchable signals** (SIGACTION/SIGRETURN, handler save/restore + re-deliver) | `SignalTests` |
| **Repeated/chained exec** (`RunUntilIdle` harness; alternation-flake regression) | `FsExecChainTests` |
| **Fork propagates kernel-written memory** (INS/OUTS dirty-bit carries across fork) | `ForkMemoryProbeTests` |
| **Shell program + SETFOCUS** (fork+exec+wait loop, job-control builtins, **auto-shell scripted-input demo**) | `ShellTests` |
| **`/bin` command programs** (ls/cat/rm/mkdir/echo/help) | `FsBinProgramsTests` |
| **Exec with parsed argv** (command line → argv[]; EAX=argc/EBX=argv) | `FsArgvExecTests` |
| **Assembler tables** (mnemonic→opcode/shape, register index; host-side) | `AsmTableTests` |
| **`/bin/as` self-hosted assembler** (assemble source→image, all shapes + labels/branches, run) | `FsAssemblerTests` |
| **`/bin/edit` line editor** (author a source file) | `FsToolchainTests` |
| **Snake game** (render/input/collision + the `cache_flush`/ECX scheduler regression) | `SnakeTests` |
| **Disk-view reconstruction** (`FsDiskView` snapshot: superblock/block-map/tree) | `FsDiskViewTests` |
| **Focus / foreground I/O model** | `HardwareFocusTests` |
| **Position-independent addressing** | `PositionIndependenceTests` |
| **Paging** (demand fault-in, frames, swap, COW) | `PagingTests` |
| **IN/OUT trap → syscall handler, IRET, privilege** | `SyscallTests` |
| **Interrupt-enable flag / atomicity** | `InterruptFlagTests` |
| **OS-support instrs** (SAVEREGS/LOADREGS/SETLAYOUT/OSRET) + kernel absolute addr | `OsSupportInstructionTests` |
| **ALU / MOV / flags** | `InstructionTests` |
| **LOAD/STORE/CMP and other newer instrs** | `NewInstructionTests` |
| **DREAD / DWRITE instructions** | `DiskInstructionTests` |
| **Disassembler** (one case per opcode) | `DisassemblerTests` |
| **Branch predictor** (2-bit BHT) | `BranchPredictorTests` |
| **Hardware basics** (ctor, memory round-trips) | `HardwareTests` |
| **Device table / char devices / disk block device** | `DeviceTableTests` |
| **Events** (InstructionExecuted, MemoryWritten, ProgramOutput, …) | `EventTests` |
| **BasicOS load + liveness queries** | `OperatingSystemTests` |
| **OsPluginLoader reflection** | `OsPluginLoaderTests` |
| **Computer host end-to-end** | `ComputerTests` |
| **Struct/data-object basics** (Process/MemoryRange/Trap/RegisterName) | `DataObjectTests` |
| **Trap providers** (IRET only) + CollectTraps reflection | `TrapProviderTests` |
| **MMU protection faults** (out-of-bounds user access kills the process; size guard) | `MemoryProtectionTests` |
| **Kernel-mediated user-memory access via paging** (OUTS/INS + FSYS r/w on DATA-region/COW pages; `Hardware.UserToPhysical`, `ensure_user_page`) | `KernelUserMemoryTests` |
| **Console visualizer / dashboard** (incl. foreground-follow focus, input echo, one-output-per-line) | `ConsoleVisualizerTests`, `SpectreDashboardTests` |
| **Process display names in the panels** (DisplayName → FS file by FirstBlock → `pN`) | `ProcessNamingTests` |
| **Visualizer internals** (history, frames, buddy view, model; frame-pacing predicate + speed ladder live in `FrameHistoryTests`) | `FrameHistoryTests`, `VisualizerModelTests`, `BuddyHeapViewTests` |
| **Risky edge cases / uninitialised state** | `EdgeCaseTests`, `EdgeCaseScenarioTests` |

---

## Shared Test Infrastructure (`TestSupport.cs`)

`internal static class Test` — used by nearly every file. Prefer these over hand-rolled setup:

| Member | Purpose |
|--------|---------|
| `Test.MinMachineSize` | `TotalSize + 4096`; smallest machine that boots + runs simple procs |
| `Test.MachineWithHeap(heapBytes)` | `TotalSize + heapBytes`; sizes relative to the OS region so layout growth never starves a test |
| `Test.FullHeapMachineSize` | `TotalSize + MinBlock*MaxProcesses`; exactly MaxProcesses leaves |
| `Test.NewHardware(memSize, os)` | Hardware with the full `RegisterName` set |
| `Test.AllRegisters()` | `Enum.GetValues<RegisterName>()` |
| `Test.ReadWord(hw, addr)` / `WriteWord(hw, addr, val)` | little-endian 32-bit memory access |
| `Test.Word(op,b1,b2,b3)` | encode one 4-byte instruction |
| `Test.ZeroFlag(hw)` / `SignFlag(hw)` | EFLAGS bit 0 / bit 1 as 0/1 |
| `Test.WordSize` (=4), `ZeroFlagMask`, `SignFlagMask` | ISA constants mirrored for tests |

Test doubles (also in `TestSupport.cs`): `FakeOS` (records the calls Hardware makes — `OsMemorySize=0`, so it does **not** boot an OS image or seed data) and `TrappingOS` (exercises the abstract base with a caller-supplied trap table).

---

## Conventions (so a new test matches the suite)

- **ISA-routine isolation tests** drive a routine directly via `hw.RunOsRoutineSynchronously(slot, eaxArg)` against a **hand-seeded** process table / OS memory, then read results back with `Test.ReadWord`. This avoids a full scheduler run. Used by all `Os*RoutineTests`, `Mlfq`, `Cache`, and every `Fs*Tests` isolation file.
- **FS cores are reached two ways:** the `IvtFsOp` selectors (`Hardware.FsOp*`, absolute path/buffer + explicit proc index) for isolation, or the `FSYS` instruction (`Hardware.Fsys*`, user pointer, current process) for end-to-end. Isolation files use `IvtFsOp`; `FsSyscallTests`/`FsExecTests` use a live scheduler.
- **End-to-end tests** build a real `BasicOS`, load a program (`hw.Disk.Store(image)` → `os.LoadProcess(new Process(slot, mem, stack))`), subscribe to `ProgramOutput` (calling `RaiseOutputComplete` in the handler), then loop `hw.Run()` while `os.HasProcesses` (cap the loop, e.g. 40000). See `FsExecTests`/`FsSyscallTests`.
- **FS boot note:** since Inc 6, `BasicOS` auto-formats on boot, so end-to-end FS tests no longer need `FsOpFormat`. Isolation tests on a bare `FakeOS` (which does not boot an OS image) still format by hand.
- **FSYS read/write buffers may be anywhere mapped** (Phase 3 rectification): `fsy_read`/`fsy_write` now translate the user buffer through the page table page-by-page (`user_word_addr`), so a demand-paged/swapped DATA-region buffer round-trips — see `Fsys_WriteThenRead_WithDataRegionBuffers_RoundTripsData`. Two constraints remain: (a) **FSYS path pointers** (open/exec/unlink/mkdir/readdir) are still translated with flat `ProgramAddress+ptr` math, so keep **paths** in a program-image (RAM-home) page; (b) the `IvtFsOp` cores (`fs_read_core`/`fs_write_core`) still take **absolute** buffers, so isolation tests driving them directly must pass RAM-home addresses. Kernel-mediated OUTS/INS access (`Hardware.UserToPhysical`) is likewise paging-correct now — see `KernelUserMemoryTests`.
- **Cover every case, including edges** (project rule): the `*EdgeCase*`, `MissingCoverage`, and per-feature "…_ReturnsMinusOne / _Empty / _PastEnd" tests exist for this. Add the failing/edge case, don't skip it.
- **Reusing ONE machine for several programs?** Use `Test.RunUntilIdle(hw, os)` between them, not a bare `while (os.HasProcesses) hw.Run()`. `os.HasProcesses` flips false the instant the last process is marked Terminated — while the exit-teardown ISA routine is still running with interrupts masked. Stopping there and calling `os.LoadProcess` again injects into that mid-teardown state, which corrupted on alternate runs (the old "FSYS-exec alternation flake" — root-caused as a harness contract, not an OS bug; regression: `FsExecChainTests`). Single-program tests that build a fresh machine and dispose it are unaffected; their private `while (os.HasProcesses)` loops are fine.

---

## Big files (grep the method name; no line index kept — names are stable, line numbers drift)

`MlfqTests` (36), `OsAllocatorRoutineTests` (24), `ConsoleVisualizerTests` (30), `SyscallTests` (21),
`PagingTests` (19), `OsSeedDataTests` (24), `MissingCoverageTests` (27), `TrapProviderTests` (20),
`EdgeCaseScenarioTests` (19), `FrameHistoryTests` (24), `BinTests` (35) are the largest — each still one
concern per file, so a `[Fact]`-name grep lands directly on the case.
