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
13. [Process lifecycle](#process-lifecycle)
    - [Boot creation (Spawn)](#boot-creation-spawn)
    - [Scheduling and context switching](#scheduling-and-context-switching)
    - [I/O: blocking and waking](#io-blocking-and-waking)
    - [Fork](#fork)
    - [Exec](#exec)
    - [Wait](#wait)
    - [Exit / HLT](#exit--hlt)
    - [Invalid-instruction fault](#invalid-instruction-fault)
14. [Dynamic OS plugin loading](#dynamic-os-plugin-loading)
15. [How the pieces fit together (end-to-end)](#how-the-pieces-fit-together-end-to-end)

---

## Overview

CSharpOS emulates a complete operating system on a custom 32-bit CPU (the `Hardware` class). The distinguishing design choice is that **the OS scheduler, memory allocator, and process-control routines are compiled to the CSharpOS ISA and run on the same CPU** — not implemented as C# methods called from the outside. The C# layer is responsible only for:

- Setting up the OS image in memory at boot.
- Loading initial programs (driving the ISA allocator).
- Fielding device interrupts from the host (keyboard input, output completion).
- Providing an event feed for the visualizer.

Everything else — scheduling, buddy allocation, context-switching, fork/exec/wait, and the kernel-mode I/O syscall path — executes as ISA instructions in the OS memory region.

---

## OS memory layout

### OS region

At boot, `Hardware.ReserveOsMemory(OsLayout.TotalSize)` places the OS at address 0. All process memory is allocated above it, so user and kernel code can never directly address the OS image.

```
Address 0
+------------------------------------------------------------+
| Interrupt Vector Table (IVT)                               |  64 bytes (16 × 4)
+------------------------------------------------------------+  offset 0x000 (= 0)
| OS code (assembled ISA routines)                           |  starts at OsLayout.CodeBase = 64
|   ContextSwitch, Schedule, Block, WakeInput, WakeOutput,  |
|   Halt, InvalidInstruction, BuddyAlloc, DiskLoad, Spawn,  |
|   Fork, Exec, Wait, Syscall, PageFault + shared exit_body,|
|   alloc_sub, free_sub, release_frames, flush_frames,      |
|   zero_swap_slots, pair_resolve, resolve_cow, cow_share,  |
|   resume_mlfq                                             |
+------------------------------------------------------------+  up to OsLayout.DataBase = 12288
| OS data section (runtime-seeded by C#)                     |  starts at OsLayout.DataBase = 12288
|   Scheduler state header (52 bytes)                        |
|   Process table  (8 × 176 = 1408 bytes)                   |
|   Buddy bitmap   (8 × 4 = 32 bytes)                        |
|   Privileged scratch stack (64 bytes)                      |
|   Page tables    (8 processes × 64 PTEs × 4 = 2048 bytes) |
|   Frame table    (4 frames × 32 = 128 bytes)               |
|   Frame pool     (4 frames × 256 = 1024 bytes)             |
|   Zero page      (256 bytes, always zero)                  |
|   Swap scratch   (256 bytes, fork transfer buffer)         |
|   COW partner table (8 × 4 = 32 bytes)                    |
+------------------------------------------------------------+
| (total OS region)                                          |  OsLayout.TotalSize = 17584 bytes
+------------------------------------------------------------+  <-- heap start (process allocations)
```

**OS data section fields** (all offsets below are absolute addresses):

| Field | Absolute address | Size | Description |
|-------|-----------------|------|-------------|
| ProcessCount | 12288 | 4 | Number of process-table slots in use (high-water mark). |
| CurrentIndex | 12292 | 4 | Index of the currently running process; −1 when CPU is idle. |
| BuddyHeapStart | 12296 | 4 | Absolute address where the managed heap begins (= OsMemorySize). |
| BuddyHeapSize | 12300 | 4 | Buddy heap size in bytes (largest power of 2 ≤ available RAM). |
| BoostTimer | 12304 | 4 | Countdown ticks until the next MLFQ global priority boost. |
| QuantumTable | 12308 | 16 | 4 × 4-byte tick thresholds per MLFQ level: [1, 2, 4, 255]. |
| BuddyMinBlock | 12324 | 4 | Minimum allocatable block size in bytes (default: 256). |
| BuddyLevels | 12328 | 4 | Buddy tree depth = log2(HeapSize / MinBlock). |
| NextPid | 12332 | 4 | Monotonic PID counter; incremented each time a process is created. Starts at 1 (0 = "no process"). |
| ProcessTable | 12336 | 1408 | 8 entries × 176 bytes (see entry layout below). |
| BuddyBitmap | 13744 | 32 | 8 × 32-bit words; 1 bit per buddy tree node (1=free, 0=used/split). |
| PrivilegedStack | 13776 | 64 | Scratch stack for CALL/RET within the atomic OS routines. |
| PageTables | 13840 | 2048 | 8 processes × 64 PTEs × 4 bytes. PTE ≥ 0: resident frame base. PTE = −1: unmapped. PTE = −2: non-resident RAM-home. PTE ≤ −3: non-resident swap-backed. PTE ≤ −4096: copy-on-write share. |
| FrameTable | 15888 | 128 | 4 frames × 32 bytes each (see frame table layout below). |
| FramePool | 16016 | 1024 | 4 frames × 256 bytes. Frame f lives at `FramePoolBase + f * PageSize`. |
| ZeroPage | 17040 | 256 | Always-zero OS scratch page; `DWRITE` source when zeroing a swap slot. |
| SwapScratch | 17296 | 256 | Transfer page for fork's per-slot deep-copy (DREAD src → scratch, DWRITE scratch → dst). |
| CowPartners | 17552 | 32 | 8 × 4 bytes. `CowPartner[i]` = the partner process-table slot index, or −1 when no COW share is active. |

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

Each entry is 176 bytes. Source: `Hardware.ProcessEntry*` constants.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 96 | Register file | 24 × 4-byte register values. The EIP slot (register index 8, offset 32) holds the saved IP as a program-relative offset (not absolute). |
| 96 | 4 | Level | Saved `PrivilegeLevel` (0=User, 1=Kernel). |
| 100 | 4 | State | `ProcessState` enum: Ready(0), Blocked(1), Terminated(2), Zombie(3). |
| 104 | 4 | WaitReason | `WaitReason` enum: None(0), Input(1), Output(2), ChildProcess(3). |
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
| 152 | 4 | DiskSlot | Disk slot index holding this process's program image. |
| 156 | 4 | (spare) | Reserved. |
| 160 | 16 | FdTable | 4 × 4-byte file descriptor → device-id mappings. fd 0 = stdin, fd 1 = stdout, fd 2–3 unused. |
| **176** | — | *(total)* | `ProcessEntrySize = 176` |

The register-file region (offset 0–95) is the save area written by `SAVEREGS` and read by `LOADREGS`. Because EIP is stored as a base-relative offset, the same saved context works after a `FORK` (the child's copy is at a different physical base but the offset is unchanged).

---

## Boot and image-build flow

1. **Plugin discovery.** `OsPluginLoader.Load(dllPath, log)` loads the OS plugin DLL, finds the `OperatingSystem` subclass, and constructs it with `new BasicOS(log)`.

2. **`BasicOS` construction.** `CollectTraps()` scans the plugin assembly via reflection for all `ITrapProvider` implementations and collects their `Trap` structs. The base class `OperatingSystem(List<Trap> traps, TextWriter log)` stores them.

3. **Hardware construction.** `new Hardware(memorySize, registerNames, os)` registers the disk as a block device at `DiskDeviceId = 256`, then calls `os.AttachHardware(hw)`.

4. **`AttachHardware`.** Loads the collected traps into `hw.trapTable`, calls `hw.ReserveOsMemory(OsLayout.TotalSize)`, and writes the OS image to address 0 via `hw.WriteBytes(0, BuildOsImage(0))`.

5. **`BuildOsImage`.** `OsRoutines.BuildOsImage()` assembles all OS ISA routines into one `Assembler`, calls `Build(OsLayout.CodeBase)` to resolve labels against their absolute addresses, and returns a `byte[]` of length `OsLayout.TotalSize`. The IVT slots at the front are filled with the entry-point addresses of each routine. The COW partner table (at `OsLayout.CowPartnerBase`) is initialized to all −1 (no partner) directly in the image, so even hand-seeded test images see a correct initial state without calling `SeedOsData`.

6. **`SeedOsData`.** The C# side writes runtime values into the data section: heap parameters, the MLFQ quantum table, the initial buddy bitmap (root node free), and the initial PID counter.

7. **Process loading.** `os.LoadProcess(process)` (called per program) resolves the disk slot, runs `hw.RunOsRoutineSynchronously(IvtSpawn, entry)` to allocate and load the process in ISA, then seeds the fd table and reads back the assigned PID from C# (no per-process kernel image to copy — the syscall handler is shared OS code).

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

The IVT occupies the first 64 bytes of the OS region (`IvtSlotCount = 16` slots × 4 bytes, `IvtSize = 64`). Each slot holds the absolute address of the corresponding OS routine. Hardware reads slot `s` as `ReadWord(0 + s * 4)` and jumps there in Kernel mode. Slot `IvtSyscall` is special: `EnterKernel` jumps there directly **without** masking interrupts (so the syscall handler stays preemptible), while all other dispatched routines mask interrupts and run atomically.

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
| 2 | InvalidInstruction | Unknown opcode, user-mode DREAD/DWRITE/DLEN, OS trap conditions |
| 3 | WakeInput | Input interrupt with a waiter |
| 4 | WakeOutput | Output-complete interrupt |
| 5 | BlockInput | Process blocked on input device |
| 6 | BlockOutput | Process blocked on output device |
| 7 | Schedule | Run loop idle (no process running) |
| 8 | Allocate | Synchronous allocation from C# (RunOsRoutineSynchronously) |
| 9 | DiskLoad | Part of the C#-driven load path |
| 10 | Fork | `FORK` instruction |
| 11 | Exec | `EXEC` instruction |
| 12 | Wait | `WAIT` instruction |
| 13 | Spawn | `LoadProcess` via RunOsRoutineSynchronously |
| 14 | Syscall | `EnterKernel` (IN/OUT trap); jumped to directly, NOT dispatched — interrupts stay enabled |
| 15 | PageFault | C# MMU (`TryTranslateData`) when a user data access hits a non-resident PTE |

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

**Queue selection (`resume_mlfq`):** scan from level 0 to level 3. Within each level, scan all process-table entries in round-robin order starting one past the current index (wrapping around). Pick the first entry in state Ready at the current level. If no entry is found at any level, set `CurrentIndex = −1` and call `OSRET` with no staged context (idle).

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

**Default parameters:** `BuddyDefaultMinBlock = 256` bytes. `OsLayout.TotalSize = 17584` bytes. With a typical 32 KiB machine, the heap starts at 17584 bytes, leaving about 14 KiB; the largest power of 2 fitting is 8 192 bytes, giving `BuddyLevels = log2(8192 / 256) = 5`.

---

## I/O device table and per-process file descriptors

`Hardware` maintains a `Dictionary<int, Device>` keyed by device id. Two device types exist:

- **Character device:** owns an input queue (`Queue<int>`), a waiter list (`List<int>` of process indices blocked on input), and an output-busy flag. Character devices are created on demand.
- **Block device:** the `Bin` disk, registered at `DiskDeviceId = 256`.

**Device ids for processes:** by convention, each process's private I/O device has id equal to its process-table slot index. This shim is installed by `LoadProcess` and `IvtFork`.

**File descriptor table:** each process-table entry holds `FdCount = 4` file descriptors starting at `ProcessEntryFdTable` (absolute offset 160). Each fd is a 4-byte device id. fd 0 = stdin, fd 1 = stdout. `Hardware.FdDevice(fd)` resolves the running process's fd to a device id; `Hardware.FocusedInputDevice()` resolves the foreground process's stdin.

**Focus (foreground process):** `Hardware.activeProcess` is the slot index of the foreground process. Keyboard input from `RaiseInputInterrupt()` (no device argument) is delivered to the focused process's stdin device. The host can cycle focus; `SETFOCUS` (instruction 0x38) updates `activeProcess` by scanning the process table for the target PID.

**I/O flow (character device):**
- **Output:** `Hardware.KernelOutput(value)` resolves fd 1 → device id, checks `OutputBusy`, delivers via `Output(value, deviceId)` (fires `ProgramOutput` event), and sets `OutputBusy = true`. When the host signals output completion via `RaiseOutputComplete(deviceId)`, the interrupt is enqueued; on the next tick, `TryDispatchPendingInterrupt` clears `OutputBusy` and dispatches `IvtWakeOutput`.
- **Input:** `Hardware.KernelInput(register)` resolves fd 0 → device id, checks the input queue. If empty: records the process as a waiter on that device and calls `BlockCurrent(WaitReason.Input)`. When input arrives via `RaiseInputInterrupt(value, deviceId)`, it is buffered in the device queue; if there is a waiter, `IvtWakeInput` is dispatched with the waiter's process index in EAX.

---

## Bin disk (block device)

`CSharpOS/Disk/Bin.cs` implements a flat, fixed-slot block store. The disk's backing store is a `byte[]` of size `slotCount * slotSize`, with a directory of `bool[] occupied` and `int[] lengths` (actual content length per slot, which may be less than `slotSize`).

**Default geometry:** 576 slots × 1024 bytes. The `Hardware` convenience constructor uses `DefaultDiskSlots + OsLayout.SwapSlotCount` = 64 + 512 = 576 total slots. Slots 0–63 hold process program images; slots 64–575 are the deterministic swap region used by demand paging (8 processes × 64 pages per process).

**C# API:**
- `Store(byte[] data)` — writes to the first free slot; returns slot index or −1 if full.
- `Store(int slot, byte[] data)` — overwrites a specific slot.
- `Load(int slot)` — returns a fresh copy of the slot's content at its true length.
- `Free(int slot)` — marks the slot empty and zeroes its storage.
- `GetLength(int slot)` — returns the stored byte count (throws if slot is free).

**ISA interface:** the `DREAD`, `DWRITE`, and `DLEN` instructions (all Kernel-only) delegate to `hw.DiskRead`, `hw.DiskWrite`, and `hw.DiskLength`, which call through to the `Bin` behind the `Device` at `DiskDeviceId`. Addresses passed to `DREAD`/`DWRITE` are absolute (Kernel mode program base = 0).

---

## Trap system

A `Trap` struct has three fields: `Opcode` (the instruction to guard), `Reason` (a human-readable fault message), and `Condition` (a `Func<Hardware, byte, byte, byte, bool>` that returns true when the trap should fire). Source: `CSharpOS/Structs/Trap.cs`.

Traps are evaluated by `Hardware.EvaluateTraps(opcode, b1, b2, b3)` before any instruction executes. If a trap fires, `TrapInvalidInstruction` is called, dispatching `IvtInvalidInstruction`, and the instruction does not execute.

**Trap registration:** `BasicOS.CollectTraps()` discovers all `ITrapProvider` implementations in the plugin assembly via reflection (`Assembly.GetExecutingAssembly().GetTypes()`). New trap handlers require no manual registration; adding a class that implements `ITrapProvider` is sufficient.

**Current traps registered by `BasicOSPlugin`:**

| Provider class | Opcode guarded | Condition | Fault reason |
|---------------|---------------|-----------|-------------|
| `IretTrapProvider` | `IRET` (0x33) | `PrivilegeLevel == User` | "IRET is a privileged instruction" |
| `LoadBoundsTrapProvider` | `LOAD` (0x05) | User mode AND address outside process ranges | "Memory read outside process bounds" |
| `StoreBoundsTrapProvider` | `STORE` (0x06) | User mode AND address outside process ranges | "Memory write outside process bounds" |

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
4. **Unmapped** (`pte == -1`, outside the process): linear fallback to `programBase + virtualAddress`. The bounds traps still guard genuine out-of-bounds accesses.
5. **Non-resident or COW** (`pte <= -2`): `TryTranslateData` rewinds the instruction pointer and dispatches `IvtPageFault`. The faulting instruction re-runs after the page is made resident.

**Write to a resident COW frame** (`FrameCow == 1`): `TryTranslateData` detects `isWrite && FrameIsCow`, rewinds IP, and re-raises `IvtPageFault`. The ISA handler calls `pair_resolve` to give the writer a private copy; the instruction then re-runs and commits the write.

In **Kernel mode** or without an OS image, all addresses are absolute (program base = 0) and translation is bypassed.

**`TranslateDataAddress`** (non-faulting, for the visualizer and tests): returns the linear address for non-resident or unmapped pages without raising a fault.

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
- Total disk slots: `64 + 8 * 64 = 576`.

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

## Process lifecycle

### Boot creation (Spawn)

`OperatingSystem.LoadProcess(process)`:
1. Resolve the disk slot (auto-stage file-path processes to a free Bin slot on first load).
2. Find a free process-table slot (reusing Terminated slots, else the next fresh one).
3. Seed sizing fields (`ProgramSize`, `RequiredMemory`, `RequiredStackSize`, `TotalSize`, `DiskSlot`) and a `State = Terminated` placeholder into the entry.
4. Call `hw.RunOsRoutineSynchronously(IvtSpawn, entry)`. This runs the `IvtSpawn` ISA routine synchronously (no context switch, suppressing observability events):
   - `alloc_sub` allocates a physical region; if successful, writes `ProgramAddress`.
   - `DREAD` copies the program image from disk into RAM.
   - Seeds the saved register file: `EIP = 0`, `ESP = TotalSize − KernelStackSize`.
   - Sets `Level = User`, `State = Ready`, `Priority = 0`, `WaitReason = None`.
   - Assigns `Pid = NextPid++`.
5. C# seeds `FdTable`: fd 0 and fd 1 both point to the process's own device (slot index shim).
6. C# bumps `ProcessCount` if a fresh slot was used.
7. C# updates the C# `Process` object and `namesByBase` map.

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
4. Copy the parent's sizing fields (`ProgramSize`, `RequiredMemory`, `RequiredStackSize`, `TotalSize`, `DiskSlot`) to the child entry.
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

1. Record the new disk slot in the entry (`DiskSlot = reg[reg]`).
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
