# CSharpOS Quick Reference

## Solution Structure

| Project | Purpose |
|---------|---------|
| `CSharpOS` | Core library: Hardware, Assembler, OS interfaces, OsLayout, enums, events, Bin disk |
| `BasicOSPlugin` | OS personality loaded via reflection: BasicOS, OsRoutines, trap providers |
| `CSharpOSConsole` | Console host: Spectre.Console TUI, Programs, plugin loader entry point |
| `OSTests` | xUnit tests (references both CSharpOS and BasicOSPlugin directly) |

**Solution file:** `CSharpOS.slnx`

### Key Folders

```
CSharpOS/
  CPU/          Hardware.cs  Hardware.Types.cs  Instruction.cs  InstructionFunctions.cs
                Disassembler.cs  BranchPredictor.cs
  Assembler/    Assembler.cs  Assembler.Types.cs
  OS/           OperatingSystem.cs  IOperatingSystem.cs  OsLayout.cs
                ITrapProvider.cs  OsPluginLoader.cs
  Processes/    Process.cs
  Disk/         Bin.cs
  Enums/        RegisterName.cs  PrivilegeLevel.cs  ProcessState.cs  WaitReason.cs
  Structs/      MemoryRange.cs  Trap.cs
  Events/       *Args.cs (11 event arg classes)
BasicOSPlugin/
  BasicOS.cs
  OsRoutines.cs            partial class — core: BuildOsImage, scheduling, lifecycle, buddy, syscall, helpers
  OsRoutines.Paging.cs     partial — PageFault + frame/swap/COW subs
  OsRoutines.Cache.cs      partial — IvtCacheOp + cache_* subs
  OsRoutines.Fs.cs         partial — IvtFsOp + fs_* block/chain/dir subs
  Traps/  IretTrapProvider.cs  LoadBoundsTrapProvider.cs  StoreBoundsTrapProvider.cs
CSharpOSConsole/
  Visualization/  VisualizerModel.cs  HardwareEventBridge.cs  SpectreDashboard.cs
                  PlainTextRenderer.cs  NoOpRenderer.cs  FrameHistory.cs
                  InteractionController.cs  BuddyHeapView.cs  Pacer.cs
  ConsoleVisualizer.cs  Program.cs  Programs.cs  VisualizerMode.cs
OSTests/
  *Tests.cs  TestSupport.cs (shared Test static class)
```

---

## Where to Find X

| Task | Files to edit (in order) |
|------|--------------------------|
| Add new opcode | `Instruction.cs` (const + opcodeTable entry) → `InstructionFunctions.cs` (handler) → `Assembler.cs` (emit method) → `Disassembler.cs` (Decode case) |
| Add new IVT slot | `Hardware.cs` (IvtXxx const, bump IvtSlotCount, update NameForRoutineSlot) → `OsRoutines.cs` (EmitXxx method + BuildOsImage IVT write) |
| Add new OsLayout offset | `OsLayout.cs` (add field, adjust downstream fields that depend on it, TotalSize updates automatically via chain) |
| Add new ProcessEntry field | `Hardware.cs` (ProcessEntryXxx const, update ProcessEntrySize to next multiple that fits) |
| Add new trap provider | New `public sealed class XxxTrapProvider : ITrapProvider` in `BasicOSPlugin/Traps/` — `CollectTraps()` auto-discovers via reflection, no registration needed |
| Add new event | New `*Args.cs` in `CSharpOS/Events/` → add `event EventHandler<XxxArgs>? XxxHappened` to `Hardware.cs` → fire it → subscribe in `HardwareEventBridge.cs` |
| Add new ISA subroutine | `OsRoutines.cs` — emit with `asm.Label("name")` + `asm.Ret()`, call from `BuildOsImage` after other routines |
| Change quantum table values | `OperatingSystem.SeedOsData()` in `OperatingSystem.cs` |

---

## Opcode Table

All instructions are 4 bytes: `[opcode][b1][b2][b3]`. EFLAGS: bit 0 = Zero, bit 1 = Sign.

| Hex | Constant | Handler | Operands / Notes |
|-----|----------|---------|-----------------|
| 0x01 | MOV_REG_REG | MovRegReg | b1=dst, b2=src |
| 0x02 | MOV_REG_IMM | MovRegImm | b1=dst, b2=imm8 (zero-extended) |
| 0x03 | MOV_REG_IMM16 | MovRegImm16 | b1=dst, value=(b2<<8)\|b3 |
| 0x05 | LOAD | Load | b1=dst, b2=ptr-reg; MMU-translated in User mode |
| 0x06 | STORE | Store | b1=ptr-reg, b2=src; MMU-translated in User mode |
| 0x10 | ADD | Add | b1+=b2; sets flags |
| 0x11 | SUB | Sub | b1-=b2; sets flags |
| 0x12 | MUL | Mul | b1*=b2; sets flags |
| 0x13 | DIV | Div | b1/=b2; sets flags |
| 0x14 | CMP | Cmp | flags from (b1-b2); no write |
| 0x15 | INC | Inc | b1++; sets flags |
| 0x16 | DEC | Dec | b1--; sets flags |
| 0x17 | AND | And | b1&=b2; sets flags |
| 0x18 | OR | Or | b1\|=b2; sets flags |
| 0x19 | XOR | Xor | b1^=b2; sets flags |
| 0x1A | NOT | Not | b1=~b1; sets flags |
| 0x1B | SHL | Shl | b1<<=b2 (masked to low 5 bits); sets flags |
| 0x1C | SHR | Shr | b1>>=b2 logical (unsigned); sets flags |
| 0x20 | JMP | Jmp | IP = programBase + ((b1<<8)\|b2) |
| 0x21 | JZ | Jz | branch-pred scored; taken if ZF=1 |
| 0x22 | JNZ | Jnz | taken if ZF=0 |
| 0x23 | CALL | Call | push return-offset (base-relative) to stack; IP = base+target |
| 0x24 | RET | Ret | pop return-offset; IP = base+offset; stack via MMU |
| 0x25 | JS | Js | taken if SF=1 |
| 0x26 | JNS | Jns | taken if SF=0 |
| 0x30 | OUT | Out | User→EnterKernel(OUT); Kernel→KernelOutput |
| 0x31 | IN | In | User→EnterKernel(IN); Kernel→KernelInput |
| 0x32 | HLT | Hlt | terminate with status 0; dispatches IvtHalt |
| 0x33 | IRET | Iret | return from shared syscall handler; restore saved regs |
| 0x34 | FORK | Fork | dispatches IvtFork |
| 0x35 | EXEC | Exec | b1=slot-reg; dispatches IvtExec |
| 0x36 | WAIT | Wait | b1=pid-reg; dispatches IvtWait |
| 0x37 | EXIT | Exit | b1=status-reg; dispatches IvtHalt(status) |
| 0x38 | SETFOCUS | SetFocus | b1=pid-reg; C# Hardware.SetFocus (no ISA routine) |
| 0x40 | SAVEREGS | SaveRegs | b1=ptr-reg; save trap frame to absolute address; privileged |
| 0x41 | LOADREGS | LoadRegs | b1=ptr-reg; stage entry for OSRET; privileged |
| 0x42 | SETLAYOUT | SetLayout | b1=entry-ptr-reg; rebuild HW layout from entry; privileged |
| 0x43 | OSRET | OsRet | b1=level-reg; commit staged context and drop to level; privileged |
| 0x44 | DREAD | DRead | b1=dest, b2=slot, b3=lenOut; privileged (User→trap) |
| 0x45 | DWRITE | DWrite | b1=slot, b2=src, b3=len; privileged (User→trap) |
| 0x46 | DLEN | DLen | b1=slot, b2=lenOut; privileged (User→trap) |
| 0x47 | OUTS | Outs | b1=ptr-reg (virt addr), b2=len-reg; reads len words→low byte as char→string output; stops at null word; User→EnterKernel(OUTS, b1*4, b2*4) |
| 0x48 | INS | Ins | b1=ptr-reg (virt addr), b2=maxLen-reg; blocks WaitReason.StringInput; writes chars as words + null; User→EnterKernel(INS, b1*4, b2*4) |
| 0x49 | INK | Ink | b1=dst; block until raw keypress; delivers keycode; WaitReason.KeyInput; User→EnterKernel(INK, b1*4, 0) |
| 0x4A | INPOLL | InkPoll | b1=dst; non-blocking; delivers keycode or -1 if empty; never blocks; User→EnterKernel(INPOLL, b1*4, 0) |
| 0x4B | FBREAD | FbRead | b1=dest (RAM addr), b2=block; copy FileBlockSize bytes file-block→RAM; privileged (User→invalid trap) |
| 0x4C | FBWRITE | FbWrite | b1=block, b2=src (RAM addr); copy FileBlockSize bytes RAM→file-block; privileged (User→invalid trap) |

*(0x4D reserved for FSYS — Increment 5.)*

---

## IVT Slot Table

`IvtSlotCount=19`, `IvtSize=76`, `CodeBase=76`

| Slot | Constant | C# addr field | Emit Method | Purpose |
|------|----------|--------------|-------------|---------|
| 0 | IvtContextSwitch | — | EmitContextSwitch | Tick, MLFQ demote/boost → resume_mlfq |
| 1 | IvtHalt | — | EmitHalt | HLT/EXIT → exit_body |
| 2 | IvtInvalidInstruction | — | EmitInvalidInstruction | Fault → exit_body (status -1) |
| 3 | IvtWakeInput | — | EmitWakeEntry(Input) | Wake first process waiting on input device |
| 4 | IvtWakeOutput | — | EmitWakeEntry(Output) | Wake process after output completes |
| 5 | IvtBlockInput | — | EmitBlock | Block current process on input/string/key (shared) |
| 6 | IvtBlockOutput | — | EmitBlock | Block current process on output |
| 7 | IvtSchedule | — | EmitSchedule | Pick next Ready process → resume_mlfq |
| 8 | IvtAllocate | — | EmitBuddyAlloc | Buddy-alloc for staged entry (EAX=entry addr) |
| 9 | IvtDiskLoad | — | EmitDiskLoad | DREAD program slot→RAM for staged entry |
| 10 | IvtFork | — | EmitFork | Duplicate running process (COW data pages) |
| 11 | IvtExec | — | EmitExec | Replace image; EAX=slot; resolve_cow+free+realloc |
| 12 | IvtWait | — | EmitWait | Block until child PID terminates; EAX=pid |
| 13 | IvtSpawn | — | EmitSpawn | Alloc+DREAD+seed regs (boot path) |
| 14 | IvtSyscall | — | EmitSyscall | Shared IN/OUT/INK/INPOLL handler (preemptible, Kernel mode) |
| 15 | IvtPageFault | — | EmitPageFault | Demand fault-in + COW-write resolve; EAX=page |
| 16 | IvtWakeKey | — | EmitWakeEntry(KeyInput) | Wake first process waiting for a raw keypress |
| 17 | IvtCacheOp | — | EmitCacheOp | FS buffer-cache control: EAX=op, EBX=block → result in CacheResult |
| 18 | IvtFsOp | — | EmitFsOp | FS block layer: EAX=op, EBX=block, ECX=next → result in FsResult |

Slots 5+6 both point to the same `EmitBlock` routine; slot 5 is also used for KeyInput blocking. IvtSyscall is jumped-to directly by `EnterKernel`, not dispatched (so interrupts stay enabled). IvtCacheOp op selectors (EAX): 0=Get, 1=Dirty, 2=WriteThrough, 3=Pin, 4=Unpin, 5=Discard, 6=Flush (see `Hardware.CacheOp*`). IvtFsOp op selectors (EAX): 0=Format, 1=AllocBlock, 2=FreeBlock, 3=ChainNext, 4=ChainSetNext, 5=RootDir, 6=Hash, 7=Lookup(EBX=dir,ECX=name), 8=Insert(EBX=dir,ECX=name,EDX=type,ESI=first), 9=Remove (see `Hardware.FsOp*`).

---

## OsLayout Offsets

`DataBase = 12288`

### Header (relative to DataBase)

| Field | Absolute Addr | +offset |
|-------|--------------|---------|
| ProcessCountOffset | 12288 | +0 |
| CurrentIndexOffset | 12292 | +4 |
| BuddyHeapStartOffset | 12296 | +8 |
| BuddyHeapSizeOffset | 12300 | +12 |
| BoostTimerOffset | 12304 | +16 |
| QuantumTableOffset | 12308 | +20 (4 × 4-byte thresholds: L0=1, L1=2, L2=4, L3=255) |
| BuddyMinBlockOffset | 12324 | +36 |
| BuddyLevelsOffset | 12328 | +40 |
| NextPidOffset | 12332 | +44 |
| ProcessTableOffset | 12336 | +48 (MaxProcesses=8 × ProcessEntrySize=192 = 1536 bytes) |

### After Process Table

*(All addresses below shifted +128 from the pre-Inc-0 layout: process table grew 1408→1536 when FdCount went 4→8.)*

| Region | Absolute Addr | Size |
|--------|--------------|------|
| BuddyBitmapOffset | 13872 | 32 bytes (BuddyBitmapWords=8 × 4) |
| PrivilegedStackOffset | 13904 | 64 bytes |
| PrivilegedStackTop | 13968 | — |
| PageTableBase | 13968 | PageTableRegionSize=2048 (8 procs × 64 pages × 4 bytes) |
| FrameTableBase | 16016 | FrameTableSize=128 (FrameCount=4 × 32 bytes each) |
| FramePoolBase | 16144 | FramePoolSize=1024 (4 frames × PageSize=256) |
| ZeroPageBase | 17168 | 256 bytes (always-zero DWRITE source) |
| SwapScratchBase | 17424 | 256 bytes (fork slot-copy transfer buffer) |
| CowPartnerBase | 17680 | 32 bytes (8 procs × 4 bytes each) |
| CacheClockOffset | 17712 | 4 (LRU stamp counter) |
| CacheFlushTimerOffset | 17716 | 4 (periodic-flush countdown) |
| CacheResultOffset | 17720 | 4 (IvtCacheOp return slot) |
| CacheSlotTableBase | 17724 | CacheRegionSize=3588 (CacheSlotCount=13 × CacheSlotSize=276) |
| FsResultOffset | 21312 | 4 (IvtFsOp return slot) |
| FsScratchBase | 21316 | 32 (FsScratchWords=8; dir routines spill cross-cache-call state here) |
| **TotalSize** | **21348** | — |

`PageTableAddress(i) = 13968 + i * 256`
`FrameTableEntry(f) = 16016 + f * 32`
`FrameBase(f) = 16144 + f * 256`
`SwapSlot(proc, page) = 64 + proc * 64 + page`
`CowPartnerAddress(i) = 17680 + i * 4`
`CacheSlotAddress(i) = 17724 + i * 276`

### Cache Slot Fields (276 bytes per slot)
| Offset | Field | Notes |
|--------|-------|-------|
| 0 | CacheValidField | 0=empty (zero-init = whole pool empty), 1=holds a block |
| 4 | CacheBlockField | file-block number cached here |
| 8 | CacheDirtyField | 1=modified; written back on evict/flush (write-back) |
| 12 | CachePinField | pin count; a pinned slot is never an eviction victim |
| 16 | CacheStampField | LRU stamp (manager writes CacheClock on each access) |
| 20 | CacheDataField | the block's bytes (FileBlockSize=256) |

---

## ProcessEntry Field Map

`ProcessEntrySize = 192`; `ProcessEntryAddress(i) = 12336 + i * 192`

| Byte Offset | Field Constant | Size | Notes |
|-------------|---------------|------|-------|
| 0–95 | ProcessEntryRegisterFile | 96 | 24 registers × 4 bytes; EIP slot holds saved IP |
| 96 | ProcessEntryLevel | 4 | PrivilegeLevel (User=0, Kernel=1) |
| 100 | ProcessEntryState | 4 | ProcessState |
| 104 | ProcessEntryWaitReason | 4 | WaitReason |
| 108 | ProcessEntryProgramAddress | 4 | absolute base in RAM |
| 112 | ProcessEntryProgramSize | 4 | bytes |
| 116 | ProcessEntryRequiredMemory | 4 | data heap bytes |
| 120 | ProcessEntryRequiredStackSize | 4 | user stack bytes |
| 124 | ProcessEntryTotalSize | 4 | prog+mem+stack+KernelStackSize |
| 128 | ProcessEntryPriority | 4 | MLFQ level 0–3 (0=highest) |
| 132 | ProcessEntryTicksUsed | 4 | ticks used at current MLFQ level |
| 136 | ProcessEntryPid | 4 | monotonic PID |
| 140 | ProcessEntryParentPid | 4 | -1 if no parent |
| 144 | ProcessEntryWaitTarget | 4 | PID being waited on; -1 otherwise |
| 148 | ProcessEntryExitStatus | 4 | |
| 152 | ProcessEntryDiskSlot | 4 | Bin disk slot for program image |
| 156 | (spare) | 4 | |
| 160 | ProcessEntryFdTable | 32 | FdCount=8 × 4-byte device ids; [0]=stdin, [1]=stdout, [2..7]=open files |
| **192** | **(end)** | | |

Process memory layout: `[program][memory][user stack][kernel stack]`
`TotalSize = ProgramSize + RequiredMemory + RequiredStackSize + KernelStackSize(176)`

---

## Register File

24 registers; byte offset in register file = `(int)RegisterName.X * 4`

| Register | Index | Byte Offset | Role |
|----------|-------|-------------|------|
| EAX | 0 | 0 | general / return value / routine arg |
| EBX | 1 | 4 | OS: current entry address |
| ECX | 2 | 8 | OS: current process index |
| EDX | 3 | 12 | OS: wait reason / scan counter |
| ESI | 4 | 16 | OS: scan counter |
| EDI | 5 | 20 | OS: process count |
| ESP | 6 | 24 | stack pointer (base-relative in User mode) |
| EBP | 7 | 28 | kernel: trap-frame base pointer |
| EIP | 8 | 32 | instruction pointer (saved as base-relative offset) |
| EFLAGS | 9 | 36 | bit 0=Zero, bit 1=Sign |
| CS | 10 | 40 | segment (unused by instructions) |
| DS | 11 | 44 | segment |
| ES | 12 | 48 | segment |
| FS | 13 | 52 | segment |
| GS | 14 | 56 | segment |
| SS | 15 | 60 | segment |
| R8 | 16 | 64 | OS scratch (MLFQ TicksUsed, frame index, etc.) |
| R9 | 17 | 68 | OS scratch |
| R10 | 18 | 72 | OS scratch |
| R11 | 19 | 76 | OS scratch |
| R12 | 20 | 80 | OS scratch (paging: faulting page, COW page) |
| R13 | 21 | 84 | OS scratch (paging: current index, swap slot) |
| R14 | 22 | 88 | OS scratch (paging: swap slot of faulting page) |
| R15 | 23 | 92 | OS scratch (paging: chosen frame; COW: page scan) |

Register file size = 96 bytes. `hw.GetRegisterOffset(RegisterName.X)` returns byte offset.

---

## Key Constants

### Scheduler / MLFQ
| Constant | Value | Location |
|----------|-------|----------|
| SchedulerInstructionCount | 30 | Hardware.cs (private) |
| QueueCount | 4 | OsLayout.cs |
| BoostInterval | 20 | OsLayout.cs |
| Quantum L0/L1/L2/L3 | 1/2/4/255 | seeded in OperatingSystem.SeedOsData |

### Kernel Stack
| Constant | Value |
|----------|-------|
| KernelTrapInfoOffset | 96 |
| KernelTrapInfoSize | 16 |
| KernelHeaderSize | 112 |
| KernelStackSize | 176 (= KernelHeaderSize + 64) |
| KernelSaveAreaOffset | 0 |

### Branch Predictor
| Constant | Value |
|----------|-------|
| BranchPredictor.DefaultSize | 64 entries |
| TakenThreshold | 2 (counter ≥ 2 → predict taken) |
| WeakNotTaken (cold) | 1 |
| MispredictPenalty | 3 observational cycles (private to Hardware) |
| Scored | User-mode branches only (not OS, not Kernel-mode syscall) |

### Paging
| Constant | Value |
|----------|-------|
| PageSize | 256 |
| MaxPagesPerProcess | 64 |
| PageTableEntryBytes | 4 |
| FrameCount | 4 |
| FrameTableEntryBytes | 32 |
| UnmappedPage | -1 |
| NonResidentPage | -2 |
| SwapPteBias | 3 |
| SwapCowBias | 4096 |

### Disk
| Constant | Value |
|----------|-------|
| DefaultDiskSlots | 64 (image slots) |
| DefaultDiskSlotSize | 1024 bytes |
| SwapBase | 64 (swap slots start after image slots) |
| SwapSlotsPerProcess | 64 |
| SwapSlotCount | 512 (8 procs × 64 pages) |
| DiskDeviceId | 256 |
| Total disk slots (default) | 576 (64 + 512) |
| DefaultFileBlockSize | 256 bytes (== PageSize by convention) |
| DefaultFileBlockCount | 256 (file-block region; 64 KiB file space) |

**File-block region** (Inc 1): a second backing store inside the same `Bin`, block-addressed (not slot-addressed). `Bin.ReadFileBlock(block)` reads a fresh copy (zeros if never written — raw block-device semantics, never throws for empty); `Bin.WriteFileBlock(block, data)` requires exactly `FileBlockSize` bytes. `Bin.Save(path)`/`Load(path)` persist **only** the file-block region (magic `0x43534653` "CSFS" + geometry header) — images rebuild from programs at boot, swap is transient. Moved via privileged `FBREAD`/`FBWRITE` (0x4B/0x4C). Default disk is now `Bin(576, 1024, 256, 256)`.

### Filesystem Cache (Inc 2)
| Constant | Value |
|----------|-------|
| CacheSlotCount | 13 (≈ DefaultFileBlockCount/20) |
| CacheSlotSize | 276 (20-byte header + 256 data) |
| CacheFlushInterval | 200 (context-switch ticks between periodic flushes) |

ISA write-back cache in the OS region managed by `cache_*` subroutines via `IvtCacheOp`. LRU eviction (invalid slot first, else lowest stamp among **unpinned**), write-back on evict, dirty write-back on periodic flush (hooked into ContextSwitch at `cs_skip`) and on whole-cache `cache_flush`. `cache_discard` drops a block without write-back (clears valid+dirty). `cache_write_through` flushes one block now and leaves it clean.

### FsLayout — on-disk structure (Inc 3, `CSharpOS/Disk/FsLayout.cs`)
Distinct from OsLayout (OS RAM). File-block region blocks:
| Block | Role |
|-------|------|
| 0 (SuperBlock) | magic 0x5346 @0, BlockCount @4, FreeCount @8, RootDir @12 (Inc 4) |
| 1 (BitmapBlock) | free bitmap, 256 bits = BitmapWords=8 words; bit=1 → allocated |
| 2+ (FirstDataBlock) | allocatable data blocks |

Each block: payload bytes 0..251, `NextPtrOffset=252` holds the next-block link (`EndOfChain=-1`) for free-chaining. Block layer is ISA `fs_*` subroutines via `IvtFsOp`, all through the cache: `fs_format` (bits 0,1 set, allocs root dir → block 2 stored @ SuperRootDirOffset), `fs_alloc_block` (scan bitmap for a clear bit → set + init next=-1), `fs_free_block` (clear bit + cache_discard), `fs_chain_next`/`fs_chain_set_next`. **Convention:** EDX/EDI carry a value across `cache_*` calls (the cache subroutines never touch EDX/EDI); state that must survive `fs_alloc_block`/`fs_chain_set_next` (which do clobber EDX/EDI) is spilled to `OsLayout.FsScratch*`.

**Directory entries (Inc 4a):** a directory is a block chain; `DirEntryBytes=64`, `DirEntriesPerBlock=3`. Entry: `type`@0 (0=free,1=file,2=dir) · `hash`@4 · `firstBlock`@8 · `size`@12 · `name`@16 (`NameMaxChars=12` words, word-per-char, null-padded). ISA dir routines via `IvtFsOp`: `fs_hash` (h=h*31+c), `fs_root_dir`, `fs_dir_lookup` (hash-reject then name-verify; stashes matched block in FsScratchEntryBlock), `fs_dir_insert` (dup-reject via lookup, find free slot or extend chain), `fs_dir_remove` (type=free). **Nested dirs / path traversal = Inc 4b (pending).**

### Buddy Allocator
| Constant | Value |
|----------|-------|
| BuddyBitmapWords | 8 |
| BuddyDefaultMinBlock | 256 |
| MaxProcesses | 8 |

### Raw Key Codes (INK / INPOLL)
ASCII 32–126 delivered as-is. Special keys use values above the ASCII range:
| Constant | Value |
|----------|-------|
| Hardware.KeyUp | 256 |
| Hardware.KeyDown | 257 |
| Hardware.KeyLeft | 258 |
| Hardware.KeyRight | 259 |

---

## Enums

### ProcessState
| Value | Numeric | Meaning |
|-------|---------|---------|
| Ready | 0 | Running or schedulable |
| Blocked | 1 | Waiting on an I/O device; skipped by scheduler |
| Terminated | 2 | Slot is free; scheduler ignores it |
| Zombie | 3 | Terminated but not reaped; holds Pid/ParentPid/ExitStatus for parent's wait |

### WaitReason
| Value | Numeric | Meaning |
|-------|---------|---------|
| None | 0 | Not blocked |
| Input | 1 | Waiting on `IN` (stdin buffered by device) |
| Output | 2 | Waiting on `OUT` (output device busy) |
| ChildProcess | 3 | Blocked in `WAIT(pid)` for a child to terminate |
| StringInput | 4 | Waiting on `INS` (string line on stdin string queue) |
| KeyInput | 5 | Waiting on `INK` (raw keypress on stdin key queue) |

### PrivilegeLevel
| Value | Numeric |
|-------|---------|
| User | 0 |
| Kernel | 1 |

---

## Privilege Model

Two privilege levels: `PrivilegeLevel.User = 0`, `PrivilegeLevel.Kernel = 1`.

Atomicity is the **hardware interrupt-enable flag** (`hw.InterruptsEnabled()`), not a privilege level.

| Mode | PrivilegeLevel | InterruptsEnabled | How entered |
|------|---------------|-------------------|-------------|
| User process | User | true | OSRET with level=User |
| Syscall handler | Kernel | true | EnterKernel (IN/OUT trap); preemptible |
| OS routine (IVT) | Kernel | **false** | DispatchOsRoutine; atomic |

**Run loop gate** (`Hardware.Run`):
1. `!interruptsEnabled` → step atomic OS routine
2. `TryDispatchPendingInterrupt()` → wake routine entered
3. `!processRunning` → dispatch IvtSchedule
4. else → step current process; preempt at SchedulerInstructionCount=30

**Program base:** User=`currentProcessInstructionStart`; Kernel=0 (absolute). JMP/CALL/RET targets are base-relative; saved EIP stored base-relative (position-independent).

---

## Session Cost Log

Track token usage per session to catch efficiency regressions early. Update at end of each session.
Inline work token counts are estimates; fork/agent counts come from the task notification.

| Date | Task | Tokens (est.) | Notes |
|------|------|:-------------:|-------|
| 2026-06-29 | Build all CLAUDE.md reference files | ~143K (fork) | Cold scan of full source; one-time cost |
| 2026-06-29 | Add section markers to OsRoutines/Hardware/InstructionFunctions/SpectreDashboard | ~115K (fork) | 71 markers across 4 files; CLAUDE.md tables updated with line numbers |
| 2026-06-30 | Visualizer fixes: run loop, termination display, process state refresh | ~18K (inline) | 6 targeted reads (sections by line offset); no full-file scans needed |
| 2026-06-30 | OUTS + INS string I/O: 18 files modified, 2 new tests, option 12 demo | ~35K (inline) | Resumed from context summary; full implementation in one session |
| 2026-06-30 | Process tree panel + option 11 spawn demo | ~22K (inline) | ProcessRow Pid/ParentPid, BuildProcessTree, SpawnChildren (3 children), WAIT-clobbers-EAX bug found via OsRoutines read |
| 2026-06-30 | INK + INPOLL raw key input: IvtSlotCount 16→17, WaitReason.KeyInput=5, 3 new syscall tests, 5 revised arrow-key tests | ~20K (inline) | CodeBase 64→68; arrow keys route to process when running, scrub history when paused |
| 2026-06-30 | Key routing fix + F1 passthrough toggle: command keys (a/s/o/q) no longer leak to process; F1 toggles full keyboard passthrough to process buffer | ~10K (inline) | Resumed from context summary; 6 new passthrough tests; 506 total. PR merged to master. |
| 2026-07-01 | Filesystem roadmap Inc 0 (FdCount 4→8, ProcessEntrySize 176→192) + Inc 1 (Bin file-block region + .bin persistence + FBREAD/FBWRITE 0x4B/0x4C) | ~30K (inline) | Traced full syscall path first (EnterKernel/EmitSyscall) to de-risk plan. 26 new tests (532 total). ISA filesystem chosen over C#. |
| 2026-07-01 | Filesystem Inc 2: RAM write-back cache + ISA cache manager (IvtCacheOp slot 17, IVT 17→18, CodeBase 68→72; cache_find/get/dirty/write_through/pin/unpin/discard/flush; LRU+write-back+pin; periodic flush hooked into ContextSwitch) | ~40K (inline) | 12 new tests (544 total), all passed first run. Write-through included per user. TotalSize 17712→21312. |
| 2026-07-01 | Filesystem Inc 3: ISA block allocator + free-chaining (FsLayout.cs on-disk struct; IvtFsOp slot 18, IVT 18→19, CodeBase 72→76; fs_format/alloc_block/free_block/chain_next/chain_set_next, all through the cache) | ~35K (inline) | 10 new tests (554 total), all passed first run. ISA format routine chosen. EDX/EDI carry values across cache_* calls. |
| 2026-07-01 | Filesystem Inc 4a: ISA directory layer (word-per-char names; FsScratch spill region; fs_hash/root_dir/dir_lookup/dir_insert/dir_remove; dup rejection, chain extension). Format now allocs root dir @block 2 | ~45K (inline) | 10 new dir tests + 6 Inc-3 tests updated for root-dir reservation (564 total), all green. Heavy register-spill-to-memory pattern. Nested/path traversal deferred to Inc 4b. |
| 2026-07-01 | Refactor: split OsRoutines.cs (3021 lines) into partial-class files (core + Paging + Cache + Fs) via scripted line-slicing; CLAUDE index switched from drifting line numbers to Grep-stable marker names + file map | ~15K (inline) | Pure code motion, 564 tests still green. FS work now opens OsRoutines.Fs.cs (~520 lines) not the whole file. Scripted with PowerShell to keep file content out of context. |

**Red flag:** any single planning/implementation task exceeding ~50K tokens — investigate what was being re-scanned and add it to CLAUDE.md or markers.

---

## Paging PTE Encoding

| PTE value | Meaning |
|-----------|---------|
| ≥ 0 | Resident; value = frame's physical base in FramePool |
| -1 (UnmappedPage) | Outside process; linear fallback |
| -2 (NonResidentPage) | RAM-home code/stack page; not in a frame |
| ≤ -3 (SwapPte) | Private swap-backed DATA page; slot = -pte - 3 |
| ≤ -4096 (CowPte) | COW-shared DATA page; slot = -pte - 4096 |

**Resident COW write** → MMU re-raises IvtPageFault; handler calls `pair_resolve`.
**Test order:** IsCowPte first (more negative), then IsSwapPte.

### Frame Table Entry (32 bytes per frame)
| Offset | Field | Notes |
|--------|-------|-------|
| 0 | FrameOccupiedField | 0=free, 1=in use |
| 4 | FrameOwnerProcField | process-table index |
| 8 | FrameOwnerPageField | virtual page number |
| 12 | FrameHomeField | RAM-home block addr (RAM pages); 0 for swap pages |
| 16 | FrameDirtyField | 0=clean, 1=needs write-back on evict |
| 20 | FrameLastUseField | LRU stamp (C# MMU increments pageClock on each access) |
| 24 | FrameSwapField | swap slot (-1 for RAM-home frames) |
| 28 | FrameCowField | 1 = read-only COW share (write traps) |
