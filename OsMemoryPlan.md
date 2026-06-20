# OS Memory Plan

---

## ✅ COMPLETE (2026-06-20) — cutover done, 191 tests green, console works

The OS now runs its scheduler, allocator, and process lifecycle entirely as ISA code
in a reserved OS memory region. The C# scheduling/allocation logic has been removed.

**Final architecture:**
- `Hardware.Run` drives everything via the IVT: when Privileged it steps the in-flight
  OS routine; otherwise it dispatches one pending interrupt (Wake), schedules when idle,
  or steps the current process and dispatches ContextSwitch at the quantum. Halt /
  invalid-opcode / block all `DispatchOsRoutine` to the matching IVT routine. A bare
  hardware harness (`OsMemorySize == 0`, e.g. FakeOS) falls back to plain stepping.
- `Hardware` fires `ContextSwitched` (at OSRET, when the resumed program base changes)
  and `InvalidInstruction` (with the trap reason) — observability moved off the OS.
- `OperatingSystem` is now thin: boots the OS image + seeds OS data (`AttachHardware`),
  loads programs by running the ISA allocator then seeding the process-table entry
  (`LoadProcess`), and answers `HasProcesses`/`HasRunningProcess` from OS memory /
  the `processRunning` flag. `NameForBase` maps a program base → file path for the UI.
- `BasicOS.OsMemorySize = OsLayout.TotalSize`, `BuildOsImage = OsRoutines.BuildOsImage`.
- All nine routines implemented + IVT-wired: ContextSwitch, Schedule, Block(In/Out),
  Wake(In/Out), Halt, InvalidInstruction, LoadProcess (first-fit alloc + split; Halt &
  InvalidInstruction reclaim memory to the free list).
- `OsLayout.DataBase = 1280` (code ~1KB; BuildOsImage guards against overrun).
- Console: run loop pumps ticks to distinguish transient (mid-routine) idle from true
  idle before prompting for input; visualizer subscribes to the Hardware events.

**Tests:** the ~30 tests that asserted removed C# internals were replaced with
end-to-end tests (load real programs, run via `hw.Run()` to completion) plus the OS
routine isolation tests. New OS test files: OsSupportInstructionTests,
OsContextSwitchRoutineTests, OsSchedulingRoutineTests, OsAllocatorRoutineTests.
Console smoke-tested: counter prints 1..10, round-robin runs both processes, the
guessing game blocks/wakes on input and outputs the match.

Everything below is the original design + the incremental build log, kept for history.

---

## IMPLEMENTATION PROGRESS (updated 2026-06-20)

**Migration strategy chosen: routine-by-routine, parallel.** Keep the working C#
OS. Build + isolation-test each assembly routine against a hand-seeded process
table. Flip BasicOS over to the assembly routines only once all are proven, so the
build/tests stay green throughout.

**Observability decision: Hardware fires the events.** Once scheduling logic is in
assembly, Hardware detects a process switch at OSRET (layout changed) and fires
ContextSwitched itself. InvalidInstruction is already a Hardware event. OS ISA code
stays pure; the visualizer keeps working.

### Wake resume-level decision: RESOLVED — option (a) + SAVEREGS persists level
Hardware stashes the interrupted level in `CaptureInterruptedContext` (field
`interruptedLevel`). The clean fix that also covers ContextSwitch's *save* side:
**`SAVEREGS` writes the stashed interrupted level into the entry's level slot**
(`address + ProcessEntryLevel`), so `entry.level` is always fresh. Wake then reuses
SAVEREGS→LOADREGS→OSRET(entry.level) to resume the interrupted process unchanged —
no sentinel, no new opcode. Added `ProcessState.Terminated` (free table slot).
Added `DispatchOsRoutine(slot, eaxArgument)` to pass a wait reason / opcode / entry
to a routine in EAX (after the interrupted registers are snapshotted). Added
`Assembler.CodeLength` to compute each routine's address when packing the image.

### DONE (203 tests green, console builds)
- **New instructions** (Instruction.cs / InstructionFunctions.cs / Assembler.cs):
  - `MOV_REG_IMM16 = 0x03` — **added, not in original plan.** Needed because the
    plan's routines load addresses/offsets > 255 (process table alone is 1024 bytes)
    and the existing `MOV_REG_IMM` is 8-bit only. Encoding `[0x03, dest, hi, lo]`.
  - `SAVEREGS = 0x40`, `LOADREGS = 0x41`, `SETLAYOUT = 0x42`, `OSRET = 0x43`.
- **Trap-frame model** (Hardware.cs): an OS routine needs scratch registers but must
  preserve the interrupted process's registers. Solved like real trap hardware:
  `DispatchOsRoutine` → `CaptureInterruptedContext()` snapshots the live register
  file (folding live IP into the EIP slot) into `trapFrame`; the routine then
  clobbers live registers freely; `SAVEREGS` persists `trapFrame` to a table entry.
  `LOADREGS` stages a `pendingContext` buffer (live regs untouched); `OSRET` reads
  the target level from a live reg, then atomically commits `pendingContext`, sets
  IP from its EIP slot, and drops to that level.
- **GetProgramBase()** returns 0 in Privileged mode (absolute addressing).
- **Process-entry constants** (Hardware.cs): ProcessEntry* offsets, ProcessEntrySize=128.
- **IVT constants** (Hardware.cs): Ivt* slot indices, IvtSlotCount=9, IvtSize=36.
- **OS memory reservation**: `IOperatingSystem.OsMemorySize` + `BuildOsImage(base)`
  (default 0 / empty). `OperatingSystem.AttachHardware` reserves the OS region at
  front of memory and allocates processes above it. `Hardware.ReserveOsMemory` +
  `DispatchOsRoutine(slot)`. With OsMemorySize 0 everything is behavior-identical.
- **OsLayout.cs**: full data-section layout constants (DataBase=1024, scheduler
  header, process table, free-range table, pending queue; MaxProcesses=8).
- **OsRoutines.cs**: `BuildOsImage()` packs every routine after the IVT (uses
  `Assembler.CodeLength` to record each routine's address) and a shared
  `resume_next` / `rn_idle` tail (scan from current+1, wrap, resume Ready or idle).
  Implemented + isolation-tested:
  - **ContextSwitch** (save+resume, skip blocked, wrap, all-blocked idle).
  - **Schedule** (from idle: pick a Ready process, or stay idle).
  - **Block** (mark current Blocked + reason via EAX, save, switch away; last one idles).
  - **Wake** (reason in EAX; make one matching waiter Ready, resume interrupted
    process unchanged at its true level; only wakes the matching reason).
  - **Halt** (mark current Terminated, resume next; last one idles).
  IVT slots wired: ContextSwitch, Schedule, BlockInput/Output, WakeInput/Output, Halt.

### REMAINING
- **LoadProcess routine** (IVT slot 8) — the big one: first-fit allocation from the
  free-range table, range split/merge, move a pending entry into the table, mark
  Ready. Largest remaining piece; do with seeded free-list isolation tests. NOTE:
  Halt currently marks the slot Terminated but does NOT yet return its memory to the
  free list — wire memory reclamation in alongside the allocator.
- **InvalidInstruction routine** (IVT slot 2) — opcode passed in EAX; terminate the
  faulting process (like Halt) and switch away. (Logging/observability already moved
  to Hardware per the events decision.)
- **Cutover** — set `BasicOS.OsMemorySize = OsLayout.TotalSize` and
  `BasicOS.BuildOsImage = OsRoutines.BuildOsImage`; have the C# `LoadProcess` seed
  the OS process table + free list in memory (instead of C# lists); wire
  `Hardware.Run` to `DispatchOsRoutine(slot)` instead of `os.X()`; make Run's idle
  check read `currentIndex` from OS memory; have Hardware fire `ContextSwitched` at
  OSRET when the resumed process differs; then delete the C# scheduling/allocation
  logic and migrate the ~30 affected tests + the FakeOS/TrappingOS doubles.

### Suggested next-session order
1. LoadProcess allocator (first-fit + split) + memory reclaim in Halt + seeded tests.
2. InvalidInstruction routine + test.
3. Cutover: BasicOS image + C# LoadProcess seeds OS memory + Hardware.Run dispatch +
   Hardware-fired ContextSwitched + Run idle reads OS memory.
4. Migrate the C# OS tests and FakeOS/TrappingOS doubles; delete dead C# logic.

---


Goal: Convert all OS C# methods into ISA code stored in a dedicated OS memory region,
allocated at hardware-attach time. Mimics how a real OS kernel lives in protected memory
and is entered via interrupt vectors rather than direct function calls.

---

## Decisions Made

| Question | Decision |
|---|---|
| OS data structures (process table, free list, scheduler state) | Move to OS memory as binary data |
| Hardware triggers OS routines via | Interrupt Vector Table (IVT) — Hardware reads IVT slot, jumps |
| OS routine return mechanism | IRET-style: OS prepares next process with LOADREGS then calls OSRET |
| Privileged-mode addressing | `GetProgramBase()` returns 0 — LOAD/STORE/JMP are absolute |
| Context switch: how to save/restore 64-byte register file | Two new instructions: SAVEREGS / LOADREGS |
| SETLAYOUT vs baking into LOADREGS | Separate SETLAYOUT instruction for flexibility |
| OS memory size | Declared by OperatingSystem (`OsMemorySize` property) — Hardware allocates that much |
| IOperatingSystem / OperatingSystem / BasicOS hierarchy | Keep — future plan: reflective dynamic loading |
| Memory allocation / process creation / destruction | Also move to OS memory (not just runtime scheduling) |

---

## Four New Opcodes (all Privileged-only)

| Hex | Name | Behaviour |
|---|---|---|
| `0x40` | `SAVEREGS b1` | Writes `instructionPointer` into EIP register slot, then saves full register file to absolute address held in register `b1` |
| `0x41` | `LOADREGS b1` | Reads full register file from absolute address in register `b1`, sets `instructionPointer` from the EIP register slot |
| `0x42` | `SETLAYOUT b1` | Reads ProgramAddress/ProgramSize/RequiredMemory/RequiredStackSize from process entry at absolute address in register `b1`, calls the existing `SetProcessLayout` logic |
| `0x43` | `OSRET b1` | Sets `level = (PrivilegeLevel)ReadRegisterAt(b1)` — execution continues at IP already set by LOADREGS |

`GetProgramBase()` change:
```csharp
public int GetProgramBase()
{
    if (level == PrivilegeLevel.Privileged) { return 0; }
    return level == PrivilegeLevel.User ? currentProcessInstructionStart : currentProcessKernelSectionStart;
}
```

---

## OS Memory Layout

Hardware reads `os.OsMemorySize` in `AttachHardware`, reserves that block starting at address 0.
Everything after it is available for process allocation (so process allocator starts at `osMemoryBase + osMemorySize`).

`FakeOS` sets `OsMemorySize = 0` — no region allocated, unit tests unaffected.

```
osMemoryBase (= 0)
├── IVT          9 slots × 4 bytes = 36 bytes
├── OS code      assembled ISA routines (variable length)
└── OS data      process table + free list + scheduler state
```

### IVT Slot Assignments

```
Slot 0  ContextSwitch
Slot 1  Halt
Slot 2  InvalidInstruction   (Hardware writes faulting opcode into EAX before jump)
Slot 3  WakeInput
Slot 4  WakeOutput
Slot 5  BlockInput
Slot 6  BlockOutput
Slot 7  Schedule
Slot 8  LoadProcess          (Hardware writes process entry address into EAX before jump)
```

### IVT Dispatch (replaces every direct `os.X(hw)` call in Hardware)

```csharp
private void DispatchOsRoutine(int slot)
{
    int routineAddr = ReadWord(osMemoryBase + slot * 4);
    level = PrivilegeLevel.Privileged;
    instructionPointer = routineAddr;
    trapTaken = true;
}
```

---

## OS Data Section Layout

Placed immediately after OS code. Offsets are from the start of the data section
(= `osMemoryBase + IVT_size + code_size`). Store the data section base address as
a constant in the OS image so routines can find it.

```
[0..3]    processCount
[4..7]    currentProcessIndex  (-1 = no running process)
[8..11]   freeRangeCount
[12..15]  pendingCount
[16..]    process table         MaxProcesses × 128 bytes
[..]      free memory ranges    MaxFreeRanges × 8 bytes each  (Start:4, Size:4)
[..]      pending queue         MaxPending × 4 bytes (index into process table)
```

Suggested limits (choose based on total memory size):
- `MaxProcesses = 8`
- `MaxFreeRanges = 16`
- `MaxPending = 8`

---

## Process Table Entry (128 bytes per process)

```
Offset  Size  Field
[0]     64    register file  (EIP register slot holds saved instructionPointer)
[64]    4     privilege level (PrivilegeLevel enum value)
[68]    4     ProcessState   (0 = Ready, 1 = Blocked)
[72]    4     WaitReason     (0 = None, 1 = Input, 2 = Output)
[76]    4     ProgramAddress
[80]    4     ProgramSize
[84]    4     RequiredMemory
[88]    4     RequiredStackSize
[92]    36    reserved / padding to reach 128 bytes
```

`SETLAYOUT reg` reads `[addr+76 .. addr+91]` and calls `SetProcessLayout(ProgramAddress, ProgramSize, RequiredMemory, RequiredStackSize)`.

EIP register index: `registerIndex[RegisterName.EIP]` — use this to find the byte offset
within the 64-byte register file where the instruction pointer is stored.

---

## Parameter Passing into OS Routines

Before jumping to an IVT routine, Hardware writes relevant data into a register:

| Routine | Pre-loaded register |
|---|---|
| InvalidInstruction | EAX = faulting opcode |
| WakeInput / WakeOutput | nothing (reason is implicit in the slot) |
| BlockInput / BlockOutput | nothing |
| LoadProcess | EAX = address of process entry in OS process table (already filled by C# `LoadProcess`) |
| Others | nothing |

The OS routine reads EAX at entry if it needs the parameter.

---

## C# Changes Per File

### Hardware.cs
- Add fields: `int osMemoryBase`, `int osMemorySize`
- `AttachHardware` (called from constructor via os): after traps, read `os.OsMemorySize`, reserve that block, call `os.BuildOsImage(osMemoryBase)`, write returned bytes to memory
- `GetProgramBase()`: add `if (level == PrivilegeLevel.Privileged) return 0;`
- `DispatchOsRoutine(int slot)`: reads IVT entry, jumps (see above)
- Replace every `os.ContextSwitch(this)` → `DispatchOsRoutine(0)`
- Replace `os.HandleHalt(this)` → `DispatchOsRoutine(1)`
- Replace `os.HandleInvalidInstruction(...)` → write opcode to EAX, `DispatchOsRoutine(2)`
- Replace `os.Wake(WaitReason.Input)` → `DispatchOsRoutine(3)`
- Replace `os.Wake(WaitReason.Output)` → `DispatchOsRoutine(4)`
- Replace `os.BlockCurrentProcess(WaitReason.Input)` → `DispatchOsRoutine(5)`
- Replace `os.BlockCurrentProcess(WaitReason.Output)` → `DispatchOsRoutine(6)`
- Replace `os.Schedule(this)` → `DispatchOsRoutine(7)`
- Replace `os.LoadProcess(...)` → write entry addr to EAX, `DispatchOsRoutine(8)`
- Add to `InstructionFunctions` / `opcodeTable`: SAVEREGS(0x40), LOADREGS(0x41), SETLAYOUT(0x42), OSRET(0x43)
- `SetProcessLayout` must be accessible from `SETLAYOUT` implementation — make it `internal` if needed
- `HasRunningProcess` now reads `currentProcessIndex != -1` from OS data in hardware memory (or keep as a derived Hardware property reading from `osMemoryBase + dataOffset + 4`)

### IOperatingSystem.cs
Add to interface:
```csharp
int OsMemorySize { get; }
byte[] BuildOsImage(int osMemoryBase);
```
Remove (or make optional/noop): `ContextSwitch`, `HandleHalt`, `HandleInvalidInstruction`, `Wake`, `BlockCurrentProcess`, `Schedule` — these are now handled entirely in OS ISA code. Keep `LoadProcess` signature for the C# entry point that stages data before calling `DispatchOsRoutine(8)`.

### OperatingSystem.cs (abstract)
- Remove: `List<Process> pendingProcesses`, `List<Process> activeProcesses`, `int currentProcess`, `List<MemoryRange> availableMemoryRanges`
- Remove C# scheduling logic from `ContextSwitch`, `HandleHalt`, `HandleInvalidInstruction`, `Wake`, `BlockCurrentProcess`, `Schedule`, `DrainPendingProcesses`
- Add: `int OsMemorySize { get; }` (abstract or virtual)
- Add: `byte[] BuildOsImage(int osMemoryBase)` (abstract) — returns IVT + code + zeroed data section
- `AttachHardware`: call `hw.LoadTraps(traps)` (keep); hardware now does the image write
- `LoadProcess(Process process)`: write program bytes to hardware, fill process entry into OS process table pending queue, call `DispatchOsRoutine(8)`

### BasicOS.cs
- Implement `OsMemorySize` — calculate: IVT (36 bytes) + assembled code size + data section size (based on MaxProcesses etc.)
- Implement `BuildOsImage(int osMemoryBase)`:
  1. Assemble all 9 OS routines
  2. Compute data section offset
  3. Write IVT entries (absolute addresses of each routine)
  4. Return full byte array
- Keep `BuildTraps()` and `BuildKernelImage()` unchanged — per-process kernel syscall image is separate from OS memory

### Process.cs
- Keep as-is for now — used as a loading descriptor
- Data is written into the OS process table entry during `LoadProcess`

### Assembler additions needed
- `SaveRegs(RegisterName reg)` → emits `[0x40, regIndex, 0, 0]`
- `LoadRegs(RegisterName reg)` → emits `[0x41, regIndex, 0, 0]`
- `SetLayout(RegisterName reg)` → emits `[0x42, regIndex, 0, 0]`
- `OsRet(RegisterName reg)` → emits `[0x43, regIndex, 0, 0]`

---

## OS Routine Pseudocode (ISA level)

### ContextSwitch (slot 0)
```
; Read scheduler state to find current process entry address
MOV EAX, [DATA_BASE + CURRENT_INDEX_OFFSET]   ; current index
CMP EAX, -1
JZ  no_save
MOV EBX, ENTRY_SIZE                            ; 128
MUL EAX, EBX
MOV ECX, DATA_BASE + PROCESS_TABLE_OFFSET
ADD ECX, EAX                                   ; ECX = current entry addr
SAVEREGS ECX                                   ; save registers + IP
no_save:
; Find next Ready process (scan from currentIndex+1, wrap)
; Set DATA[CURRENT_INDEX] = nextIndex
; ECX = next entry addr
LOADREGS ECX
SETLAYOUT ECX
MOV EAX, [ECX + 64]                            ; privilege level
OSRET EAX
```

### Halt (slot 1)
```
; Read current entry, mark it as terminated (remove from active list)
; Compact process table or mark slot free
; Find next Ready process
; If none: set CURRENT_INDEX = -1, return somehow (idle)
; Else: LOADREGS / SETLAYOUT / OSRET
```

### WakeInput / WakeOutput (slots 3/4)
```
; Scan process table for first Blocked entry with matching WaitReason
; Set State = Ready
; OSRET EAX (restore whatever level we had — passed via EAX or read from somewhere)
```

### BlockInput / BlockOutput (slots 5/6)
```
; Read current entry, set State = Blocked, WaitReason = Input/Output
; Run ContextSwitch logic to pick next Ready process
```

### Schedule (slot 7)
```
; If a Ready process exists: set CURRENT_INDEX, LOADREGS, SETLAYOUT, OSRET
; Else: OSRET back to idle (level = User, IP = ?)
```

### LoadProcess (slot 8)
```
; EAX = address of filled process entry in pending queue
; Allocate memory from free list (first-fit)
; Write ProgramAddress into entry, call SetLayout equivalent
; Move entry from pending queue into process table
; Mark State = Ready
; OSRET
```

---

## Implementation Order (tests green at each step)

1. **New instructions + GetProgramBase=0 for Privileged**
   - Add SAVEREGS/LOADREGS/SETLAYOUT/OSRET to Instruction.cs opcode constants and opcodeTable
   - Implement in InstructionFunctions.cs
   - Update GetProgramBase() in Hardware.cs
   - Add Assembler DSL methods
   - Write unit tests for each new instruction

2. **OS memory allocation + IVT plumbing**
   - Add OsMemorySize to IOperatingSystem and OperatingSystem (default 0 → no-op)
   - Hardware.AttachHardware allocates os region, writes os.BuildOsImage()
   - Add DispatchOsRoutine to Hardware (but keep old os.X() calls for now)
   - FakeOS: OsMemorySize=0 → existing tests unaffected

3. **Process table constants**
   - Define all entry offsets as public constants in Hardware (or a new OsLayout static class)
   - These are shared between Hardware (SETLAYOUT impl) and BasicOS (routine assembly)

4. **ContextSwitch routine in BasicOS**
   - Implement BuildOsImage with just ContextSwitch
   - Wire Hardware to call DispatchOsRoutine(0) instead of os.ContextSwitch()
   - Remove ContextSwitch C# logic from OperatingSystem
   - Update/add tests

5. **Halt + InvalidInstruction routines**
   - Wire slots 1 and 2
   - Remove C# handlers from OperatingSystem

6. **Wake + Block + Schedule routines**
   - Wire slots 3–7
   - Remove C# handlers from OperatingSystem

7. **LoadProcess routine (memory allocation)**
   - Wire slot 8
   - Remove C# memory allocation from OperatingSystem
   - Process.cs stays as descriptor; LoadProcess writes to OS pending queue in memory

---

## Current Codebase Context

Solution: `CSharpOS.slnx`  
Projects: `CSharpOS` (library), `OSTests` (xUnit, 183 tests all passing), `CSharpOSConsole`

Key files:
- `CSharpOS/CPU/Hardware.cs` — partial class, constants/events/fields/run loop/kernel mechanism
- `CSharpOS/CPU/Instruction.cs` — opcode constants + Execute() + opcodeTable
- `CSharpOS/CPU/InstructionFunctions.cs` — one static method per opcode
- `CSharpOS/OS/OperatingSystem.cs` — abstract base, scheduling, memory allocation
- `CSharpOS/OS/BasicOS.cs` — concrete OS, kernel image (per-process I/O syscalls)
- `CSharpOS/Assembler/Assembler.cs` (partial) — DSL for building ISA byte arrays
- `OSTests/TestSupport.cs` — FakeOS, TrappingOS, KernelImageOS, helper methods

Trap system (already done, don't change):
- OS-defined traps loaded into `Hardware.trapTable` (Dictionary<byte, List<Trap>>)
- `Instruction.Execute` calls `hw.EvaluateTraps` before dispatch
- BasicOS.BuildTraps() defines IRET/LOAD/STORE privilege checks
- FakeOS has no trap table → instruction unit tests unaffected

Per-process kernel section (already done, separate from OS memory):
- Each process gets a kernel section with Hardware.KernelHeaderSize header + OS kernel image copy
- `EnterKernel` / `Iret` handle user→kernel transitions for IN/OUT syscalls
- `BasicOS.BuildKernelImage()` assembles the per-process I/O dispatch code
- This is NOT the same as OS memory — it stays as-is
