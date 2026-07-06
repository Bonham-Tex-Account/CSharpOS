# CSharpOS Operating System Architecture

This document describes how the CSharpOS operating system is structured and how its pieces fit together. Source of truth: the files in `CSharpOS/OS/`, `CSharpOS/CPU/Hardware.cs`, `CSharpOS/Disk/Bin.cs`, `BasicOSPlugin/`, and `CSharpOS/Processes/`.

---

## Table of Contents

1. [Overview](#overview)
2. [OS memory layout](#os-memory-layout)
   - [OS region](#os-region)
   - [Per-process memory layout](#per-process-memory-layout)
   - [Process table entry layout](#process-table-entry-layout)
3. [Boot and image-build flow](#boot-and-image-build-flow)
4. [The hardware run loop](#the-hardware-run-loop)
5. [Interrupt vector table (IVT) and dispatch model](#interrupt-vector-table-ivt-and-dispatch-model)
6. [Branch predictor](#branch-predictor)
7. [MLFQ scheduler](#mlfq-scheduler)
8. [Buddy memory allocator](#buddy-memory-allocator)
9. [I/O device table and per-process file descriptors](#io-device-table-and-per-process-file-descriptors)
10. [Bin disk (block device)](#bin-disk-block-device)
11. [Trap system](#trap-system)
12. [Virtual memory and demand paging](#virtual-memory-and-demand-paging)
    - [Address translation (MMU)](#address-translation-mmu)
    - [Page table seeding](#page-table-seeding)
    - [Frame pool and LRU eviction](#frame-pool-and-lru-eviction)
    - [Swap backing for DATA pages](#swap-backing-for-data-pages)
    - [Copy-on-write fork](#copy-on-write-fork)
    - [Paging subroutines](#paging-subroutines)
13. [The ISA filesystem](#the-isa-filesystem)
    - [On-disk layout](#on-disk-layout)
    - [Write-back buffer cache](#write-back-buffer-cache)
    - [Block allocator and free-chaining](#block-allocator-and-free-chaining)
    - [Directories and path traversal](#directories-and-path-traversal)
    - [Open-file table and file syscalls](#open-file-table-and-file-syscalls)
    - [Exec-by-path and boot auto-format](#exec-by-path-and-boot-auto-format)
14. [Process lifecycle](#process-lifecycle)
    - [Boot creation (Spawn)](#boot-creation-spawn)
    - [Scheduling and context switching](#scheduling-and-context-switching)
    - [I/O: blocking and waking](#io-blocking-and-waking)
    - [Fork](#fork)
    - [Exec](#exec)
    - [Wait](#wait)
    - [Exit / HLT](#exit--hlt)
    - [Invalid-instruction fault](#invalid-instruction-fault)
15. [Dynamic OS plugin loading](#dynamic-os-plugin-loading)
16. [How the pieces fit together (end-to-end)](#how-the-pieces-fit-together-end-to-end)

---

## Overview

CSharpOS emulates a complete operating system on a custom 32-bit CPU (the `Hardware` class). The distinguishing design choice is that **the OS scheduler, memory allocator, process-control routines, virtual memory manager, and filesystem are all compiled to the CSharpOS ISA and run on the same CPU** — not implemented as C# methods called from the outside. The C# layer is responsible only for:

- Setting up the OS image in memory at boot.
- Loading initial programs (driving the ISA allocator).
- Fielding device interrupts from the host (keyboard input, output completion).
- Providing an event feed for the visualizer.

Everything else — scheduling, buddy allocation, context-switching, fork/exec/wait, the kernel-mode I/O syscall path, demand paging, and the filesystem (buffer cache, block allocator, directories, file syscalls) — executes as ISA instructions in the OS memory region.

---

## OS memory layout

### OS region

At boot, `Hardware.ReserveOsMemory(OsLayout.TotalSize)` places the OS at address 0. All process memory is allocated above it, so user and kernel code can never directly address the OS image.

```
Address 0
+------------------------------------------------------------+
| Interrupt Vector Table (IVT)                               |  92 bytes (23 × 4)
+------------------------------------------------------------+  offset 0x000 (= 0)
| OS code (assembled ISA routines)                           |  starts at OsLayout.CodeBase = 92
|   ContextSwitch, Halt, InvalidInstruction, WakeInput,      |
|   WakeOutput, Block, Schedule, BuddyAlloc,                 |
|   Fork, Exec, Wait, Spawn, Syscall, PageFault, WakeKey,    |
|   CacheOp, FsOp, FsSyscall, EnsureUserPage                 |
|   + shared exit_body, alloc_sub, free_sub,                |
|   release_frames, flush_frames, zero_swap_slots,          |
|   pair_resolve, resolve_cow, cow_share,                   |
|   cache_find/get/dirty/write_through/pin/unpin/discard/   |
|     flush, fs_format/alloc_block/free_block/chain_*,      |
|   fs_hash/root_dir/dir_lookup/dir_insert/dir_remove,      |
|   fs_extract_component/path_resolve/mkdir,                |
|   oft_alloc/resolve_parent/create_file/open_core/         |
|     close_core, oft_from_fd/fs_grow_chain/fs_read_core/   |
|     fs_write_core, fs_exec_core, fs_load_image,            |
|   exec_next_token/exec_build_argv (Shell §2 argv),         |
|   reap, kill_core, teardown_reap, sigreturn, sig_copy      |
|     (Shell §2.5 job control + catchable signals),          |
|   user_word_addr, resume_mlfq                              |
+------------------------------------------------------------+  up to OsLayout.DataBase = 24576
| OS data section (runtime-seeded by C#)                     |  starts at OsLayout.DataBase = 24576
|   Scheduler state header (48 bytes)                        |
|   Process table  (8 × 208 = 1664 bytes)                   |
|   Buddy bitmap   (8 × 4 = 32 bytes)                        |
|   Privileged scratch stack (64 bytes)                      |
|   Page tables    (8 processes × 128 PTEs × 4 = 4096 bytes)|
|   Frame table    (4 frames × 32 = 128 bytes)               |
|   Frame pool     (4 frames × 256 = 1024 bytes)             |
|   Zero page      (256 bytes, always zero)                  |
|   Swap scratch   (256 bytes, fork transfer buffer)         |
|   COW partner table (8 × 4 = 32 bytes)                    |
|   Cache header + slot table (12 + 13 × 276 = 3600 bytes)  |
|   FS result + scratch + path scratch (4+40+60 = 104 bytes)|
|   Open-file table (8 × 24 = 192 bytes)                     |
|   FS open + read/write scratch (8+12 words = 80 bytes)    |
|   User-page-translate + EnsureUserPage scratch (2+2 words)|
|   FSYS read/write page-chunk wrapper scratch (6 words)    |
|   FSYS-exec argv staging (131 words, Shell §2)            |
|   FS program-install staging (20+63 words, Phase 4)       |
|   Job-control region (KillSig/SaveIndex/NoDeliver, §2.5)  |
|   Signal save area (8 × 100 = 800 bytes, JC-E)            |
+------------------------------------------------------------+
| (total OS region)                                          |  OsLayout.TotalSize = 37860 bytes
+------------------------------------------------------------+  <-- heap start (process allocations)
```

**OS data section fields.** Offsets below are given **relative to `OsLayout.DataBase` (= 24576)** — the absolute address is `DataBase + offset`. (The doc used absolute addresses until repeated `DataBase` bumps kept invalidating them; relative offsets only shift when an *earlier* field's size changes. Ground truth: `dotnet run --project .claude/skills/os-facts/dump -- layout`.)

| Field | Offset (from DataBase) | Size | Description |
|-------|-----------------------|------|-------------|
| ProcessCount | +0 | 4 | Number of process-table slots in use (high-water mark). |
| CurrentIndex | +4 | 4 | Index of the currently running process; −1 when CPU is idle. |
| BuddyHeapStart | +8 | 4 | Absolute address where the managed heap begins (= OsMemorySize). |
| BuddyHeapSize | +12 | 4 | Buddy heap size in bytes (largest power of 2 ≤ available RAM). |
| BoostTimer | +16 | 4 | Countdown ticks until the next MLFQ global priority boost. |
| QuantumTable | +20 | 16 | 4 × 4-byte tick thresholds per MLFQ level: [1, 2, 4, 255]. |
| BuddyMinBlock | +36 | 4 | Minimum allocatable block size in bytes (default: 256). |
| BuddyLevels | +40 | 4 | Buddy tree depth = log2(HeapSize / MinBlock). |
| NextPid | +44 | 4 | Monotonic PID counter; incremented each time a process is created. Starts at 1 (0 = "no process"). |
| ProcessTable | +48 | 1664 | 8 entries × 208 bytes (see entry layout below). |
| BuddyBitmap | +1712 | 32 | 8 × 32-bit words; 1 bit per buddy tree node (1=free, 0=used/split). |
| PrivilegedStack | +1744 | 64 | Scratch stack for CALL/RET within the atomic OS routines. |
| PageTables | +1808 | 4096 | 8 processes × 128 PTEs × 4 bytes. PTE ≥ 0: resident frame base. PTE = −1: unmapped (a user access is a protection fault — see [Address translation (MMU)](#address-translation-mmu)). PTE = −2: non-resident RAM-home. PTE ≤ −3: non-resident swap-backed. PTE ≤ −4096: copy-on-write share. |
| FrameTable | +5904 | 128 | 4 frames × 32 bytes each (see frame table layout below). |
| FramePool | +6032 | 1024 | 4 frames × 256 bytes. Frame f lives at `FramePoolBase + f * PageSize`. |
| ZeroPage | +7056 | 256 | Always-zero OS scratch page; `DWRITE` source when zeroing a swap slot. |
| SwapScratch | +7312 | 256 | Transfer page for fork's per-slot deep-copy (DREAD src → scratch, DWRITE scratch → dst). |
| CowPartners | +7568 | 32 | 8 × 4 bytes. `CowPartner[i]` = the partner process-table slot index, or −1 when no COW share is active. |
| CacheHeader | +7600 | 12 | Clock counter, flush timer, and the `IvtCacheOp`/`IvtFsOp` result slot (4 bytes each). |
| CacheSlotTable | +7612 | 3588 | 13 cache slots × 276 bytes each (see [Write-back buffer cache](#write-back-buffer-cache)). |
| FsResult | +11200 | 4 | Result of the last `IvtFsOp`/`IvtFsSyscall` dispatch. |
| FsScratch | +11204 | 40 | 10 words of directory-layer scratch (name/hash/type/first/dir/entryBlock/freeBlock/argA/argB + one spare). |
| FsPath | +11244 | 60 | Path-resolve loop state (cursor, current dir, last-component flag) + a 12-word extracted-component buffer. |
| Oft | +11304 | 192 | Open-file table: 8 entries × 24 bytes (see [Open-file table and file syscalls](#open-file-table-and-file-syscalls)). |
| FsOpen | +11496 | 32 | `fs_open_core` spill scratch (absPath/flags/proc/entryAddr/first/size/dirBlock/entryOffset). |
| FsRw | +11528 | 48 | `fs_read_core`/`fs_write_core` spill scratch (fd/buf/count/proc/oft/curBlock/remaining/charInBlock/bufPtr/copied/counter). |
| PageXlate | +11576 | 8 | `user_word_addr` spill scratch (offset, page) — one virtual word address being translated at a time. |
| EnsureUserPage | +11584 | 8 | C#↔ISA handoff for `IvtEnsureUserPage`: `Hardware.UserToPhysical` writes IsWrite before dispatch and reads Result after the routine returns. |
| FsWrap | +11592 | 24 | `fsy_read`/`fsy_write` page-chunk loop scratch (fd/ptr/remaining/copied/isWrite/chunk) — walks a user buffer one page-chunk at a time, translating each chunk via `user_word_addr` before delegating to `fs_read_core`/`fs_write_core`. |
| FsArgv | +11616 | 524 | **Exec argv staging (Shell §2):** the captured command line (`FsArgvCmd`, 63 words) plus one extracted token buffer and tokenizer bookkeeping. `fsy_exec` copies the whole command line here via `user_word_addr` *before* teardown, then `exec_next_token`/`exec_build_argv` split it into `argv`. |
| InstallPath / InstallBuf | +12140 / +12220 | 80 / 252 | Phase 4 program-install staging: an absolute path buffer (`"/bin/p<seq>"`) and a one-block transfer buffer used by `LoadProcess` to write a program's bytes into the filesystem in block-sized chunks. |
| JobCtl | +12472 | 12 | **Job control (Shell §2.5):** `KillSig` (the signal being delivered), `KillSaveIndex` (scratch for the temporary `CurrentIndex` swap in `kill_core`), and `KillNoDeliver` (set for terminal-initiated Ctrl-C/Ctrl-Z signals that have no killer process to receive a return value). |
| SignalSave | +12484 | 800 | **Catchable signals (JC-E):** 8 per-process slots of 100 bytes (register file + privilege level). On delivery `kill_core` snapshots the interrupted context here and redirects the process to its handler; `SIGRETURN` restores it. Saving the level too is essential — see [Job control and signals](#job-control-and-signals). |

**Frame table entry layout (32 bytes, one entry per physical frame):**

| Byte offset | Field | Description |
|-------------|-------|-------------|
| 0 | Occupied | 1 = frame holds a page; 0 = free. |
| 4 | OwnerProc | Process-table index of the owning process. |
| 8 | OwnerPage | Virtual page number held in this frame. |
| 12 | Home | For RAM-home pages: physical block-home base (`ProgramAddress + page * PageSize`). −1 for swap-backed frames. |
| 16 | Dirty | 1 = written since loaded; write-back required on eviction. Set by C# `StampFrame` on every write access. |
| 20 | LastUse | LRU stamp (`pageClock` value at last access). Eviction picks the frame with the smallest stamp. |
| 24 | Swap | Bin disk swap slot backing this frame, or −1 for RAM-home frames. |
| 28 | Cow | 1 = read-only copy-on-write share. A write to this frame re-raises a page fault and calls `pair_resolve`. |

### Per-process memory layout

Each process occupies one contiguous block allocated by the buddy allocator. The block is laid out as:

```
[programBase]
+---------------------------------------------------+
| Program image       (ProgramSize bytes)           |
+---------------------------------------------------+
| Process data memory (RequiredMemory bytes)        |
+---------------------------------------------------+
| User stack          (RequiredStackSize bytes)     |
+---------------------------------------------------+
| Kernel stack        (KernelStackSize = 176 bytes) |
|   The syscall trap frame sits at the base:        |
|   [0]    Saved user register file  (96 bytes)     |  KernelSaveAreaOffset = 0
|   [96]   Trap info                 (16 bytes)     |  KernelTrapInfoOffset = 96
|           +0: faulting opcode                     |
|           +4: operand byte-offset in save area    |
|           +8: return IP (user-relative offset)    |
|   (handler stack space above the frame)           |  KernelHeaderSize = 112
+---------------------------------------------------+
[programBase + TotalSize]
```

`TotalSize = ProgramSize + RequiredMemory + RequiredStackSize + KernelStackSize`

There is **no per-process kernel section**: the syscall handler is shared OS code in the OS region (reached via IVT slot `IvtSyscall`). Each process keeps its own kernel stack, whose base holds the trap frame of an in-flight syscall — so a syscall that blocks survives a context switch without clobbering another process's. The shared privileged OS stack (at `OsLayout.PrivilegedStackTop`) is used by the atomic OS routines, which run with interrupts masked and never nest.

### Process table entry layout

Each entry is 200 bytes. Source: `Hardware.ProcessEntry*` constants.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 96 | Register file | 24 × 4-byte register values. The EIP slot (register index 8, offset 32) holds the saved IP as a program-relative offset (not absolute). |
| 96 | 4 | Level | Saved `PrivilegeLevel` (0=User, 1=Kernel). |
| 100 | 4 | State | `ProcessState` enum: Ready(0), Blocked(1), Terminated(2), Zombie(3). |
| 104 | 4 | WaitReason | `WaitReason` enum: None(0), Input(1), Output(2), ChildProcess(3), StringInput(4), KeyInput(5). |
| 108 | 4 | ProgramAddress | Absolute physical address where the program image begins. Set to −1 if allocation failed. |
| 112 | 4 | ProgramSize | Size of the program image in bytes. |
| 116 | 4 | RequiredMemory | Size of the data memory region. |
| 120 | 4 | RequiredStackSize | Size of the user stack. |
| 124 | 4 | TotalSize | Sum of all regions; used by the buddy allocator. |
| 128 | 4 | Priority | MLFQ queue level (0 = highest priority). |
| 132 | 4 | TicksUsed | Context-switch ticks accumulated at the current priority level. |
| 136 | 4 | Pid | Monotonic process identifier (1-indexed). Survives exec; never reused. |
| 140 | 4 | ParentPid | PID of the parent (−1 if spawned at boot with no parent). |
| 144 | 4 | WaitTarget | PID the process is waiting on in `wait()`; −1 otherwise. |
| 148 | 4 | ExitStatus | Exit status written by HLT/EXIT or −1 on a fault. |
| 152 | 4 | DiskSlot | Disk slot index holding this process's program image, or −1 if the process is filesystem-backed (see FirstBlock below and [Boot creation (Spawn)](#boot-creation-spawn)). |
| 156 | 4 | FirstBlock | First data block of the process's program image in the filesystem's block chain, or −1 if the process is slot-backed (`DiskSlot >= 0`). `IvtSpawn` branches on `DiskSlot`: `>= 0` DREADs the disk slot; `< 0` chain-loads the image from `FirstBlock` via `fs_load_image`. |
| 160 | 32 | FdTable | 8 × 4-byte file descriptor → device-id/OFT mappings. fd 0 = stdin, fd 1 = stdout, fd 2–7 hold `OFT index + 1` for open files (0 = free — a zeroed table means no open files, no seeding required). |
| 192 | 4 | Stopped | Job-control stop flag (0/1). Orthogonal to `State`, so even a Blocked process can be stopped; the scheduler (`resume_mlfq`) skips any entry with `Stopped = 1`. Set by `SIGSTOP`, cleared by `SIGCONT`. |
| 196 | 4 | SigHandler | Catchable-signal handler virtual address (0 = none), installed by `SIGACTION`; `SIGTERM`/`SIGINT` are delivered here instead of the default action. |
| 200 | 4 | SigPending | A catchable signal that arrived while the process was already in its handler; re-delivered on `SIGRETURN` (0 = none). |
| 204 | 4 | InHandler | 1 while the process is executing its signal handler, so a second catchable signal defers (sets SigPending) rather than re-entering. |
| **208** | — | *(total)* | `ProcessEntrySize = 208` |

The register-file region (offset 0–95) is the save area written by `SAVEREGS` and read by `LOADREGS`. Because EIP is stored as a base-relative offset, the same saved context works after a `FORK` (the child's copy is at a different physical base but the offset is unchanged).

---

## Boot and image-build flow

1. **Plugin discovery.** `OsPluginLoader.Load(dllPath, log)` loads the OS plugin DLL, finds the `OperatingSystem` subclass, and constructs it with `new BasicOS(log)`.

2. **`BasicOS` construction.** `CollectTraps()` scans the plugin assembly via reflection for all `ITrapProvider` implementations and collects their `Trap` structs. The base class `OperatingSystem(List<Trap> traps, TextWriter log)` stores them.

3. **Hardware construction.** `new Hardware(memorySize, registerNames, os)` registers the disk as a block device at `DiskDeviceId = 256`, then calls `os.AttachHardware(hw)`.

4. **`AttachHardware`.** Loads the collected traps into `hw.trapTable`, calls `hw.ReserveOsMemory(OsLayout.TotalSize)`, and writes the OS image to address 0 via `hw.WriteBytes(0, BuildOsImage(0))`.

5. **`BuildOsImage`.** `OsRoutines.BuildOsImage()` assembles all OS ISA routines into one `Assembler`, calls `Build(OsLayout.CodeBase)` to resolve labels against their absolute addresses, and returns a `byte[]` of length `OsLayout.TotalSize`. The IVT slots at the front are filled with the entry-point addresses of each routine. The COW partner table (at `OsLayout.CowPartnerBase`) is initialized to all −1 (no partner) directly in the image, so even hand-seeded test images see a correct initial state without calling `SeedOsData`.

6. **`SeedOsData`.** The C# side writes runtime values into the data section: heap parameters, the MLFQ quantum table, the initial buddy bitmap (root node free), and the initial PID counter.

7. **`OnBooted` (post-boot hook).** A virtual hook, called once the image is written and seeded; the base class does nothing. `BasicOS` overrides it to format the filesystem (`fs_format`, via `IvtFsOp`) — but only if the disk's superblock doesn't already carry the filesystem magic, so a disk loaded from a persisted `.bin` keeps its files instead of being reformatted. See [Exec-by-path and boot auto-format](#exec-by-path-and-boot-auto-format).

8. **Process loading.** `os.LoadProcess(process)` (called per program) stages the program's backing — installed into the filesystem (`FirstBlock` set, `DiskSlot = −1`) if `UsesFilesystemBoot` is true (`BasicOS` opts in), otherwise a legacy disk slot (`DiskSlot` set, `FirstBlock = −1`) — then runs `hw.RunOsRoutineSynchronously(IvtSpawn, entry)` to allocate and load the process in ISA, then seeds the fd table and reads back the assigned PID from C# (no per-process kernel image to copy — the syscall handler is shared OS code). See [Boot creation (Spawn)](#boot-creation-spawn) for the full flow.

---

## The hardware run loop

`Hardware.Run()` is called in a tight loop by the host until `!os.HasProcesses`. Each call does exactly one of:

```
if (!InterruptsEnabled())
    StepInstruction()           // continue an in-progress atomic OS routine

else if (TryDispatchPendingInterrupt())
    (wake routine entered — will run on next ticks)

else if (!processRunning)
    DispatchOsRoutine(IvtSchedule)

else
    StepInstruction()           // run the current process (User or Kernel)
```

`StepInstruction` fetches the 4-byte instruction at `instructionPointer`, advances IP by 4, executes it (which may fire a trap and redirect IP), and counts the instruction. After `SchedulerInstructionCount = 30` counted instructions, it dispatches `IvtContextSwitch`. Instructions that trap (invalid opcode, OUT/IN in user mode, process-control syscalls) do **not** count toward the quantum.

Atomic OS-routine instructions (those running with interrupts masked) are never counted and never preempted.

---

## Interrupt vector table (IVT) and dispatch model

The IVT occupies the first 92 bytes of the OS region (`IvtSlotCount = 23` slots × 4 bytes, `IvtSize = 92`). Each slot holds the absolute address of the corresponding OS routine. Hardware reads slot `s` as `ReadWord(0 + s * 4)` and jumps there in Kernel mode. Slot `IvtSyscall` is special: `EnterKernel` jumps there directly **without** masking interrupts (so the syscall handler stays preemptible), while all other dispatched routines — including `IvtFsSyscall`, which `FSYS` reaches via the ordinary atomic `DispatchOsRoutine` path (the same mechanism as `FORK`/`EXEC`/`WAIT`/`EXIT`, not `EnterKernel`) — mask interrupts and run atomically.

`SETFOCUS` (0x38) is intentionally C#-only (`Hardware.SetFocus`) — it has no IVT slot or ISA routine, because "focused process" is a hardware-side field with no OS-memory representation.

`Hardware.DispatchOsRoutine(int slot)` (or the overload with an EAX argument):
1. `CaptureInterruptedContext()` — snapshots the live register file plus the current IP (as a base-relative offset) into `trapFrame`; records the current privilege level in `interruptedLevel`.
2. Reads the routine address from the IVT.
3. Fires the `OsRoutineEntered` event.
4. Masks interrupts (the routine runs atomically), then sets `level = Kernel`.
5. Sets `instructionPointer` to the routine address.
6. Sets `trapTaken = true` so the current step does not advance the quantum.

| Slot | Name | Dispatched by |
|------|------|---------------|
| 0 | ContextSwitch | Hardware timer (every 30 instructions) |
| 1 | Halt | `HLT` instruction or `EXIT` |
| 2 | InvalidInstruction | Unknown opcode, user-mode DREAD/DWRITE/DLEN/FBREAD/FBWRITE, OS trap conditions, MMU protection faults |
| 3 | WakeInput | Input interrupt with a waiter |
| 4 | WakeOutput | Output-complete interrupt |
| 5 | BlockInput | Process blocked on input device |
| 6 | BlockOutput | Process blocked on output device |
| 7 | Schedule | Run loop idle (no process running) |
| 8 | Allocate | Synchronous allocation from C# (RunOsRoutineSynchronously) |
| 9 | Fork | `FORK` instruction |
| 10 | Exec | `EXEC` instruction |
| 11 | Wait | `WAIT` instruction |
| 12 | Spawn | `LoadProcess` via RunOsRoutineSynchronously; DREADs a disk slot or chain-loads from the filesystem via `fs_load_image`, depending on `DiskSlot`/`FirstBlock` |
| 13 | Syscall | `EnterKernel` (IN/OUT/OUTS/INS/INK/INPOLL trap); jumped to directly, NOT dispatched — interrupts stay enabled |
| 14 | PageFault | C# MMU (`TryTranslateData`) when a user data access hits a non-resident PTE |
| 15 | WakeKey | Raw-key interrupt with a process waiting in `INK` |
| 16 | CacheOp | `IvtFsOp`'s block/directory/file routines and the periodic context-switch flush hook (buffer-cache control) |
| 17 | FsOp | Internal filesystem selector (block allocator, directories, path resolve, open/close cores) — used by `FsImage` and by tests; not directly reachable from user mode |
| 18 | FsSyscall | `FSYS` instruction (dispatched atomically, like `FORK`/`EXEC`/`WAIT`/`EXIT` — not via `EnterKernel`) |
| 19 | EnsureUserPage | `Hardware.UserToPhysical` (C#), used by the `FSYS` read/write wrapper (`user_word_addr`) to fault in or COW-resolve one user page synchronously, outside the normal per-instruction MMU path |
| 20 | Reap | `REAP` instruction — non-blocking reap of a terminated child (dispatched atomically, like `FORK`) |
| 21 | Kill | `KILL` instruction, and the terminal `ForegroundSignal` interrupt (Ctrl-C/Ctrl-Z) — signal a process (TERM/KILL/STOP/CONT/INT); delivers a catchable signal to an installed handler or runs the default action |
| 22 | SigReturn | `SIGRETURN` instruction — return from a catchable-signal handler: restore the saved context (register file + level) and resume |

The dead `IvtDiskLoad` slot (a leftover from an earlier C#-driven load path, never dispatched) was removed and slots 9–18 renumbered down by one; `IvtEnsureUserPage` was then added as slot 19. The job-control work (see [Job control and signals](#job-control-and-signals)) then appended slots 20 (`IvtReap`), 21 (`IvtKill`), and 22 (`IvtSigReturn`), bringing `IvtSlotCount` to 23.

---

## Branch predictor

The hardware contains a 2-bit saturating branch history table (BHT) implemented in `CSharpOS/CPU/BranchPredictor.cs`. The predictor is **observational only** — it never changes which branch is actually taken — and applies only to user-mode conditional branches (`JZ`/`JNZ`/`JS`/`JNS`).

**BHT parameters:** 64 entries, indexed by `(instructionAddress / 4) % 64`. Each entry is a 2-bit saturating counter (0 = strongly not-taken, 3 = strongly taken). The initial state is 0 for all entries.

**Scoring:** `Hardware.RecordBranch(bool taken)` is called by each conditional-branch handler. It is a no-op for Kernel-mode code and for OS-routine code (interrupts masked, since those are the scheduler's own loops). For user-mode branches it:
1. Predicts from the BHT entry for `currentInstructionAddress`.
2. Updates the counter (increment on taken, decrement on not-taken, clamped 0–3).
3. Fires the `BranchPredicted` event (carrying address, predicted value, actual value, hit/miss).
4. On a misprediction, adds `MispredictPenalty = 3` observational cycles to the hardware cycle counter.

**Cycle counter (`Hardware.GetCycles()`):** counts one cycle per executed non-atomic instruction, plus the misprediction penalty per miss. It is independent of the MLFQ quantum, which remains instruction-count based (every 30 counted instructions).

**Access:** `Hardware.GetBranchPredictor()` returns the predictor for tests and the visualizer.

---

## MLFQ scheduler

Four priority queues (levels 0–3, where 0 is highest). All scheduling logic is ISA code, primarily in `EmitContextSwitch`, `EmitSchedule`, and `EmitResumeMlfq` in `OsRoutines.cs`.

**Queue selection (`resume_mlfq`):** scan from level 0 to level 3. Within each level, scan all process-table entries in round-robin order starting one past the current index (wrapping around). Pick the first entry in state Ready **and not `Stopped`** (job-control stop is enforced here — see [Job control and signals](#job-control-and-signals)) at the current level. If no entry is found at any level, set `CurrentIndex = −1` and call `OSRET` with no staged context (idle). The round-robin start index is read from ECX, so any routine that `Call`s a register-clobbering subroutine (e.g. `EmitContextSwitch`'s periodic `cache_flush`) before falling into `resume_mlfq` must reload ECX from `CurrentIndexOffset` first.

**Context switch (every 30 instructions):**
1. Save the current process's registers with `SAVEREGS`.
2. Increment `TicksUsed`.
3. If not at the lowest level and `TicksUsed >= QuantumTable[level]`: increment `Priority`, reset `TicksUsed = 0` (demotion).
4. Decrement `BoostTimer`. If it reaches 0: reset all non-terminated processes to `Priority = 0`, `TicksUsed = 0`, and reset `BoostTimer = BoostInterval = 20`.
5. Call `resume_mlfq` to pick the next process.

**I/O boost:** `EmitWakeBody` resets the woken process to `Priority = 0`, `TicksUsed = 0`.

**Quantum thresholds** (from `SeedOsData`):

| Level | Threshold (ticks) |
|-------|------------------|
| 0 | 1 |
| 1 | 2 |
| 2 | 4 |
| 3 | 255 (effectively unlimited) |

A "tick" here is one context-switch event, not one instruction. Each context-switch event may or may not demote the process depending on its accumulated count.

---

## Buddy memory allocator

The buddy allocator manages physical RAM above the OS region. It is implemented entirely in ISA (`EmitBuddyAlloc`, `EmitAllocSub`, `EmitBuddyFree` in `OsRoutines.cs`) and runs in Kernel mode with interrupts masked (atomic).

**Bitmap representation:** a compact binary tree where each node is one bit. Bit = 1 means the block is FREE; bit = 0 means it is either allocated or split. Node numbering is 1-indexed: root = node 1, left child of n = 2n, right child = 2n + 1. Node i maps to bit (i−1) in the bitmap, stored in word `(i−1) / 32`, bit-in-word `(i−1) % 32`. The bitmap fits in `BuddyBitmapWords = 8` 32-bit words (supports up to 255 nodes = 8 levels).

**Initialization (SeedOsData):**
- `BuddyHeapStart = OsMemorySize` (immediately above the OS region).
- `BuddyHeapSize` = largest power of 2 ≤ (total RAM − OS region).
- `BuddyLevels = log2(HeapSize / MinBlock)`.
- Bitmap zeroed; bit 0 (root) set to 1 (the entire heap is one free block).

**Allocation (`alloc_sub`, called by IvtAllocate, IvtSpawn, IvtFork, IvtExec):**
1. Compute `targetLevel`: start at level 0 (blockSize = HeapSize); halve until `blockSize / 2 < TotalSize` or `targetLevel == BuddyLevels`.
2. Scan from `targetLevel` up toward the root for any free node. If none found at any level: set `ProgramAddress = −1` (failure).
3. If the free node is above `targetLevel`: split down, marking left children as free, then allocate the left child at `targetLevel`.
4. Compute physical address: `HeapStart + (nodeIndex − 2^targetLevel) * blockSize`.
5. Write `ProgramAddress` into the process-table entry.

**Deallocation (`free_sub`, called by exit_body and IvtExec before re-exec):**
1. Derive the allocated node from `ProgramAddress`, `TotalSize`, and the heap parameters.
2. Set the node's bit to 1 (free).
3. Merge loop: if the buddy node is also free, clear both, set their parent free, ascend. Repeat until root or buddy is used.

**Default parameters:** `BuddyDefaultMinBlock = 256` bytes. `OsLayout.TotalSize = 37860` bytes. Tests commonly size a machine as `Test.MinMachineSize = OsLayout.TotalSize + 4096` (see `OSTests/TestSupport.cs`); with that sizing the heap starts at `TotalSize` with exactly 4096 bytes available, giving `BuddyLevels = log2(4096 / 256) = 4`. A larger machine yields a larger power-of-2 heap and correspondingly more levels.

---

## I/O device table and per-process file descriptors

`Hardware` maintains a `Dictionary<int, Device>` keyed by device id. Two device types exist:

- **Character device:** owns an input queue (`Queue<int>`), a waiter list (`List<int>` of process indices blocked on input), and an output-busy flag. Character devices are created on demand.
- **Block device:** the `Bin` disk, registered at `DiskDeviceId = 256`.

**Device ids for processes:** by convention, each process's private I/O device has id equal to its process-table slot index. This shim is installed by `LoadProcess` and `IvtFork`.

**File descriptor table:** each process-table entry holds `FdCount = 8` file descriptors starting at `ProcessEntryFdTable` (absolute offset 160). Each fd is a 4-byte word. fd 0 = stdin, fd 1 = stdout (both hold a device id). fd 2–7 are reserved for open files: a nonzero value there holds `OFT index + 1` (0 = free), resolved through the open-file table — see [Open-file table and file syscalls](#open-file-table-and-file-syscalls). `Hardware.FdDevice(fd)` resolves the running process's fd to a device id; `Hardware.FocusedInputDevice()` resolves the foreground process's stdin.

**Focus (foreground process):** `Hardware.activeProcess` is the slot index of the foreground process. Keyboard input from `RaiseInputInterrupt()` (no device argument) is delivered to the focused process's stdin device. The host can cycle focus; `SETFOCUS` (instruction 0x38) updates `activeProcess` by scanning the process table for the target PID.

**I/O flow (character device):**
- **Output:** `Hardware.KernelOutput(value)` resolves fd 1 → device id, checks `OutputBusy`, delivers via `Output(value, deviceId)` (fires `ProgramOutput` event), and sets `OutputBusy = true`. When the host signals output completion via `RaiseOutputComplete(deviceId)`, the interrupt is enqueued; on the next tick, `TryDispatchPendingInterrupt` clears `OutputBusy` and dispatches `IvtWakeOutput`.
- **Input (int, `IN`):** `Hardware.KernelInput(register)` resolves fd 0 → device id, checks the input queue. If empty: records the process as a waiter on that device and calls `BlockCurrent(WaitReason.Input)`. When input arrives via `RaiseInputInterrupt(value, deviceId)`, it is buffered in the device queue; if there is a waiter, `IvtWakeInput` is dispatched with the waiter's process index in EAX.
- **String input (`INS`):** blocks with `WaitReason.StringInput` on a per-device string queue (a full line, not a single value); woken the same way as `Input` via `IvtWakeInput`.
- **Raw key input (`INK`):** blocks with `WaitReason.KeyInput` on a per-device key queue (one raw keycode per keypress, not line-buffered); woken by the dedicated `IvtWakeKey` slot. `INPOLL` reads this same queue without blocking, returning −1 when it is empty.

---

## Bin disk (block device)

`CSharpOS/Disk/Bin.cs` implements a flat, fixed-slot block store, plus a second, independent block-addressed region used by the filesystem.

**Slot region:** the disk's backing store is a `byte[]` of size `slotCount * slotSize`, with a directory of `bool[] occupied` and `int[] lengths` (actual content length per slot, which may be less than `slotSize`).

**Default geometry:** 1088 slots × 1024 bytes. The `Hardware` convenience constructor uses `DefaultDiskSlots + OsLayout.SwapSlotCount` = 64 + 1024 = 1088 total slots. Slots 0–63 hold process program images; slots 64–1087 are the deterministic swap region used by demand paging (8 processes × 128 pages per process).

**C# API (slot region):**
- `Store(byte[] data)` — writes to the first free slot; returns slot index or −1 if full.
- `Store(int slot, byte[] data)` — overwrites a specific slot.
- `Load(int slot)` — returns a fresh copy of the slot's content at its true length.
- `Free(int slot)` — marks the slot empty and zeroes its storage.
- `GetLength(int slot)` — returns the stored byte count (throws if slot is free).

**ISA interface (slot region):** the `DREAD`, `DWRITE`, and `DLEN` instructions (all Kernel-only) delegate to `hw.DiskRead`, `hw.DiskWrite`, and `hw.DiskLength`, which call through to the `Bin` behind the `Device` at `DiskDeviceId`. Addresses passed to `DREAD`/`DWRITE` are absolute (Kernel mode program base = 0).

**File-block region:** a second, independent backing store inside the same `Bin` — fixed-size, block-addressed, with no directory (a block is either all-zero or holds whatever was last written; there is no "free" concept at this layer, only at the filesystem layer above it). Default geometry: `DefaultFileBlockCount = 256` blocks × `DefaultFileBlockSize = 256` bytes (a 64 KiB file space), independent of the 1088 slots above — the two regions never overlap or share addresses.

- `ReadFileBlock(int block)` — returns a fresh copy of the block; **never throws for an unwritten block** (returns zeros — raw block-device semantics).
- `WriteFileBlock(int block, byte[] data)` — `data` must be exactly `FileBlockSize` bytes, else `ArgumentException`.
- `Save(string path)` / `Load(string path)` — persist **only** the file-block region (a `.bin` file with a `0x43534653` "CSFS" magic + geometry header). The slot region (process images, swap) is not persisted — it rebuilds from the loaded programs at boot, and swap is transient by design.

**ISA interface (file-block region):** the `FBREAD`/`FBWRITE` instructions (Kernel-only) delegate to `hw.FileBlockRead`/`hw.FileBlockWrite`, which call `Bin.ReadFileBlock`/`WriteFileBlock` directly (no `Device`/device-id indirection — the filesystem's buffer cache is the layer that manages concurrency and write-back). See [The ISA filesystem](#the-isa-filesystem) for how the cache and block allocator use these two instructions.

---

## Trap system

A `Trap` struct has three fields: `Opcode` (the instruction to guard), `Reason` (a human-readable fault message), and `Condition` (a `Func<Hardware, byte, byte, byte, bool>` that returns true when the trap should fire). Source: `CSharpOS/Structs/Trap.cs`.

Traps are evaluated by `Hardware.EvaluateTraps(opcode, b1, b2, b3)` before any instruction executes. If a trap fires, `TrapInvalidInstruction` is called, dispatching `IvtInvalidInstruction`, and the instruction does not execute.

**Trap registration:** `BasicOS.CollectTraps()` discovers all `ITrapProvider` implementations in the plugin assembly via reflection (`Assembly.GetExecutingAssembly().GetTypes()`). New trap handlers require no manual registration; adding a class that implements `ITrapProvider` is sufficient.

**Current traps registered by `BasicOSPlugin`:**

| Provider class | Opcode guarded | Condition | Fault reason |
|---------------|---------------|-----------|-------------|
| `IretTrapProvider` | `IRET` (0x33) | `PrivilegeLevel == User` | "IRET is a privileged instruction" |

`IretTrapProvider` is the only remaining trap provider. `LoadBoundsTrapProvider` and `StoreBoundsTrapProvider` have been removed — the MMU is now the sole memory-protection mechanism (see [Virtual memory and demand paging](#virtual-memory-and-demand-paging)); a data access outside a process's mapped page extent is a **protection fault**, not a trap-table match.

**Adding a trap:** implement `ITrapProvider` in a class in `BasicOSPlugin` (or the active plugin), returning a `Trap` struct. No other changes are needed.

---

## Virtual memory and demand paging

CSharpOS implements per-process virtual address spaces with demand paging, a shared LRU physical frame pool, Bin-disk swap backing for data pages, and copy-on-write fork. The MMU is in the C# `Hardware` class; the ISA page-fault handler and all paging subroutines are in `OsRoutines.cs` (`EmitPageFault` and six subroutines).

### Address translation (MMU)

**Code (instruction) addresses** are never translated. The CPU fetches from `instructionPointer`, which always holds a physical address.

**Data addresses** (LOAD/STORE operands in user mode) are translated through the running process's page table:

1. `page = virtualAddress / PageSize` (PageSize = 256); `offset = virtualAddress % PageSize`.
2. `pte = PageTable[currentIndex][page]`.
3. **Resident** (`pte >= 0`): `physical = pte + offset`. C# `StampFrame` bumps the LRU counter and sets the dirty bit on a write.
4. **Unmapped** (`pte == -1`, or `page >= MaxPagesPerProcess`): the MMU is the **sole** memory-protection mechanism — there is no linear fallback and no separate LOAD/STORE bounds trap. `RaiseProtectionFault` terminates the process (exit status −1) via the same teardown path as an invalid instruction (`IvtInvalidInstruction`).
5. **Non-resident or COW** (`pte <= -2`): `TryTranslateData` rewinds the instruction pointer and dispatches `IvtPageFault`. The faulting instruction re-runs after the page is made resident.

**Write to a resident COW frame** (`FrameCow == 1`): `TryTranslateData` detects `isWrite && FrameIsCow`, rewinds IP, and re-raises `IvtPageFault`. The ISA handler calls `pair_resolve` to give the writer a private copy; the instruction then re-runs and commits the write.

In **Kernel mode** or without an OS image, all addresses are absolute (program base = 0) and translation is bypassed.

**`TranslateDataAddress`** (non-faulting, for the visualizer and tests): returns the linear address for non-resident pages without raising a fault.

**Kernel-mediated user-memory access (`Hardware.UserToPhysical`):** a second, synchronous entry point into the MMU used outside the normal per-instruction path — currently by the `FSYS` read/write wrapper (see [Open-file table and file syscalls](#open-file-table-and-file-syscalls)) to translate a user buffer's virtual address one page-chunk at a time. `UserToPhysical(va, isWrite)` writes the `isWrite` flag to `OsLayout.EnsureUserPageIsWrite`, dispatches `IvtEnsureUserPage` (an ISA routine, `ensure_user_page`, that faults in a non-resident page or COW-resolves a resident one exactly like `IvtPageFault` would), and reads the resulting physical address back from `OsLayout.EnsureUserPageResult`. It does not itself perform a LOAD/STORE — it only guarantees the page is resident and returns where it landed.

**`LoadProcess` sizing guard:** a process whose user extent (program + data memory + user stack) exceeds `MaxPagesPerProcess * PageSize` (32 KiB) is rejected before it is ever loaded — the same limit the MMU enforces at runtime via `UnmappedPage`.

### Page table seeding

`SeedPageTableIfNew` is called from `SetLayoutFromEntry` (the universal pre-resume gate that runs before every context switch). It seeds a process's page table only when the slot is first used for that process (guarded by `pageSeedBase`/`pageSeedExtent`), so resident pages are not evicted on every context switch.

Seeding rules per virtual page `p`:

| Condition | PTE written |
|-----------|-------------|
| `p >= pageCount` (beyond user extent) | `UnmappedPage = -1` |
| DATA page: `p * PageSize` in `[ProgramSize, ProgramSize + RequiredMemory)`, and the process has a COW partner | `CowPte(SwapSlot(partner, p))` — shares the partner's snapshot |
| DATA page, no COW partner | `SwapPte(SwapSlot(index, p))` — private swap slot |
| Code or user-stack page | `NonResidentPage = -2` — RAM-home, faulted in from the buddy block |

### Frame pool and LRU eviction

The shared frame pool holds `FrameCount = 4` physical frames. All `IvtPageFault` logic is in `EmitPageFault`.

**On a non-resident demand fault (EAX = faulting page):**

1. `SAVEREGS` persists the faulting process's context (IP is already rewound).
2. Decode the PTE: COW page → shared swap slot (read-only fill); private swap page → own swap slot; RAM-home (`pte == -2`) → fill from block home.
3. **Find a free frame:** scan `Occupied == 0`. If all occupied, select the **LRU victim** (smallest `LastUse` stamp).
4. **Evict the victim** (if pool was full):
   - Swap-backed victim: if `Dirty`, `DWRITE` frame → its swap slot. Flip its owner's PTE to `SwapPte` (or `CowPte` if the frame was COW).
   - RAM-home victim: if `Dirty`, ISA memcpy frame → block home. Flip its owner's PTE to `NonResidentPage`.
5. **Fill the frame:**
   - Swap / COW source: `DREAD` swap slot → frame. Set `FrameCow = 1` if the PTE was a COW PTE.
   - RAM-home source: ISA memcpy block home → frame. Set `FrameCow = 0`.
6. **Map:** write the frame's physical base into the faulting PTE. `JMP resume_mlfq`.

**On a resident COW write fault (`pte >= 0`, `FrameCow == 1`):** the handler calls `pair_resolve` for the faulting page, then `JMP resume_mlfq`. The faulting instruction re-runs against the now-writable private frame.

### Swap backing for DATA pages

Each `(processIndex, page)` DATA page has a fixed, deterministically computed Bin disk swap slot:

```
SwapSlot(processIndex, page) = SwapBase + processIndex * MaxPagesPerProcess + page
```

- `SwapBase = DefaultDiskSlots = 64` (swap region follows the 64 image slots).
- Total disk slots: `64 + 8 * 128 = 1088`.

PTE encodings for non-resident DATA pages:

| Encoding | Formula | Decode |
|----------|---------|--------|
| Private swap | `SwapPte(slot) = -(slot + 3)` | `SwapSlotFromPte(pte) = -pte - 3` |
| COW share | `CowPte(slot) = -(slot + 4096)` | `CowSlotFromPte(pte) = -pte - 4096` |

`CowPte` is always more negative than any `SwapPte` (since `SwapCowBias = 4096 >> SwapBase + MaxProcesses * MaxPagesPerProcess + SwapPteBias`), so the ranges never overlap and the test `IsCowPte` is a simple threshold check.

Swap slots are zeroed by `zero_swap_slots` on process exit and on exec, so a slot reused by a later process never serves stale data.

### Copy-on-write fork

Fork does not eagerly copy DATA page content. The full ISA fork flow is:

1. **`resolve_cow`:** if the forking parent already has a COW partner, resolves all shared DATA pages first (both partners get private copies) so the new fork starts from a clean private state.
2. **`flush_frames`:** writes every dirty frame owned by the parent back to its backing (swap slot or block home), then clears its dirty bit. This ensures the snapshot slots on disk are current before the child shares them.
3. **`cow_share`:** converts the parent's DATA-page PTEs to COW encoding. Resident DATA frames get `FrameCow = 1` (write-protect in the MMU). Non-resident swap PTEs are re-encoded as `CowPte`.
4. **Record partnership:** `CowPartner[parent] = child`, `CowPartner[child] = parent`.
5. **Allocate and memcpy:** the child's full RAM block is allocated and flat-memcpy'd from the parent (code, user stack, and kernel stack are copied eagerly). DATA pages are in the block too, but they are always accessed through swap — the memcpy'd image is only the initial backing.
6. **Child page table seeded on first resume** (by `SeedPageTableIfNew`): DATA pages get `CowPte(SwapSlot(parent, page))` — sharing the parent's snapshot. Code and stack pages get `NonResidentPage` (faulted in from the child's own block).

**Write by either partner triggers a page fault:** the ISA handler calls `pair_resolve` for the faulting page.

**`pair_resolve(page)`** (in `EmitPairResolve`): gives both partners private copies of the shared snapshot:
1. Copy the shared slot into X's private slot via the swap scratch page (DREAD shared → scratch, DWRITE scratch → Xslot).
2. Copy the shared slot into Y's private slot the same way.
3. X's frame (if resident and COW): clear `FrameCow = 0`; PTE stays pointing at the frame (now writable).
4. Y's frame (if resident): free it (`Occupied = 0`); PTE → `SwapPte(Yslot)` (non-resident, private).
5. X non-resident: PTE → `SwapPte(Xslot)`.

**`resolve_cow`** (in `EmitResolveCow`): loops over all DATA pages calling `pair_resolve` for each, then clears both partners to −1.

**Key invariant:** a process resolves any existing COW before it forks, execs, or exits again.

### Paging subroutines

| Subroutine | Callers | Effect |
|------------|---------|--------|
| `release_frames` | `exit_body`, exec routine | Frees all frames owned by the current process without write-back. Prevents dead-process frames from being evicted into freed and reused RAM. |
| `flush_frames` | Fork routine | Writes all dirty frames back to their backing (DWRITE or ISA memcpy); clears dirty bits. Used before fork's memcpy so the snapshot is current. |
| `zero_swap_slots` | `exit_body`, exec routine | Zeros all swap slots for the current process via `DWRITE` from the OS zero page. |
| `pair_resolve` | `pf_cow_write` path, `resolve_cow` | Gives both COW partners a private copy of one DATA page. |
| `resolve_cow` | Fork routine, `exit_body`, exec routine | Calls `pair_resolve` for every DATA page; clears the COW partner on both sides. |
| `cow_share` | Fork routine | Converts the forking parent's DATA pages to COW (write-protects resident frames, re-encodes non-resident swap PTEs). |

---

## The ISA filesystem

CSharpOS has a complete filesystem, built the same way as the scheduler and buddy allocator: it is compiled to the CSharpOS ISA and runs on the emulated CPU, not implemented as C# called from outside. It lives entirely in the disk's **file-block region** (see [Bin disk (block device)](#bin-disk-block-device)) and the OS RAM region's cache/scratch fields (see the OS data section table above). It was built in increments; each subsection below corresponds to one layer of that build.

Source: `CSharpOS/Disk/FsLayout.cs` (on-disk structure), `CSharpOS/Disk/FsImage.cs` (host-side file-staging helper), `BasicOSPlugin/OsRoutines.Cache.cs` (buffer cache), `BasicOSPlugin/OsRoutines.Fs.cs` (block allocator, directories, path resolution, syscalls).

### On-disk layout

The file-block region is `FsLayout.BlockCount = 256` blocks of `FsLayout.BlockSize = 256` bytes each (`Hardware.DefaultFileBlockCount`/`DefaultFileBlockSize`):

| Block | Role |
|-------|------|
| 0 (`SuperBlock`) | Magic (`0x5346`) at offset 0, block count at 4, free count at 8, root-directory block number at 12 (`SuperRootDirOffset`). |
| 1 (`BitmapBlock`) | Free bitmap: 256 bits (one per block) packed into `BitmapWords = 8` 32-bit words; bit = 1 means allocated. |
| 2..255 (`FirstDataBlock`..) | Allocatable data blocks. Block 2 is always the root directory (reserved by `fs_format`). |

Every block reserves its last 4 bytes (`NextPtrOffset = 252`) as a next-block link (`EndOfChain = -1`), so files and directories are **chains** of blocks that need not be contiguous; the usable payload per block is `PayloadBytes = 252` bytes. File content is stored **word-per-char** (one byte value per 4-byte word, matching the `OUTS`/`INS`/user-pointer convention), so a block holds `CharsPerBlock = 63` characters of file content, or `DirEntriesPerBlock = 3` directory entries (`DirEntryBytes = 64` each) when used as a directory block.

**Directory entry** (64 bytes): `type`@0 (0=free, 1=file, 2=dir) · `hash`@4 (fast-reject on lookup) · `firstBlock`@8 · `size`@12 (bytes, files only) · `name`@16 (`NameMaxChars = 12` words, word-per-char, null-padded).

### Write-back buffer cache

All filesystem block I/O goes through an in-OS-region write-back cache (`IvtCacheOp`, slot 16; `cache_*` ISA subroutines in `OsRoutines.Cache.cs`) — the block allocator and directory layer never call `FBREAD`/`FBWRITE` directly. `CacheSlotCount = 13` slots (≈ `BlockCount / 20`), each `CacheSlotSize = 276` bytes (20-byte header + `FileBlockSize = 256` data):

| Offset | Field | Notes |
|--------|-------|-------|
| 0 | Valid | 0 = empty (the whole pool starts empty), 1 = holds a block. |
| 4 | Block | The file-block number cached here. |
| 8 | Dirty | 1 = modified since load; written back on eviction or flush. |
| 12 | Pin | Pin count; a pinned slot (count > 0) is never chosen as an eviction victim. |
| 16 | Stamp | LRU stamp — the manager writes the shared `CacheClock` value on every access. |
| 20 | Data | The block's 256 bytes. |

**Eviction:** `cache_get` (the workhorse, called on every access) returns the resident data address on a hit (bumping the stamp) or, on a miss, evicts a victim (first an invalid slot, else the unpinned slot with the lowest stamp — LRU), writes it back via `FBWRITE` if dirty, `FBREAD`s the requested block in, and returns the new data address. `cache_dirty` marks a resident block dirty without writing it back (lazy write-back); `cache_write_through` writes it back immediately (`FBWRITE`) and clears dirty. `cache_pin`/`cache_unpin` bump/floor-decrement a slot's pin count. `cache_discard` drops a block with **no** write-back (clears valid+dirty+pin — used when a block is freed, so stale data is never written to a block that has been given away). `cache_flush` writes back every dirty, valid slot — **including pinned ones** (pinning blocks eviction, not write-back; a pinned slot that skipped `cache_flush` would never reach disk) — and clears their dirty bits; it runs periodically (`CacheFlushInterval = 200` context-switch ticks, hooked into `EmitContextSwitch`) and on demand.

### Block allocator and free-chaining

`IvtFsOp` (slot 17) dispatches on `EAX`; selectors 0–4 are the block layer (`fs_format`, `fs_alloc_block`, `fs_free_block`, `fs_chain_next`, `fs_chain_set_next`). `fs_format` writes the superblock (magic + geometry), zeroes the bitmap except bits 0 and 1 (superblock and bitmap block are always "allocated"), and allocates block 2 as the root directory — all through the cache. `fs_alloc_block` scans the bitmap for a clear bit, sets it, and initializes the new block's next-link to `EndOfChain`; `fs_free_block` clears the bit and calls `cache_discard` (no write-back of the freed block's stale content). `fs_chain_next`/`fs_chain_set_next` read/write a block's next-block link.

**Register convention:** `EDX`/`EDI` are used to carry a value across `cache_*` calls (the cache subroutines never touch them). Anything that must survive `fs_alloc_block`/`fs_chain_set_next` — which **do** clobber `EDX`/`EDI` — is spilled to `OsLayout.FsScratch*` memory instead.

### Directories and path traversal

A directory is just a block chain of directory entries (see On-disk layout above), so directory ISA routines are `IvtFsOp` selectors 5–9 built on top of the block layer:

| Selector | Routine | Behavior |
|----------|---------|----------|
| 5 | `fs_root_dir` | Reads the root directory's block number from the superblock. |
| 6 | `fs_hash` | `EAX` = name address → `EAX` = hash (`h = h*31 + c` over the word-per-char name). Used for a fast reject before the byte-for-byte name comparison. |
| 7 | `fs_dir_lookup` | `EBX` = dir block, `ECX` = name → entry address or −1. Walks the chain, hash-rejects, then verifies the full name; stashes the matched block in `FsScratchEntryBlock`. |
| 8 | `fs_dir_insert` | `EBX` = dir, `ECX` = name, `EDX` = type, `ESI` = first block → new entry address or −1. Rejects duplicates via `fs_dir_lookup`; finds a free slot in the chain or extends it with `fs_alloc_block` + `fs_chain_set_next`. |
| 9 | `fs_dir_remove` | `EAX` = dir, `ECX` = name → 0/−1. Marks the matched entry `type = free`. |

**Nested directories and paths** (selectors 10–11, in `OsRoutines.Fs.cs`'s path subroutines): `fs_mkdir` (`EBX` = parent dir, `ECX` = name → new dir block or −1; allocates a block and inserts a `type = dir` entry, freeing the block again if the name is a duplicate) and `fs_path_resolve` (`EBX` = path → final entry address or −1; walks `/a/b/c` one word-per-char component at a time via `fs_extract_component`, descending only through `type = dir` entries — a `type = file` component that isn't the last one fails the resolve). A trailing slash resolves the last named component; an empty or `/`-only path resolves to −1. Because directory lookups clobber registers between components, all loop state (read cursor, current directory, last-component flag, and the extracted component buffer) lives in `OsLayout.FsPath*` memory rather than registers.

### Open-file table and file syscalls

The open-file table (OFT) is a fixed array of `MaxOpenFiles = 8` entries (24 bytes each, at `OsLayout.OftBase`), independent of any one process:

| Offset | Field | Notes |
|--------|-------|-------|
| 0 | InUse | 0 = free slot. |
| 4 | FirstBlock | First data block of the open file. |
| 8 | Offset | Current read/write cursor, in bytes. |
| 12 | Size | Current file size, in bytes. |
| 16 | DirBlock | The directory block holding this file's entry (so writes can grow `size` in place). |
| 20 | EntryOffset | Byte offset of the entry within `DirBlock`. |

A process's fd table (`ProcessEntryFdTable`, fds 2–7) stores `OFT index + 1` per open fd (0 = free), so `oft_from_fd` (`EBX` = fd, `ECX` = proc → OFT entry address) is a pure memory lookup with no scanning.

`FSYS` (0x4D) is the sole user-mode entry point — see `docs/ISA.md`, section "Filesystem", for the exact opcode/register encoding and the full syscall number table (Open/Read/Write/Close/Exec/Unlink/Mkdir/Readdir). Internally it dispatches `IvtFsSyscall` (slot 18), which routes to:

- **`fs_open_core`** (`EBX` = absolute path, `ECX` = flags, `EDX` = proc → fd or −1): resolves the path via `fs_path_resolve`; if missing and `FsysCreateFlag` is set, creates it (`fs_create_file`, which resolves the parent directory via `fs_resolve_parent` and inserts a `type = file` entry); rejects directories; rejects a **second** open of the same file (`oft_find_first` — single-open policy, since two OFT handles would desync their cached sizes); fills a free OFT slot (`oft_alloc`) and a free fd slot.
- **`fs_close_core`** (`EBX` = fd, `ECX` = proc → 0/−1): clears the fd and its OFT slot (pure memory, no disk I/O).
- **`fs_read_core`** (`EBX` = fd, `ECX` = absolute buffer, `EDX` = count, `ESI` = proc → chars read or −1): reads through the cache starting at the OFT's current offset, clamped to `size - offset`, and advances the offset.
- **`fs_write_core`** (same signature, "written" instead of "read"): grows the file's block chain as needed (`fs_grow_chain`, which walks and extends the chain via `fs_alloc_block`+`fs_chain_set_next` — it reuses the same `FsRw*` scratch fields the caller is using, so callers restore them afterward), marks written blocks dirty (`cache_dirty`, lazy write-back), advances the offset, and grows `size` in both the OFT entry and the on-disk directory entry if the write extended the file.

The `IvtFsOp` selectors (12 = Open, 13 = Close, 14 = Read, 15 = Write) reach the same cores with an **absolute** path/buffer and an **explicit** process index — used by isolation tests and by `FsImage`. `FSYS` translates the user's virtual pointer to an absolute address and always operates on the calling process; see below for how the FSYS Read/Write wrapper does that translation.

**FSYS read/write buffer translation (Phase 3 rectification):** the buffer address FSYS passes to `fs_read_core`/`fs_write_core` must be absolute, but a user's data buffer can be demand-paged or swapped out — flat `ProgramAddress + ptr` arithmetic would read/write stale or wrong memory once that happens. The `fsy_read`/`fsy_write` wrapper routines walk the user's virtual buffer one page-chunk at a time, translating each chunk's start address through `user_word_addr` (which calls `ensure_user_page` → `IvtEnsureUserPage`, faulting the page in or COW-resolving it exactly as an ordinary LOAD/STORE would) before delegating that chunk to the unchanged, absolute-address `fs_read_core`/`fs_write_core`. This lifts the earlier constraint that FSYS read/write buffers had to be RAM-home (in a program-image page); a buffer anywhere in the process's mapped DATA region now round-trips correctly. Two constraints remain: **path** pointers (`Open`/`Exec`/`Unlink`/`Mkdir`/`Readdir`) are still translated with flat `ProgramAddress + ptr` arithmetic, so a path argument must still be RAM-home, and the `IvtFsOp` cores themselves still take absolute buffers (that is the internal/testing interface, not user-facing).

**Filesystem maintenance** (`IvtFsOp` selectors 16–18, `EmitFsMaintSubroutines` in `OsRoutines.Fs.cs`) back `FSYS` syscalls 5–7:

| Selector | Routine | Behavior |
|----------|---------|----------|
| 16 | `fs_unlink` | `EBX` = absolute path → 0/−1. Resolves the parent and entry, refuses a directory or a currently-open file (`oft_find_first`), frees the file's **entire block chain** (fixing an earlier leak where removal only cleared the directory entry and every block stayed marked allocated), then clears the directory entry. |
| 17 | `fs_mkdir_path` | `EBX` = absolute path → new dir block or −1. `fs_resolve_parent` + `fs_mkdir`. |
| 18 | `fs_readdir` | `EBX` = dir block, `ECX` = index, `EDX` = absolute output buffer → entry type or −1 past the last entry. Copies the n-th in-use (non-`type=free`) 64-byte directory entry into the output buffer. |

`fs_resolve_dir` (path → dir block, with `"/"` as a root special-case) and `oft_find_first` (an OFT scan shared by `fs_unlink`'s open-check and `fs_open_core`'s single-open reject) support these. The on-disk superblock's `FreeCount` is now kept accurate — `fs_alloc_block` decrements it, `fs_free_block` increments it — instead of being written once at format time. `fs_format` also **pins** the superblock and bitmap blocks in the cache (a pinned slot is never an eviction victim), and `cache_flush` was corrected to write back pinned-but-dirty slots too (pinning blocks eviction, not write-back — without the fix, the pinned superblock's writes never reached disk).

### Exec-by-path and boot auto-format

**Exec-by-path** (`FsysExec = 4`, `fs_exec_core`): replaces the running process's image with a program stored as a file in the filesystem, rather than a disk image slot. `EBX` = absolute path. It resolves the file's directory entry (holding its entry address in `R12` — `LoadField` clobbers `EAX`, so the address can't live there across the lookup), then reuses the same teardown/realloc/resume sequence as slot-based `EXEC` ([Exec](#exec)): `free_sub` releases the old region, `resolve_cow`/`release_frames`/`zero_swap_slots` tear down paging state, `alloc_sub` allocates a region sized to the file, `fs_load_image` copies the file's block chain into the new `ProgramAddress` (instead of a `DREAD` from a disk slot), and it `OSRET`s into the new image. Returns −1 (in the caller's saved `EAX`) if the path is missing or names a directory; **it never returns on success** — the calling process resumes running the new image instead.

**Boot auto-format:** `OperatingSystem.AttachHardware` calls a virtual `OnBooted(hw)` hook after seeding; `BasicOS.OnBooted` reads the disk's superblock directly (not through the cache, which is empty on a fresh machine) and runs `fs_format` only if the magic doesn't already match `FsLayout.SuperMagic`. This means a freshly created `Hardware` gets a usable, empty filesystem with no caller action required, while a disk loaded from a persisted `.bin` (via `Bin.Load`) keeps its files instead of being wiped.

**Populating the filesystem from the host:** `FsImage.WriteFile(hw, path, content)` drives the same `fs_open_core`/`fs_write_core`/`fs_close_core` cores (through the `IvtFsOp` selectors, using a reserved scratch process index) to stage a file before any user process runs — so a program later launched via `FSYS` exec-by-path is byte-for-byte what a user process would have produced by writing it itself. It stages the path and content in the free heap just above the OS region, so it must be called before any process memory is allocated there. `FsImage.EnsureDir`/`InstallProgram` are the Phase 4 counterparts `LoadProcess` uses to auto-install every booted program to `/bin/p<seq>`; unlike `WriteFile`, they stage through the dedicated `OsLayout.InstallPath`/`InstallBuf` region inside the OS area (not the free heap), so they remain safe to call after process memory has been allocated.

**Boot-from-filesystem (Phase 4):** `OperatingSystem.LoadProcess` (the general process-launch path — see [Boot creation (Spawn)](#boot-creation-spawn)) now installs every program into the filesystem, under `/bin/p<seq>` (a monotonically-numbered path), and runs it filesystem-backed instead of from a disk image slot — gated by the virtual `UsesFilesystemBoot` hook (`OperatingSystem`, default `false`; `BasicOS` overrides it to `true`). The legacy disk-slot path (`DiskSlot >= 0`) still exists and is exercised by isolation tests that hand-seed a slot directly; `IvtSpawn` picks between the two per process based on `DiskSlot`/`FirstBlock` (see the process-table entry layout above). A shell process that resolves programs by filesystem path at the user level (rather than the boot-time auto-install described here) is still future work.

---

## Process lifecycle

### Boot creation (Spawn)

`OperatingSystem.LoadProcess(process)`:
1. Resolve/auto-stage the program bytes (a file-path `Process` reads its bytes from disk).
2. Compute sizing (`ProgramSize + RequiredMemory + RequiredStackSize + KernelStackSize`); reject the process if its user extent exceeds `MaxPagesPerProcess * PageSize` (32 KiB).
3. Find a free process-table slot (reusing Terminated slots, else the next fresh one).
4. Seed sizing fields (`ProgramSize`, `RequiredMemory`, `RequiredStackSize`, `TotalSize`) and a `State = Terminated` placeholder into the entry.
5. **Program backing (Phase 4):** if `UsesFilesystemBoot` is `true` (`BasicOS` overrides it so; `OperatingSystem`'s default is `false`), install the program into the filesystem at `/bin/p<seq>` (`FsImage.EnsureDir`/`InstallProgram`, writing through the OS-region install-staging buffer using the not-yet-spawned target slot as a transient FS owner), resolve its first block, and set `FirstBlock` (`DiskSlot = −1`). Otherwise (legacy path): store the image in a free `Bin` disk slot and set `DiskSlot = slot` (`FirstBlock = −1`).
6. Call `hw.RunOsRoutineSynchronously(IvtSpawn, entry)`. This runs the `IvtSpawn` ISA routine synchronously (no context switch, suppressing observability events):
   - `alloc_sub` allocates a physical region; if successful, writes `ProgramAddress`.
   - Loads the image into RAM: `DREAD` from `DiskSlot` if slot-backed (`DiskSlot >= 0`), else `fs_load_image` chain-loads it from the filesystem starting at `FirstBlock`.
   - Seeds the saved register file: `EIP = 0`, `ESP = TotalSize − KernelStackSize`.
   - Sets `Level = User`, `State = Ready`, `Priority = 0`, `WaitReason = None`.
   - Assigns `Pid = NextPid++`.
7. C# seeds `FdTable`: fd 0 and fd 1 both point to the process's own device (slot index shim).
8. C# bumps `ProcessCount` if a fresh slot was used.
9. C# updates the C# `Process` object and `namesByBase` map.

`fs_load_image` (`EBX` = first block, `ECX` = word count, `EDX` = destination) copies a file's block chain into RAM word by word through the buffer cache; it was extracted from `fs_exec_core` (see [Exec](#exec)) so `IvtSpawn` and exec-by-path share the same chain-loading logic.

### Scheduling and context switching

The hardware `Run()` loop dispatches `IvtSchedule` whenever `processRunning == false`. The schedule routine immediately falls through to `resume_mlfq`, which scans for a Ready process and calls `LOADREGS`/`SETLAYOUT`/`OSRET` to resume it.

After 30 counted instructions, `IvtContextSwitch` is dispatched:
- `SAVEREGS` persists the interrupted process's state.
- The routine updates `TicksUsed`, checks demotion, and updates the boost timer.
- Falls through to `resume_mlfq`.

`resume_mlfq` returns to the same process if it is still the best candidate, or switches to another.

### I/O: blocking and waking

When a process's `OUT` or `IN` executes in user mode:
1. `EnterKernel` pushes the trap frame onto the process's kernel stack (setting `EBP`), enters Kernel mode (interrupts stay **enabled** — the handler is preemptible), and jumps to the shared syscall handler in the OS region.
2. The shared handler reads the trap-info (via `EBP`) to decide which syscall fired.
3. For `OUT`: `hw.KernelOutput` delivers the value if the output device is free, or calls `BlockCurrent(WaitReason.Output)` (which rewinds IP and dispatches `IvtBlockOutput`).
4. For `IN`: `hw.KernelInput` dequeues a value if the input buffer is non-empty, or adds the process to the device's waiter list and calls `BlockCurrent(WaitReason.Input)` (dispatches `IvtBlockInput`).

**Block routine** (`IvtBlockInput` = `IvtBlockOutput` = same `EmitBlock`): marks the process `Blocked` with the supplied `WaitReason`, calls `SAVEREGS`, and jumps to `resume_mlfq`.

**Wake routines** (`IvtWakeInput`, `IvtWakeOutput`): mark the target process `Ready`, reset it to `Priority = 0`, and resume the currently running process (or schedule if idle).

### Fork

`FORK` in user mode dispatches `IvtFork` (entirely ISA):

1. `SAVEREGS` saves the parent's current state to its process-table entry.
2. **Prepare COW sharing** (three subroutine calls on the privileged stack):
   - `resolve_cow` — if the parent already has a COW partner, resolves all shared DATA pages first (both sides get private copies), so the new fork starts from a clean private state.
   - `flush_frames` — writes all of the parent's dirty frames back to their backing (swap slot or block home), clearing dirty bits. Ensures the snapshot slots are current before the child reads them.
   - `cow_share` — converts the parent's DATA-page PTEs to COW: resident frames get `FrameCow = 1` (write-protect), non-resident swap PTEs are re-encoded as `CowPte`.
3. Find a free child slot (Terminated or fresh). If the table is full, write −1 into the parent's saved EAX and jump to `resume_mlfq`.
4. Copy the parent's sizing fields (`ProgramSize`, `RequiredMemory`, `RequiredStackSize`, `TotalSize`, `DiskSlot`, `FirstBlock`) to the child entry — a filesystem-backed parent's child stays filesystem-backed (same `FirstBlock`).
5. **Record the COW partnership:** `CowPartner[parent] = child`, `CowPartner[child] = parent`. The child's page table will be seeded with `CowPte` references to the parent's swap slots when the child first runs (via `SeedPageTableIfNew`).
6. `alloc_sub` allocates the child's physical region. On failure, mark the child Terminated, write −1 into the parent's saved EAX, and jump to `resume_mlfq`.
7. ISA `memcpy` copies `TotalSize` bytes from the parent's physical base to the child's (4 bytes at a time via absolute LOAD/STORE in Kernel mode). This copies code, user stack, and kernel stack eagerly. DATA page content in the RAM block is irrelevant because DATA pages are always accessed through swap.
8. Copy the parent's saved register file (96 bytes) to the child entry. `ESP` and `EIP` are base-relative; no relocation is needed.
9. Assign the child `Pid = NextPid++`; set `ParentPid = parent.Pid`.
10. Seed child fd table (slot index shim, device == child slot index).
11. Write 0 into the child's saved EAX slot; write `childPid` into the parent's saved EAX slot.
12. Jump to `resume_mlfq` — both parent and child are now Ready.

### Exec

`EXEC reg` in user mode dispatches `IvtExec` (entirely ISA):

1. Record the new disk slot in the entry (`DiskSlot = reg[reg]`) and reset `FirstBlock = −1` — `EXEC` always switches back to the slot-backed path, even if the process was filesystem-backed before. (Filesystem exec-by-path, below, is the mirror case: it sets `FirstBlock` and leaves `DiskSlot = −1`.) `EXEC(slot)` is unaffected by, and unchanged since, the Phase 4 boot-from-filesystem work — it is intentionally kept as the slot-based exec path alongside `FSYS` exec-by-path.
2. `free_sub` releases the old physical region (`ProgramAddress` and `TotalSize` from the entry).
3. **Paging teardown** (three subroutine calls):
   - `resolve_cow` — if this process has a COW partner, resolves all shared DATA pages first so the partner keeps its private copies before this process's slots are zeroed.
   - `release_frames` — frees all physical frames owned by this process (no write-back; the old image is gone).
   - `zero_swap_slots` — zeros all swap slots for this process so the new image's DATA pages start blank.
4. `DLEN` the new slot to get `newLen`; recompute `TotalSize = oldTotal − oldProgramSize + newLen`. Update `ProgramSize` and `TotalSize` in the entry.
5. `alloc_sub` allocates the new region. On out-of-memory, mark the entry Terminated and jump to `resume_mlfq` (the old image is already gone).
6. `DREAD` the new program image into the allocated region.
7. Zero the register file (96 bytes). Set `EIP = 0`, `ESP = TotalSize − KernelStackSize`.
8. Reset scheduling state (level = User, state = Ready, priority = 0, ticks = 0); preserve `Pid` and `ParentPid`.
9. `LOADREGS`/`SETLAYOUT`/`OSRET` to resume the same process running the new image. `SeedPageTableIfNew` runs on the first resume and seeds the new process's page table fresh.

**Filesystem exec-by-path (`FSYS` syscall 4, `fs_exec_core`):** a second exec path, layered on the filesystem instead of a disk image slot — see [Exec-by-path and boot auto-format](#exec-by-path-and-boot-auto-format). It follows the same teardown/realloc/resume shape as slot-based `EXEC` above, but sources the new image from a file's block chain via `fs_load_image` (through the buffer cache) instead of a `DREAD` from a disk slot, sets the entry's `FirstBlock` to the new file's first block and `DiskSlot = −1` (the mirror image of what `EXEC` does), and is dispatched through `IvtFsSyscall` rather than `IvtExec`.

### Wait

`WAIT reg` dispatches `IvtWait`:
1. Scan the process table for an entry with `State = Zombie` and `Pid == reg[reg]`.
2. **Zombie found:** reap it (mark Terminated), deliver its `ExitStatus` into the caller's saved EAX, and resume the caller immediately.
3. **No zombie:** mark the caller `Blocked` / `WaitReason = ChildProcess` / `WaitTarget = childPid`; call `SAVEREGS`; jump to `resume_mlfq`. The exit body will wake the caller when the child terminates.

### Exit / HLT

`EXIT reg` or `HLT` dispatch `IvtHalt` (with the exit status in EAX). Both flow into the shared `exit_body` ISA routine:

1. Mark the entry Terminated (hides it from scans immediately).
2. `free_sub` releases the physical region.
3. **Paging teardown** (three subroutine calls):
   - `resolve_cow` — if this process has a COW partner, resolves all shared DATA pages first so the partner keeps its private copies before this process's frames and slots are reclaimed.
   - `release_frames` — frees all physical frames owned by this process without write-back.
   - `zero_swap_slots` — zeros all swap slots for this process.
4. **Scan for a waiting parent:** if a process is `Blocked` / `WaitChild` on this PID, deliver the exit status to its saved EAX, mark it Ready at Priority 0, and reap this entry (Terminated).
5. **No waiting parent, but parent is alive:** mark this entry `Zombie` (retained until the parent calls `wait()`).
6. **No parent or parent dead:** reap immediately (Terminated).
7. **Reap zombie children:** scan for entries in state `Zombie` with `ParentPid == this.Pid`; mark them Terminated.
8. Jump to `resume_mlfq`.

### Invalid-instruction fault

`hw.TrapInvalidInstruction` fires the `InvalidInstruction` event and dispatches `IvtInvalidInstruction`. The ISA routine stores exit status −1 into the entry and jumps to `exit_body`, tearing the process down exactly like `EXIT`.

---

## Job control and signals

A handful of opcodes and a few process-entry fields turn the process model into a job-control-capable one, so the shell can run background jobs, reap them without blocking, signal the foreground job, and let a program install a handler that catches a signal instead of dying.

### REAP (non-blocking reap)

`REAP pidReg` dispatches `IvtReap`. It runs the same zombie-scan as `IvtWait` but **never blocks**: it looks for a terminated child with PID `reg[pidReg]` (or any child if 0), and delivers the reaped PID in EAX (0 if none is ready) plus its exit status in EDX. The shell calls it in a drain loop after each prompt so finished background jobs are collected and reported ("done") without the shell ever waiting.

### KILL (signals)

`KILL pidReg, sigReg` dispatches `IvtKill` with the target PID and a signal number (`Hardware.Sig{Term=1, Kill=2, Stop=3, Cont=4, Int=5}`, staged in `OsLayout.KillSig`). The handler resolves the target's process-table index and acts by signal:

- **KILL** — uncatchable; always runs the full teardown on the target. The teardown logic (free region, resolve COW, release frames, zero swap, wake a waiting parent, zombie/orphan handling) was **extracted from `exit_body` into a `teardown_reap` CALL/RET subroutine** so it can run against an *arbitrary* target: `kill_core` temporarily swaps `CurrentIndexOffset` to the victim, CALLs `teardown_reap`, then restores it. Killing yourself routes to `exit_body` instead (no return).
- **TERM / INT** — **catchable** (see [Catchable signals](#catchable-signals-sigaction--sigreturn) below). If the target installed a handler, the signal is delivered to it; otherwise the default action is the same teardown as KILL.
- **STOP** — set the target's `Stopped` flag (process-entry offset 192). Because `Stopped` is **orthogonal to `State`**, a Blocked process can be stopped too. A `WAIT`-ing parent is woken with status −2 (a WUNTRACED-style notification) *without* reaping the child. Stopping yourself does a `SAVEREGS` + reschedule.
- **CONT** — clear the `Stopped` flag.

The scheduler enforces stop at a **single point**: `resume_mlfq` skips any entry with `Stopped = 1`, so a stopped process is simply never chosen until continued.

Delivery of the 0/−1 result to the caller is suppressed when `OsLayout.KillNoDeliver = 1`. That flag is set for **terminal-initiated** signals, which have no killer process to receive a return value.

### Catchable signals (SIGACTION / SIGRETURN)

A user program installs a handler with `SIGACTION sigReg, handlerReg` — a C#-side write (like `SETFOCUS`) of a user virtual address into the running process's `SigHandler` field (0 clears it). Thereafter a catchable signal (`TERM`/`INT`) delivered to that process runs the handler instead of the default teardown. `kill_core`'s `kl_catch` path:

1. If no handler is installed, fall through to the default action (teardown). If the process is *already* running a handler (`InHandler = 1`), record the new signal in `SigPending` and return — one pending signal, delivered later.
2. Otherwise **snapshot** the target's saved context — the register file **and its privilege level** — into the process's `SignalSave` slot, then point its saved `EIP` at the handler and set `InHandler = 1`. (Saving the level too is essential: while the process runs its handler, an unrelated OS routine such as `IvtWakeOutput` can `SAVEREGS` its mid-syscall Kernel-level context into the entry, clobbering the level; restoring only the register file would make `SIGRETURN`'s `OSRET` resume against the wrong program base.)
3. Resume — into the handler directly if the target is the running process (e.g. a Ctrl-C to the foreground job), otherwise deliver 0 to the killer and let the target run its handler when next scheduled.

The handler ends with **`SIGRETURN`** → `IvtSigReturn`, which restores the `SignalSave` slot (register file + level) back into the entry, clears `InHandler`, re-delivers a `SigPending` signal if one was queued, and `OSRET`s back to the interrupted instruction. The handler runs in user mode and may itself make syscalls.

### Foreground signals (Ctrl-C / Ctrl-Z)

`Hardware.RaiseForegroundSignal(sig)` enqueues a pending `ForegroundSignal` interrupt. On the next tick, `TryDispatchPendingInterrupt` writes `KillSig` + `KillNoDeliver = 1` and dispatches `IvtKill` targeting the **focused** process. The console's `InteractionController` maps **Ctrl-C → INT** (catchable — a job with a handler catches it, otherwise it terminates) and **Ctrl-Z → STOP** (via `Console.TreatControlCAsInput`), so the keys behave like a Unix terminal against whatever job currently holds focus.

### Shell integration

`Programs.Shell()` keeps a small jobs table in its DATA region. A command suffixed with `&` is FORK/exec'd as a background job and recorded there instead of being waited on. The shell dispatches its built-ins — `jobs` (list), `kill <n>`, `stop <n>`, `bg <n>`, `fg <n>` — before attempting an exec, translating a job number to a PID via a `job_lookup` helper and issuing the corresponding `KILL` signal (or a blocking `WAIT` for `fg`).

---

## Dynamic OS plugin loading

`OsPluginLoader.Load(string dllPath, TextWriter log)`:
1. `Assembly.LoadFrom(dllPath)` loads the plugin DLL.
2. Iterates all types; finds the first non-abstract class that is a subclass of `OperatingSystem` and has a `(TextWriter)` constructor.
3. Invokes that constructor and returns the instance.

The `--os-plugin` command-line flag in `CSharpOSConsole` passes a custom DLL path to this loader. The default is `BasicOSPlugin.dll` beside the executable.

**Plugin contract:** a plugin DLL must contain exactly one non-abstract `OperatingSystem` subclass with a `(TextWriter)` constructor. The subclass overrides:
- `OsMemorySize` — how much memory to reserve for the OS region.
- `BuildOsImage(int osMemoryBase)` — returns the assembled OS image bytes (including the shared syscall handler).

Trap providers are auto-discovered within the same plugin assembly via `ITrapProvider` reflection in `CollectTraps()`.

---

## How the pieces fit together (end-to-end)

Here is a walkthrough of one process running `OUT EAX` through to output and the next context switch:

1. **Run loop** calls `StepInstruction`.
2. `Instruction.Execute` finds opcode 0x30 (OUT), calls `EvaluateTraps` (no trap on OUT), then calls `InstructionFunctions.Out`.
3. `InstructionFunctions.Out` detects `PrivilegeLevel == User`, calls `hw.EnterKernel(OUT, EAX*4)`.
4. **EnterKernel:** pushes the trap frame onto the process's kernel stack (sets `EBP`), writes trap-info, sets level = Kernel (interrupts stay enabled), points `ESP` at the kernel-stack top, and jumps to the shared syscall handler (IVT slot `IvtSyscall`).
5. The **shared syscall handler** (`EmitSyscall` in `OsRoutines.cs`) reads the trap-info via `EBP`, identifies `OUT`, loads the user's `EAX` value from the save area, and calls `OUT ESI` (now in Kernel mode).
6. **`InstructionFunctions.Out`** in Kernel mode calls `hw.KernelOutput(value)`.
7. **`KernelOutput`:** resolves the running process's fd 1 → device id, checks `OutputBusy`. If free: calls `Output(value, deviceId)` → fires `ProgramOutput` event → host renders the value. Sets `OutputBusy = true`.
8. Kernel handler calls `IRET`. **`Hardware.Iret`** restores the user register file, sets level = User, resumes user code.
9. After 30 counted instructions, **`DispatchOsRoutine(IvtContextSwitch)`** is called.
10. `CaptureInterruptedContext` snapshots the user registers. Interrupts are masked (the routine runs atomically) and IP points to the ContextSwitch routine.
11. **ContextSwitch ISA routine:** calls `SAVEREGS [EBX]` (entry address in EBX). Increments `TicksUsed`. Checks demotion. Decrements boost timer. Falls into `resume_mlfq`.
12. **`resume_mlfq`:** finds the highest-priority Ready process, calls `LOADREGS [entry]` → `SETLAYOUT [entry]` → `OSRET level`. **`Hardware.OsReturn`** commits the staged context, fires `ContextSwitched` if the base changed, drops to User mode.
13. The next process runs its next instruction.
