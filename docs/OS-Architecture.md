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
6. [MLFQ scheduler](#mlfq-scheduler)
7. [Buddy memory allocator](#buddy-memory-allocator)
8. [I/O device table and per-process file descriptors](#io-device-table-and-per-process-file-descriptors)
9. [Bin disk (block device)](#bin-disk-block-device)
10. [Trap system](#trap-system)
11. [Process lifecycle](#process-lifecycle)
    - [Boot creation (Spawn)](#boot-creation-spawn)
    - [Scheduling and context switching](#scheduling-and-context-switching)
    - [I/O: blocking and waking](#io-blocking-and-waking)
    - [Fork](#fork)
    - [Exec](#exec)
    - [Wait](#wait)
    - [Exit / HLT](#exit--hlt)
    - [Invalid-instruction fault](#invalid-instruction-fault)
12. [Dynamic OS plugin loading](#dynamic-os-plugin-loading)
13. [How the pieces fit together (end-to-end)](#how-the-pieces-fit-together-end-to-end)

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
| Interrupt Vector Table (IVT)                               |  56 bytes (14 × 4)
+------------------------------------------------------------+  offset 0x000 (= 0)
| OS code (assembled ISA routines)                           |  starts at OsLayout.CodeBase = 56
|   ContextSwitch, Schedule, Block, WakeInput, WakeOutput,  |
|   Halt, InvalidInstruction, BuddyAlloc, DiskLoad, Spawn,  |
|   Fork, Exec, Wait + shared exit_body, alloc_sub,         |
|   free_sub, resume_mlfq                                    |
+------------------------------------------------------------+  up to OsLayout.DataBase = 8192
| OS data section (runtime-seeded by C#)                     |  starts at OsLayout.DataBase = 8192
|   Scheduler state header (52 bytes)                        |
|   Process table  (8 × 176 = 1408 bytes)                   |
|   Buddy bitmap   (8 × 4 = 32 bytes)                        |
|   Privileged scratch stack (64 bytes)                      |
+------------------------------------------------------------+
| (total OS region)                                          |  OsLayout.TotalSize = 9748 bytes
+------------------------------------------------------------+  <-- heap start (process allocations)
```

**OS data section fields** (all offsets below are absolute addresses):

| Field | Absolute address | Size | Description |
|-------|-----------------|------|-------------|
| ProcessCount | 8192 | 4 | Number of process-table slots in use (high-water mark). |
| CurrentIndex | 8196 | 4 | Index of the currently running process; −1 when CPU is idle. |
| BuddyHeapStart | 8200 | 4 | Absolute address where the managed heap begins (= OsMemorySize). |
| BuddyHeapSize | 8204 | 4 | Buddy heap size in bytes (largest power of 2 ≤ available RAM). |
| BoostTimer | 8208 | 4 | Countdown ticks until the next MLFQ global priority boost. |
| QuantumTable | 8212 | 16 | 4 × 4-byte tick thresholds per MLFQ level: [1, 2, 4, 255]. |
| BuddyMinBlock | 8228 | 4 | Minimum allocatable block size in bytes (default: 256). |
| BuddyLevels | 8232 | 4 | Buddy tree depth = log2(HeapSize / MinBlock). |
| NextPid | 8236 | 4 | Monotonic PID counter; incremented each time a process is created. Starts at 1 (0 = "no process"). |
| KernelImageSlot | 8240 | 4 | Disk slot holding the kernel image (syscall handlers); EXEC re-loads it. |
| ProcessTable | 8244 | 1408 | 8 entries × 176 bytes (see entry layout below). |
| BuddyBitmap | 9652 | 32 | 8 × 32-bit words; 1 bit per buddy tree node (1=free, 0=used/split). |
| PrivilegedStack | 9684 | 64 | Scratch stack for CALL/RET within privileged OS routines. |

### Per-process memory layout

Each process occupies one contiguous block allocated by the buddy allocator. The block is laid out as:

```
[programBase]
+---------------------------------------------------+
| Program image       (ProgramSize bytes)           |
+---------------------------------------------------+
| Kernel section                                    |
|   [0]    Saved user register file  (96 bytes)     |  KernelSaveAreaOffset = 0
|   [96]   Trap info                 (16 bytes)     |  KernelTrapInfoOffset = 96
|           +0: faulting opcode (4 bytes)           |
|           +4: operand byte-offset in save area    |
|           +8: return IP (user-relative offset)    |
|           +12: (unused)                           |
|   [112]  Kernel ISA code (syscall handlers)       |  KernelHeaderSize = 112
+---------------------------------------------------+
| Process data memory (RequiredMemory bytes)        |
+---------------------------------------------------+
| User stack          (RequiredStackSize bytes)     |
+---------------------------------------------------+
| Kernel stack        (KernelStackSize = 64 bytes)  |
+---------------------------------------------------+
[programBase + TotalSize]
```

`TotalSize = ProgramSize + KernelHeaderSize + KernelImage.Length + RequiredMemory + RequiredStackSize + KernelStackSize`

The kernel stack is shared for all kernel-mode execution of this process. The privileged OS stack (at `OsLayout.PrivilegedStackTop`) is shared across all processes because Privileged routines run atomically (they cannot nest).

### Process table entry layout

Each entry is 176 bytes. Source: `Hardware.ProcessEntry*` constants.

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 96 | Register file | 24 × 4-byte register values. The EIP slot (register index 8, offset 32) holds the saved IP as a program-relative offset (not absolute). |
| 96 | 4 | Level | Saved `PrivilegeLevel` (0=User, 1=Kernel, 2=Privileged). |
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

5. **`BuildOsImage`.** `OsRoutines.BuildOsImage()` assembles all OS ISA routines into one `Assembler`, calls `Build(OsLayout.CodeBase)` to resolve labels against their absolute addresses, and returns a `byte[]` of length `OsLayout.TotalSize`. The IVT slots at the front are filled with the entry-point addresses of each routine.

6. **`SeedOsData`.** The C# side writes runtime values into the data section: heap parameters, the MLFQ quantum table, the initial buddy bitmap (root node free), and stages the kernel image (syscall handlers) to a disk slot, recording the slot number in `KernelImageSlotOffset`.

7. **Process loading.** `os.LoadProcess(process)` (called per program) resolves the disk slot, runs `hw.RunOsRoutineSynchronously(IvtSpawn, entry)` to allocate and load the process in ISA, then writes the kernel image into the kernel section and seeds the fd table from C#.

---

## The hardware run loop

`Hardware.Run()` is called in a tight loop by the host until `!os.HasProcesses`. Each call does exactly one of:

```
if (level == Privileged)
    StepInstruction()           // continue an in-progress OS routine

else if (TryDispatchPendingInterrupt())
    (wake routine entered — will run on next ticks)

else if (!processRunning)
    DispatchOsRoutine(IvtSchedule)

else
    StepInstruction()           // run the current process (User or Kernel)
```

`StepInstruction` fetches the 4-byte instruction at `instructionPointer`, advances IP by 4, executes it (which may fire a trap and redirect IP), and counts the instruction. After `SchedulerInstructionCount = 30` counted instructions, it dispatches `IvtContextSwitch`. Instructions that trap (invalid opcode, OUT/IN in user mode, process-control syscalls) do **not** count toward the quantum.

Privileged-mode instructions are never counted and never preempted.

---

## Interrupt vector table (IVT) and dispatch model

The IVT occupies the first 56 bytes of the OS region (`IvtSlotCount = 14` slots × 4 bytes). Each slot holds the absolute address of the corresponding OS routine. Hardware reads slot `s` as `ReadWord(0 + s * 4)` and jumps there in Privileged mode.

`Hardware.DispatchOsRoutine(int slot)` (or the overload with an EAX argument):
1. `CaptureInterruptedContext()` — snapshots the live register file plus the current IP (as a base-relative offset) into `trapFrame`; records the current privilege level in `interruptedLevel`.
2. Reads the routine address from the IVT.
3. Fires the `OsRoutineEntered` event.
4. Sets `level = Privileged`.
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

The buddy allocator manages physical RAM above the OS region. It is implemented entirely in ISA (`EmitBuddyAlloc`, `EmitAllocSub`, `EmitBuddyFree` in `OsRoutines.cs`) and runs in Privileged mode.

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

**Default parameters:** `BuddyDefaultMinBlock = 256` bytes. A 32 KiB memory size with a 9748-byte OS region leaves approximately 22 KiB for the heap; the largest power of 2 fitting is 16 384 bytes, giving `BuddyLevels = log2(16384 / 256) = 6`.

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

**Default geometry:** 64 slots × 1024 bytes (configured in `Hardware(memorySize, registerNames, os)` convenience constructor).

**C# API:**
- `Store(byte[] data)` — writes to the first free slot; returns slot index or −1 if full.
- `Store(int slot, byte[] data)` — overwrites a specific slot.
- `Load(int slot)` — returns a fresh copy of the slot's content at its true length.
- `Free(int slot)` — marks the slot empty and zeroes its storage.
- `GetLength(int slot)` — returns the stored byte count (throws if slot is free).

**ISA interface:** the `DREAD`, `DWRITE`, and `DLEN` instructions (all Privileged-only) delegate to `hw.DiskRead`, `hw.DiskWrite`, and `hw.DiskLength`, which call through to the `Bin` behind the `Device` at `DiskDeviceId`. Addresses passed to `DREAD`/`DWRITE` are absolute (Privileged mode program base = 0).

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
5. C# writes the kernel image (syscall handlers) into the kernel section.
6. C# seeds `FdTable`: fd 0 and fd 1 both point to the process's own device (slot index shim).
7. C# bumps `ProcessCount` if a fresh slot was used.
8. C# updates the C# `Process` object and `namesByBase` map.

### Scheduling and context switching

The hardware `Run()` loop dispatches `IvtSchedule` whenever `processRunning == false`. The schedule routine immediately falls through to `resume_mlfq`, which scans for a Ready process and calls `LOADREGS`/`SETLAYOUT`/`OSRET` to resume it.

After 30 counted instructions, `IvtContextSwitch` is dispatched:
- `SAVEREGS` persists the interrupted process's state.
- The routine updates `TicksUsed`, checks demotion, and updates the boost timer.
- Falls through to `resume_mlfq`.

`resume_mlfq` returns to the same process if it is still the best candidate, or switches to another.

### I/O: blocking and waking

When a process's `OUT` or `IN` executes in user mode:
1. `EnterKernel` saves the user register file, enters Kernel mode, and jumps to the kernel section's entry point.
2. The kernel ISA handler reads the trap-info to decide which syscall fired.
3. For `OUT`: `hw.KernelOutput` delivers the value if the output device is free, or calls `BlockCurrent(WaitReason.Output)` (which rewinds IP and dispatches `IvtBlockOutput`).
4. For `IN`: `hw.KernelInput` dequeues a value if the input buffer is non-empty, or adds the process to the device's waiter list and calls `BlockCurrent(WaitReason.Input)` (dispatches `IvtBlockInput`).

**Block routine** (`IvtBlockInput` = `IvtBlockOutput` = same `EmitBlock`): marks the process `Blocked` with the supplied `WaitReason`, calls `SAVEREGS`, and jumps to `resume_mlfq`.

**Wake routines** (`IvtWakeInput`, `IvtWakeOutput`): mark the target process `Ready`, reset it to `Priority = 0`, and resume the currently running process (or schedule if idle).

### Fork

`FORK` in user mode dispatches `IvtFork` (entirely ISA):
1. `SAVEREGS` saves the parent's current state to its process-table entry.
2. Find a free child slot (Terminated or fresh).
3. Copy the parent's sizing fields (`ProgramSize`, `TotalSize`, etc.) to the child entry.
4. `alloc_sub` allocates the child's physical region.
5. ISA `memcpy` copies `TotalSize` bytes from the parent's physical base to the child's. (4 bytes at a time, using absolute LOAD/STORE in Privileged mode.)
6. Copy the parent's saved register file (96 bytes) to the child entry. Because `ESP` and `EIP` are base-relative, no relocation is needed.
7. Assign the child `Pid = NextPid++`; set `ParentPid = parent.Pid`.
8. Seed child fd table (slot index shim).
9. Write 0 into the child's saved EAX slot; write `childPid` into the parent's saved EAX slot.
10. Call `resume_mlfq` — both parent and child are now Ready.

On failure (table full or out of memory): write −1 into the parent's EAX and call `resume_mlfq`.

### Exec

`EXEC reg` in user mode dispatches `IvtExec` (entirely ISA):
1. Record the new disk slot in the entry (`DiskSlot = reg[reg]`).
2. Call `free_sub` to release the old physical region (using the entry's current `ProgramAddress` and `TotalSize`).
3. `DLEN` the new slot to get `newLen`; recompute `TotalSize = oldTotal − oldProgramSize + newLen`.
4. `alloc_sub` allocates the new region.
5. `DREAD` the new program image; `DREAD` the kernel image (from `KernelImageSlotOffset`) into the kernel section.
6. Zero the register file (96 bytes). Set `EIP = 0`, `ESP = TotalSize − KernelStackSize`.
7. Reset scheduling state; preserve `Pid` and `ParentPid`.
8. `LOADREGS`/`SETLAYOUT`/`OSRET` to resume the same process running the new image.

On out-of-memory: mark the entry Terminated and call `resume_mlfq` (the old image is already gone).

### Wait

`WAIT reg` dispatches `IvtWait`:
1. Scan the process table for an entry with `State = Zombie` and `Pid == reg[reg]`.
2. **Zombie found:** reap it (mark Terminated), deliver its `ExitStatus` into the caller's saved EAX, and resume the caller immediately.
3. **No zombie:** mark the caller `Blocked` / `WaitReason = ChildProcess` / `WaitTarget = childPid`; call `SAVEREGS`; jump to `resume_mlfq`. The exit body will wake the caller when the child terminates.

### Exit / HLT

`EXIT reg` or `HLT` dispatch `IvtHalt` (with the exit status in EAX). Both flow into the shared `exit_body` ISA routine:
1. Mark the entry Terminated (hides it from scans).
2. `free_sub` releases the physical region.
3. **Scan for a waiting parent:** if a process is `Blocked` / `WaitChild` on this PID, deliver the exit status to its saved EAX, mark it Ready at Priority 0, and reap this entry (Terminated).
4. **No waiting parent, but parent is alive:** mark this entry `Zombie` (retained until the parent calls `wait()`).
5. **No parent or parent dead:** reap immediately (Terminated).
6. **Reap zombie children:** scan for entries in state `Zombie` with `ParentPid == this.Pid`; mark them Terminated (they will never be waited on).
7. Jump to `resume_mlfq`.

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
- `BuildOsImage(int osMemoryBase)` — returns the assembled OS image bytes.
- `KernelImage` — the syscall handler bytes to copy into each process's kernel section.

Trap providers are auto-discovered within the same plugin assembly via `ITrapProvider` reflection in `CollectTraps()`.

---

## How the pieces fit together (end-to-end)

Here is a walkthrough of one process running `OUT EAX` through to output and the next context switch:

1. **Run loop** calls `StepInstruction`.
2. `Instruction.Execute` finds opcode 0x30 (OUT), calls `EvaluateTraps` (no trap on OUT), then calls `InstructionFunctions.Out`.
3. `InstructionFunctions.Out` detects `PrivilegeLevel == User`, calls `hw.EnterKernel(OUT, EAX*4)`.
4. **EnterKernel:** saves user registers to the kernel section header, writes trap-info, sets level = Kernel, points ESP at the kernel stack, jumps to `kernelSectionStart + 112`.
5. The **kernel ISA handler** (built by `BasicOS.BuildKernelImage`) reads the trap-info, identifies `OUT`, loads the user's `EAX` value from the save area, and calls `OUT ESI` (now in Kernel mode).
6. **`InstructionFunctions.Out`** in Kernel mode calls `hw.KernelOutput(value)`.
7. **`KernelOutput`:** resolves the running process's fd 1 → device id, checks `OutputBusy`. If free: calls `Output(value, deviceId)` → fires `ProgramOutput` event → host renders the value. Sets `OutputBusy = true`.
8. Kernel handler calls `IRET`. **`Hardware.Iret`** restores the user register file, sets level = User, resumes user code.
9. After 30 counted instructions, **`DispatchOsRoutine(IvtContextSwitch)`** is called.
10. `CaptureInterruptedContext` snapshots the user registers. CPU goes to Privileged; IP points to the ContextSwitch routine.
11. **ContextSwitch ISA routine:** calls `SAVEREGS [EBX]` (entry address in EBX). Increments `TicksUsed`. Checks demotion. Decrements boost timer. Falls into `resume_mlfq`.
12. **`resume_mlfq`:** finds the highest-priority Ready process, calls `LOADREGS [entry]` → `SETLAYOUT [entry]` → `OSRET level`. **`Hardware.OsReturn`** commits the staged context, fires `ContextSwitched` if the base changed, drops to User mode.
13. The next process runs its next instruction.
