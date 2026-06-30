# CSharpOS/CPU Quick Reference

## Files

| File | Role |
|------|------|
| Hardware.cs | Core emulator: memory, registers, run loop, MMU, IVT dispatch, I/O, paging |
| Hardware.Types.cs | Private nested types: `InterruptKind`, `Interrupt` struct |
| Instruction.cs | Opcode constants + dispatch table (static initializer) |
| InstructionFunctions.cs | One handler per opcode; `internal static` |
| Disassembler.cs | `Disassembler.Decode(byte, byte, byte, byte) → string`; pure, no side effects |
| BranchPredictor.cs | 2-bit saturating BHT; 64 entries; observational only |

---

## Hardware Constructors

```csharp
Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os)
    // uses default Bin(DefaultDiskSlots + OsLayout.SwapSlotCount, DefaultDiskSlotSize)

Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os, Bin disk)
```

`os.AttachHardware(this)` is called at the end of the constructor.

---

## Key Hardware Methods

Section markers use `// ===== ` — search for them, then `Read(offset=N, limit=80)` to target reads.

### Run Loop (Hardware.cs:1037 `// ---- integral functions`)
| Method | Line | Notes |
|--------|------|-------|
| `Run()` | :1048 | One tick: atomic OS step / wake dispatch / schedule / process step |
| `StepInstruction()` | :1075 | private; fetch→trap-eval→dispatch; counts toward quantum |
| `RunOsRoutineSynchronously(slot, eaxArg)` | :1324 | Runs an OS routine to completion; preserves all CPU state; suppresses events |

### IVT Dispatch (Hardware.cs:323)
| Method | Line | Notes |
|--------|------|-------|
| `DispatchOsRoutine(int slot)` | :327 | Capture context, mask interrupts, jump to IVT[slot] |
| `DispatchOsRoutine(int slot, int eaxArg)` | :334 | Same + writes eaxArg to EAX after capture |
| `EnterKernel(byte opcode, int operandByteOffset)` | :1191 (`:1197`) | IN/OUT user-mode trap; keeps interrupts enabled; jumps to IvtSyscall |
| `Iret()` | :1204 | Return from syscall handler; restore saved regs, set User mode |
| `OsReturn(int privilegeLevel)` | :1288 | OSRET impl: commit staged context, fire ContextSwitched, re-enable interrupts |

### Memory (Hardware.cs:438)
| Method | Line | Notes |
|--------|------|-------|
| `ReadBytes(int address) → byte[4]` | :440 | Raw physical read |
| `WriteBytes(int address, byte[] data)` | :445 | Raw write; fires MemoryWritten event |
| `ReadWord(int) / WriteWord(int, int)` | :704 | Little-endian 32-bit; private |
| `WriteWordRaw(int, int)` | :716 | No MemoryWritten event; used for MMU bookkeeping |
| `ReadRegisterState(int address) → byte[]` | :495 | Reads full register-file block from memory |

### MMU (Paging) (Hardware.cs:724 `// ---- MMU`)
| Method | Line | Notes |
|--------|------|-------|
| `TryTranslateData(int virt, bool isWrite, out int physical) → bool` | :733 | False on page fault (page fault dispatched; caller must abort) |
| `TranslateDataAddress(int virt) → int` | ~:789 | Non-faulting query for visualizer/tests |
| `RaisePageFault(int page)` | ~:800 | private; rewinds IP, dispatches IvtPageFault |
| `SeedPageTableIfNew(index, progAddr, progSize, reqMem, userExtent)` | ~:820 | private; once per process slot; seeds non-resident PTEs |
| `PageTableEntry(int processIndex, int page) → int` | ~:855 | Read a PTE |
| `IsPageResident(int processIndex, int page) → bool` | ~:862 | PTE ≥ 0 |
| `StampFrame(int frameIndex, bool isWrite)` | ~:870 | private; bumps pageClock, sets dirty bit |

### Frame Table Accessors (Hardware.cs:909 `// ---- frame table`)
`FrameOccupied(f)`, `FrameOwnerProcess(f)`, `FrameOwnerPage(f)`, `FrameDirty(f)`,
`FrameLastUse(f)`, `FrameSwap(f)`, `FrameCow(f)`, `ResidentFrameCount()`,
`CowPartner(int processIndex)`

### Registers (Hardware.cs:438)
| Method | Line | Notes |
|--------|------|-------|
| `ReadRegister(RegisterName) / WriteRegister(RegisterName, int)` | :474 | Named access |
| `ReadRegisterAt(byte index) / WriteRegisterAt(byte index, int)` | :460 | Index access (b1/b2/b3 from instructions) |
| `GetRegisterOffset(RegisterName) → int` | :279 | Byte offset in register file |
| `ReadRegisters() / WriteRegisters(byte[])` | :453 | Full register file |

### I/O (Hardware.cs: helper functions :554, kernel I/O :1494, interrupts :1527)
| Method | Line | Notes |
|--------|------|-------|
| `RaiseInputInterrupt(int value)` | :1527 | Routes to focused process's stdin device |
| `RaiseInputInterrupt(int value, int deviceId)` | :1534 | Direct device |
| `RaiseOutputComplete() / RaiseOutputComplete(int deviceId)` | :1539 | Signal output done |
| `KernelInput(byte register)` | :1494 | Block if empty; else dequeue to register |
| `KernelOutput(int value)` | :1507 | Block if output busy; else Output + mark busy |
| `GetDevice(int id) → Device` | :611 | Creates char device on first use |
| `RegisterDevice(Device)` | :603 | Explicit registration (used for disk block device) |

### Focus / Process Control (Hardware.cs:1370)
| Method | Line | Notes |
|--------|------|-------|
| `SetActiveProcess(int index) / GetActiveProcess() → int` | :291 | Foreground process; -1=none |
| `SetFocus(int pid)` | :1414 | Maps pid→slot; calls SetActiveProcess |
| `Fork() / Exec(int slot) / Wait(int pid) / Exit(int status) / Halt()` | :1383 | Dispatch appropriate IVT slots |

### Disk (Hardware.cs:611)
| Method | Line | Notes |
|--------|------|-------|
| `DiskRead(int destAddr, int slot) → int` | :630 | Copy slot→RAM; returns byte count; backs DREAD |
| `DiskWrite(int slot, int srcAddr, int len)` | :639 | Copy RAM→slot; backs DWRITE |
| `DiskLength(int slot) → int` | :650 | Backs DLEN |
| `Disk` property | :615 | Returns `Bin` behind DiskDeviceId |

### Process Layout (Hardware.cs:1158)
| Method | Line | Notes |
|--------|------|-------|
| `SetLayoutFromEntry(int entryAddress)` | :1271 | SETLAYOUT impl; calls SeedPageTableIfNew |
| `LoadProcess(Process, byte[])` | :1159 | Write program bytes, set layout, init ESP |
| `LoadProcessLayout(Process)` | :1166 | Restore layout without writing program bytes |

### Branch Prediction (Hardware.cs:1103 `// ---- branch prediction`)
| Method | Line | Notes |
|--------|------|-------|
| `RecordBranch(bool taken)` | :1117 | Called by JZ/JNZ/JS/JNS; User mode only |
| `GetBranchPredictor() → BranchPredictor` | :1138 | |
| `GetCycles() → long` | :1143 | Observational cycle counter |
| `SetCurrentInstructionAddress(int)` | :1107 | Set before each handler so branch can index BHT |

### Traps (Hardware.cs:381)
| Method | Line | Notes |
|--------|------|-------|
| `LoadTraps(List<Trap>)` | :382 | Replaces trap table |
| `EvaluateTraps(byte, byte, byte, byte) → bool` | :396 | Called before each instruction dispatch |
| `TrapInvalidInstruction(byte, byte, byte, byte)` | :1465 | Fires event; dispatches IvtInvalidInstruction |

### Events (11 total)
`InstructionExecuted`, `MemoryWritten`, `InvalidInstruction`, `ProgramOutput`,
`ContextSwitched`, `PrivilegeChanged`, `ProcessBlocked`, `ProcessWoken`,
`OsRoutineEntered`, `ProcessTerminated`, `BranchPredicted`

---

## InstructionFunctions (InstructionFunctions.cs)

All handlers are `internal static void XxxMethod(Hardware hw, byte b1, byte b2, byte b3)`.

Section markers (search `// ===== `):

| Section | Line |
|---------|------|
| MOV (MovRegReg, MovRegImm, MovRegImm16) | :42 |
| Memory (Load, Store) | :59 |
| ALU (Add, Sub, Mul, Div, Cmp, Inc, Dec, And, Or, Xor, Not, Shl, Shr) | :91 |
| Control Flow (Jmp, Jz, Jnz, Js, Jns, Call, Ret) | :188 |
| I/O (Out, In) | :277 |
| OS Primitives (Hlt, Iret) | :303 |
| Context Management (SaveRegs, LoadRegs, SetLayout, OsRet) | :314 |
| Disk (DRead, DWrite, DLen) | :343 |
| Process (Fork, Exec, Wait, Exit, SetFocus) | :388 |

Data access pattern (LOAD/STORE/CALL/RET): call `hw.TryTranslateData(virt, isWrite, out addr)`; if it returns false, **return immediately** (page fault raised; instruction will re-run).

---

## Assembler

```csharp
Assembler asm = new Assembler();
asm.MovImm(RegisterName.EAX, 5);    // emit instruction
asm.Label("loop");                   // define label
asm.Jmp("loop");                     // forward/back reference
byte[] code = asm.Build(origin);     // resolves labels; idempotent (copies internal state)
int offset = asm.CodeLength;         // bytes emitted so far (before Build)
```

`Build(origin)` is idempotent — safe to call twice. `origin` is added to all label addresses so code can be placed at an offset (e.g., `OsLayout.CodeBase`).

Emit methods mirror Instruction constants: `Mov`, `MovImm`, `MovImm16`, `Load`, `Store`, `Add`, `Sub`, `Mul`, `Div`, `Cmp`, `Inc`, `Dec`, `And`, `Or`, `Xor`, `Not`, `Shl`, `Shr`, `Jmp`, `Jz`, `Jnz`, `Call`, `Ret`, `Js`, `Jns`, `Out`, `In`, `Hlt`, `Iret`, `SaveRegs`, `LoadRegs`, `SetLayout`, `OsRet`, `DRead`, `DWrite`, `DLen`, `Fork`, `Exec`, `Wait`, `Exit`, `SetFocus`.

Also: `MovImmLabel(dest, label)` — loads a label's resolved 8-bit offset as an immediate.

---

## BranchPredictor

| Property/Method | Notes |
|-----------------|-------|
| `DefaultSize` | 64 entries (power of 2; indexed by address/4 & (size-1)) |
| `WeakNotTaken` | 1 (cold default) |
| `TakenThreshold` | 2 (counter ≥ 2 → predict taken) |
| `Predict(int address) → bool` | Current prediction without updating |
| `Record(int address, bool taken) → bool` | Predict + update + count; returns true if hit |
| `Update(int address, bool taken)` | Nudge counter only |
| `CounterAt(int address) → int` | Raw 2-bit counter value |
| `Predictions / Hits / Misses` | Counters |
| `Accuracy` | double in [0,1] |

BHT index = `(address / 4) & (size - 1)`. Two branches at different addresses alias when they map to the same index.

---

## Disassembler

```csharp
string mnemonic = Disassembler.Decode(opcode, b1, b2, b3);
// e.g. "MOV EAX, 5", "STORE [EBX], EAX", "JMP 108", "??? 9F"
```

Unknown opcodes return `"??? XX"` where XX is hex. Pure function, no side effects.
