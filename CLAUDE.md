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
  Traps/  IretTrapProvider.cs  (Load/StoreBoundsTrapProvider removed in Phase 2 — MMU is sole memory protection)
CSharpOSConsole/
  Visualization/  VisualizerModel.cs  HardwareEventBridge.cs  SpectreDashboard.cs
                  PlainTextRenderer.cs  NoOpRenderer.cs  FrameHistory.cs
                  InteractionController.cs  BuddyHeapView.cs  Pacer.cs
  ConsoleVisualizer.cs  Program.cs  Programs.cs  VisualizerMode.cs
OSTests/
  CLAUDE.md (test navigation index: subsystem→file jump table, shared helpers, conventions)
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
| 0x4D | FSYS | Fsys | user filesystem syscall; EAX=syscall#, EBX/ECX/EDX=args; dispatches IvtFsSyscall (like FORK), result delivered in EAX |

---

## IVT Slot Table

`IvtSlotCount=19`, `IvtSize=76`, `CodeBase=76` (was 20/80/80 before Inc 6 rectification removed dead IvtDiskLoad; slots 9–18 shifted down one)

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
| 8 | IvtAllocate | — | EmitBuddyAlloc | Buddy-alloc for staged entry (EAX=entry addr); allocator test entry point |
| 9 | IvtFork | — | EmitFork | Duplicate running process (COW data pages) |
| 10 | IvtExec | — | EmitExec | Replace image; EAX=slot; resolve_cow+free+realloc |
| 11 | IvtWait | — | EmitWait | Block until child PID terminates; EAX=pid |
| 12 | IvtSpawn | — | EmitSpawn | Alloc+DREAD+seed regs (boot path) |
| 13 | IvtSyscall | — | EmitSyscall | Shared IN/OUT/INK/INPOLL handler (preemptible, Kernel mode) |
| 14 | IvtPageFault | — | EmitPageFault | Demand fault-in + COW-write resolve; EAX=page |
| 15 | IvtWakeKey | — | EmitWakeEntry(KeyInput) | Wake first process waiting for a raw keypress |
| 16 | IvtCacheOp | — | EmitCacheOp | FS buffer-cache control: EAX=op, EBX=block → result in CacheResult |
| 17 | IvtFsOp | — | EmitFsOp | FS block/dir/file layer: EAX=op, args in EBX/ECX/EDX/ESI → result in FsResult |
| 18 | IvtFsSyscall | — | EmitFsSyscall | FSYS user syscall: EAX=syscall#, args EBX/ECX/EDX; delivers result in caller EAX |

**Note:** SETFOCUS (0x38) has no IVT slot / ISA routine — it's intentionally C#-only (`Hardware.SetFocus`), because "focused process" is a hardware-side field (`activeProcess`) with no OS-memory representation; it's a device/foreground concern, not an OS service.

Slots 5+6 both point to the same `EmitBlock` routine; slot 5 is also used for KeyInput blocking. IvtSyscall is jumped-to directly by `EnterKernel`, not dispatched (so interrupts stay enabled). IvtCacheOp op selectors (EAX): 0=Get, 1=Dirty, 2=WriteThrough, 3=Pin, 4=Unpin, 5=Discard, 6=Flush (see `Hardware.CacheOp*`). IvtFsOp op selectors (EAX): 0=Format, 1=AllocBlock, 2=FreeBlock, 3=ChainNext, 4=ChainSetNext, 5=RootDir, 6=Hash, 7=Lookup(EBX=dir,ECX=name), 8=Insert(EBX=dir,ECX=name,EDX=type,ESI=first), 9=Remove, 10=Mkdir(EBX=parent,ECX=name), 11=PathResolve(EBX=path), 12=Open(EBX=absPath,ECX=flags,EDX=proc), 13=Close(EBX=fd,ECX=proc), 14=Read(EBX=fd,ECX=absBuf,EDX=count,ESI=proc), 15=Write(EBX=fd,ECX=absBuf,EDX=count,ESI=proc), 16=Unlink(EBX=absPath), 17=MkdirPath(EBX=absPath), 18=ReadDir(EBX=dirBlock,ECX=index,EDX=absOut → type or -1) (see `Hardware.FsOp*`). FSYS syscall numbers (EAX): 0=Open(EBX=pathPtr,ECX=flags), 1=Read(EBX=fd,ECX=bufPtr,EDX=count), 2=Write(EBX=fd,ECX=bufPtr,EDX=count), 3=Close(EBX=fd), 4=Exec(EBX=pathPtr) — replace the running image with the FS file, no return on success/ -1 on failure (Inc 6), 5=Unlink(EBX=pathPtr), 6=Mkdir(EBX=pathPtr → new dir block/-1), 7=Readdir(EBX=dirPathPtr,ECX=index,EDX=outPtr → type/-1; "/"=root) (Phase 1); flag `FsysCreateFlag=1`. IvtFsOp Open/Close/Read/Write take an ABSOLUTE path/buffer + explicit proc index; FSYS translates the user pointer (ProgramAddress+ptr) and uses the current process.

---

## OsLayout Offsets

`DataBase = 20480` (raised from 16384 in Inc 6 once file read/write + exec-by-path pushed the OS code to ~16.7 KB). **Addresses below are given as `DataBase + N` (DataBase-relative) so a future DataBase bump doesn't invalidate this table** — the earlier drift taught us not to hardcode absolutes. To get an absolute address, add DataBase. Offsets shift only if an *earlier* field's size changes.

### Header

| Field | Offset from DataBase |
|-------|------|
| ProcessCountOffset | +0 |
| CurrentIndexOffset | +4 |
| BuddyHeapStartOffset | +8 |
| BuddyHeapSizeOffset | +12 |
| BoostTimerOffset | +16 |
| QuantumTableOffset | +20 (4 × 4-byte thresholds: L0=1, L1=2, L2=4, L3=255) |
| BuddyMinBlockOffset | +36 |
| BuddyLevelsOffset | +40 |
| NextPidOffset | +44 |
| ProcessTableOffset | +48 (MaxProcesses=8 × ProcessEntrySize=192 = 1536 bytes) |

### After Process Table

| Region | Offset from DataBase | Size |
|--------|------|------|
| BuddyBitmapOffset | +1584 | 32 bytes (BuddyBitmapWords=8 × 4) |
| PrivilegedStackOffset | +1616 | 64 bytes |
| PrivilegedStackTop / PageTableBase | +1680 | PageTableRegionSize=4096 (8 procs × **128** pages × 4 bytes) — doubled in Phase 2 |
| FrameTableBase | +5776 | FrameTableSize=128 (FrameCount=4 × 32 bytes each) |
| FramePoolBase | +5904 | FramePoolSize=1024 (4 frames × PageSize=256) |
| ZeroPageBase | +6928 | 256 bytes (always-zero DWRITE source) |
| SwapScratchBase | +7184 | 256 bytes (fork slot-copy transfer buffer) |
| CowPartnerBase | +7440 | 32 bytes (8 procs × 4 bytes each) |
| CacheClockOffset / FlushTimer / Result | +7472 / +7476 / +7480 | 3 × 4 (cache header) |
| CacheSlotTableBase | +7484 | CacheRegionSize=3588 (CacheSlotCount=13 × CacheSlotSize=276) |
| FsResultOffset | +11072 | 4 (IvtFsOp return slot) |
| FsScratchBase | +11076 | 40 (FsScratchWords=10: Name/Hash/Type/First/Dir/EntryBlock/FreeBlock/ArgA/ArgB + spare) |
| FsPathBase | +11116 | FsPathPos/Dir/Last (+0/+4/+8) then FsPathComponentBase (+12, NameMaxChars words) |
| OftBase | +11176 | OftRegionSize=192 (MaxOpenFiles=8 × OftEntryBytes=24: inUse/firstBlock/offset/size/dirBlock/entryOffset) |
| FsOpenBase | +11368 | 32 (fs_open_core spill scratch: absPath/flags/proc/entryAddr/first/size/dirBlock/entryOffset) |
| FsRwBase | +11400 | 48 (FsRwWords=12: fs_read_core/fs_write_core spill: Fd/Buf/Count/Proc/Oft/CurBlock/Remaining/CharInBlock/BufPtr/Copied/Counter + spare) |
| **TotalSize** | **+11448** (= 31928 abs, with DataBase 20480) | — |

`OftAddress(i) = OftBase + i * 24`. A process fd slot (2..7) holds `OFT index + 1` (0 = free), so a zeroed fd table = no open files (no seeding).

`SwapSlot(proc, page) = 64 + proc * 128 + page` (disk slot, DataBase-independent; stride = SwapSlotsPerProcess = MaxPagesPerProcess = 128). `PageTableAddress(i)`, `FrameTableEntry(f)`, `FrameBase(f)`, `CowPartnerAddress(i)`, `CacheSlotAddress(i)` are all `OsLayout.<Base> + i * <stride>` — read the stride from the region's Size column.

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

`ProcessEntrySize = 192`; `ProcessEntryAddress(i) = ProcessTableOffset + i * 192 = (DataBase + 48) + i * 192` (DataBase-relative so it survives DataBase bumps)

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
| MaxPagesPerProcess | 128 (= 32 KiB mapped user space/process; raised from 64 in Phase 2) |
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
| SwapSlotsPerProcess | 128 |
| SwapSlotCount | 1024 (8 procs × 128 pages) |
| DiskDeviceId | 256 |
| Total disk slots (default) | 1088 (64 + 1024) |
| DefaultFileBlockSize | 256 bytes (== PageSize by convention) |
| DefaultFileBlockCount | 256 (file-block region; 64 KiB file space) |

**File-block region** (Inc 1): a second backing store inside the same `Bin`, block-addressed (not slot-addressed). `Bin.ReadFileBlock(block)` reads a fresh copy (zeros if never written — raw block-device semantics, never throws for empty); `Bin.WriteFileBlock(block, data)` requires exactly `FileBlockSize` bytes. `Bin.Save(path)`/`Load(path)` persist **only** the file-block region (magic `0x43534653` "CSFS" + geometry header) — images rebuild from programs at boot, swap is transient. Moved via privileged `FBREAD`/`FBWRITE` (0x4B/0x4C). Default disk is now `Bin(1088, 1024, 256, 256)` (64 image + 1024 swap slots).

### Filesystem Cache (Inc 2)
| Constant | Value |
|----------|-------|
| CacheSlotCount | 13 (≈ DefaultFileBlockCount/20) |
| CacheSlotSize | 276 (20-byte header + 256 data) |
| CacheFlushInterval | 200 (context-switch ticks between periodic flushes) |

ISA write-back cache in the OS region managed by `cache_*` subroutines via `IvtCacheOp`. LRU eviction (invalid slot first, else lowest stamp among **unpinned**), write-back on evict, dirty write-back on periodic flush (hooked into ContextSwitch at `cs_skip`) and on whole-cache `cache_flush` (which writes back **all** dirty valid slots, pinned included — pinning blocks eviction only). `cache_discard` drops a block without write-back (clears valid+dirty). `cache_write_through` flushes one block now and leaves it clean.

### FsLayout — on-disk structure (Inc 3, `CSharpOS/Disk/FsLayout.cs`)
Distinct from OsLayout (OS RAM). File-block region blocks:
| Block | Role |
|-------|------|
| 0 (SuperBlock) | magic 0x5346 @0, BlockCount @4, FreeCount @8, RootDir @12 (Inc 4) |
| 1 (BitmapBlock) | free bitmap, 256 bits = BitmapWords=8 words; bit=1 → allocated |
| 2+ (FirstDataBlock) | allocatable data blocks |

Each block: payload bytes 0..251, `NextPtrOffset=252` holds the next-block link (`EndOfChain=-1`) for free-chaining. File **content** is stored word-per-char (`CharsPerBlock = PayloadBytes/4 = 63` chars per block, matching the OUTS/INS convention), so read/write copy with word LOAD/STORE loops. Block layer is ISA `fs_*` subroutines via `IvtFsOp`, all through the cache: `fs_format` (bits 0,1 set, allocs root dir → block 2 stored @ SuperRootDirOffset), `fs_alloc_block` (scan bitmap for a clear bit → set + init next=-1), `fs_free_block` (clear bit + cache_discard), `fs_chain_next`/`fs_chain_set_next`. **Convention:** EDX/EDI carry a value across `cache_*` calls (the cache subroutines never touch EDX/EDI); state that must survive `fs_alloc_block`/`fs_chain_set_next` (which do clobber EDX/EDI) is spilled to `OsLayout.FsScratch*`.

**Directory entries (Inc 4a):** a directory is a block chain; `DirEntryBytes=64`, `DirEntriesPerBlock=3`. Entry: `type`@0 (0=free,1=file,2=dir) · `hash`@4 · `firstBlock`@8 · `size`@12 · `name`@16 (`NameMaxChars=12` words, word-per-char, null-padded). ISA dir routines via `IvtFsOp`: `fs_hash` (h=h*31+c), `fs_root_dir`, `fs_dir_lookup` (hash-reject then name-verify; stashes matched block in FsScratchEntryBlock), `fs_dir_insert` (dup-reject via lookup, find free slot or extend chain), `fs_dir_remove` (type=free).

**Nested dirs + path traversal (Inc 4b, in `OsRoutines.Fs.cs`):** `fs_mkdir` (alloc a dir block + insert a `type=dir` entry; frees the block if the name dups), `fs_path_resolve` (walk `/a/b/c` word-per-char, descending only through `type=dir` entries), `fs_extract_component` (pull one component into `FsPathComponentBase`). Path-resolve keeps all loop state in `OsLayout.FsPath*` memory (lookup clobbers registers between components); `/` separator, trailing slashes resolve the last component, empty/`/`-only path → -1.

**File syscalls (Inc 5a, in `OsRoutines.Fs.cs`):** `FSYS` (0x4D) → `IvtFsSyscall` (atomic dispatch, like FORK) → `fs_open_core`/`fs_close_core`. OPEN resolves the path (creates via `fs_create_file`→`fs_resolve_parent` if missing and `FsysCreateFlag` set), rejects directories, fills an OFT slot (`oft_alloc`) with firstBlock/offset/size + the dir-entry location, and stores `OFT index+1` in the lowest free fd (2..7). CLOSE clears the fd + OFT slot. The FSYS wrapper resumes the caller with the result in EAX via the SAVEREGS→entry.EAX→LOADREGS→OSRET idiom (EmitWait's reap path). Note: boot does not yet auto-format — tests/callers run `FsOpFormat` first.

**File read/write (Inc 5b, in `OsRoutines.Fs.cs`):** `EmitFsRwSubroutines` adds `oft_from_fd` (fd 2..7 → OFT entry addr or -1), `fs_grow_chain` (walk/extend a block chain to ≥N blocks, allocating + linking as needed), `fs_read_core`, `fs_write_core` — reachable via `IvtFsOp` (FsOpRead=14/FsOpWrite=15, absolute buffer) or `FSYS` (FsysRead=1/FsysWrite=2, user pointer translated). Both walk the chain word-per-char through `cache_get`; READ clamps count at `size-offset` and advances OFT offset; WRITE grows the chain, marks blocks `cache_dirty`, advances offset, grows OFT size, and writes the new size back into the on-disk dir entry. All loop state lives in `OsLayout.FsRw*` (cache/chain calls clobber registers). **Trap:** `fs_grow_chain` reuses `FsRwRemaining` as its block counter, so `fs_write_core` restores `FsRwRemaining` from `FsRwCopied` after calling it. **Paging note:** cores write file→buffer via *absolute* addresses; a user buffer must sit in a RAM-home page (program image page) so the user's later MMU-translated read sees the same bytes — a buffer straddling into demand-paged heap will not.

**Exec-by-path + boot wiring (Inc 6, in `OsRoutines.Fs.cs`):** `FSYS` syscall 4 (`FsysExec`, EBX=path ptr) → `fsy_exec` → `fs_exec_core` (label, `EmitFsExecSubroutine`). Mirrors `EmitExec`'s teardown/realloc/resume but sources the new image from an FS file's block chain instead of a disk image slot: it resolves the path to a `type=file` entry (rejecting missing paths and directories → returns -1, delivered to the caller), captures `firstBlock`+`size` **before** teardown, then `free_sub`/`resolve_cow`/`release_frames`/`zero_swap_slots` → recompute sizing (`newLen = size*4`) → `alloc_sub` → copy the chain word-by-word into `ProgramAddress` through `cache_get` → reset regs (EIP=0), set ESP, and `OSRET` into the new image (never returns on success). The rebuilt process is FS-backed so its `DiskSlot` is set to -1 (code pages are RAM-home, filled from `ProgramAddress`, so no slot is needed). **Two ISA gotchas learned here:** (1) `LoadField`/`StoreFieldReg` clobber **EAX** (they build the offset in it), so an entry address reused across several `LoadField`s must live in another register (fs_exec_core holds it in R12); (2) the durable `firstBlock`/`size`/`newLen` are stashed in `FsScratch*` (not `FsRw*`) across the teardown and re-seeded into `FsRw*` after `alloc_sub`.

**Boot auto-format:** `OperatingSystem.AttachHardware` now calls a virtual `OnBooted(hw)` hook after seeding; `BasicOS` overrides it to run `FsOpFormat` once — guarded by the on-disk superblock magic (read from the disk, empty on a fresh machine) so a persisted/loaded FS is not wiped. Tests no longer need to format by hand (existing ones that still do are harmless double-formats).

**FS maintenance (Phase 1 rectification, in `OsRoutines.Fs.cs`, `EmitFsMaintSubroutines`):** `fs_unlink` (EAX=abs path → 0/-1: resolve parent+entry, refuse a directory or an **open** file, then free the file's whole block chain — the old `fs_dir_remove` only cleared the entry and **leaked every block** — and drop the dir entry), `fs_mkdir_path` (EAX=abs path → new dir block; `fs_resolve_parent`+`fs_mkdir`), `fs_readdir` (EBX=dir block, ECX=index, EDX=out → copies the n-th in-use 64-byte dir entry to the out buffer, returns its type or -1 past end; skips `type=free` slots), `fs_resolve_dir` (path→dir block, with `/`=root special-case), and `oft_find_first` (OFT scan, shared by unlink's open-check and `fs_open_core`'s single-open reject). Reachable via `IvtFsOp` 16–18 or `FSYS` 5–7. **Also fixed:** the superblock `FreeCount` is now maintained (`fs_alloc_block` decrements, `fs_free_block` increments) instead of write-once at format; `fs_open_core` rejects a **second open** of the same file (single-open policy — two OFT handles would desync their cached sizes); `fs_format` **pins** the superblock+bitmap in the cache (never eviction victims), and `cache_flush` was corrected to write back **pinned** dirty slots too (pinning blocks eviction, not write-back — else the pinned superblock never persisted).

**Storing a program as a file:** `FsImage.WriteFile(hw, path, bytes)` (host helper, `CSharpOS/Disk/FsImage.cs`) drives the `IvtFsOp` Open/Write/Close cores with a scratch process index (Close cleans up its fd/OFT), staging the path+bytes in the free heap above the OS region — intended for **boot-time** population before any process is allocated. Because content is copied word-per-4-bytes, the file's raw bytes equal the supplied image (padded to a multiple of 4), which is exactly what `fs_exec_core` reads back.

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

**Memory protection (Phase 2):** the **MMU is the sole mechanism**. User data accesses (LOAD/STORE/CALL/RET stack) translate through the per-process page table; a page outside the process's mapped extent (`UnmappedPage`, or a page ≥ MaxPagesPerProcess) is a **protection fault** → `RaiseProtectionFault` terminates the process (exit -1) via the IvtInvalidInstruction teardown. There is no linear fallback and no LOAD/STORE bounds trap (both removed). Kernel mode addresses memory absolutely (unrestricted), so syscall/OS code is not translated. `LoadProcess` rejects a process whose user extent (program+memory+stack) exceeds `MaxPagesPerProcess*PageSize` (32 KiB).

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
| 2026-07-01 | Filesystem Inc 4b: nested dirs (fs_mkdir) + path traversal (fs_path_resolve/fs_extract_component) in OsRoutines.Fs.cs; FsPath* scratch. DataBase 12288→16384 (FS code hit the guard at 12.4KB) | ~30K (inline) | 9 new path tests (573 total), all green after the DataBase bump. First increment worked in the split file. OsLayout addrs converted to DataBase-relative in docs to stop absolute-address drift. |
| 2026-07-01 | Filesystem Inc 5a: FSYS (0x4D) + IvtFsSyscall (slot 19; IVT→20, CodeBase→80) + open-file table + fs_open_core/close_core (create-on-open, dir rejection, fd/OFT bookkeeping). Cores testable via FsOpOpen/FsOpClose | ~45K (inline) | Full ISA chosen. 13 core tests + 1 end-to-end FSYS test through a live scheduler (587 total), all green. Deliver-result idiom = EmitWait reap path (SAVEREGS persists captured trap frame). READ/WRITE = 5b. |
| 2026-07-02 | Filesystem Inc 5b: finish byte-level file read/write (EmitFsRwSubroutines: oft_from_fd/fs_grow_chain/fs_read_core/fs_write_core; FsOpRead=14/Write=15, FsysRead=1/Write=2). FsRw* scratch region; TotalSize→+9400. Fixed fs_grow_chain clobbering FsRwRemaining | ~20K (inline) | Resumed from a work-in-progress tree (6 isolation tests were red). Root-caused via a cache-Get block probe: grow reused FsRwRemaining as its counter → write copied only 1 char. Added end-to-end FSYS write/read round-trip (599 total). Learned: user FS buffers must be RAM-home (kernel writes absolute, user reads via MMU). |
| 2026-07-02 | Filesystem Inc 6: exec-by-path (FSYS 4 → fs_exec_core, EmitFsExecSubroutine) + boot auto-format (OnBooted hook in BasicOS, magic-guarded) + FsImage.WriteFile host helper + FS wired into program launch. DataBase 16384→20480 | ~55K (inline) | 4 end-to-end exec tests (603 total). Long debug hunt on a "block 900" crash: root cause was LoadField clobbering EAX while fs_exec_core held the entry addr there (memory[8]=IVT[2]≈900). Fixed by holding the entry addr in R12. Learned LoadField/StoreFieldReg clobber EAX+EBP. |
| 2026-07-02 | Test-suite navigation metadata: new `OSTests/CLAUDE.md` (subsystem→file jump table for 50 files, shared `Test` helper reference, test conventions) to cut the cost of finding a test case; root CLAUDE.md folder map updated | ~12K (inline) | Extracted class summaries + sample test names via scripted grep instead of reading 50 files. Then delegated README/docs refresh to the docs-sync-writer agent. |
| 2026-07-02 | Docs refresh (README.md + docs/ISA.md + docs/OS-Architecture.md) for the whole FS subsystem (Inc 1–6), string/key I/O, two-level privilege, paging | 214K (agent) | docs-sync-writer agent; verified numbers against OsLayout.cs. Caught that FSYS dispatches atomically (like FORK), not via EnterKernel. Flagged: no FS demo in the console yet; Mkdir/Readdir/Unlink have ISA routines but no FSYS number. |
| 2026-07-03 | Rectification Phase 0 (hygiene): removed dead `IvtDiskLoad` (slot 9) + renumbered slots 9–18 (IvtSlotCount 20→19, CodeBase 80→76); deleted empty `GeneralFunctionality.cs`; untracked `claude-memory/`; fixed OS/CLAUDE.md LoadProcess flow (IvtSpawn not Allocate→DiskLoad); documented SETFOCUS as C#-only | ~15K (inline) | Plan `keen-churning-manatee.md` phase 0/6. All refs by named constant so renumber was mechanical; 1 test (OsSeedDataTests) referenced IvtDiskLoad→swapped to IvtSpawn. 602 tests (was 603, −1 deleted placeholder). |
| 2026-07-03 | Rectification Phase 2 (MMU = sole protection): MaxPagesPerProcess 64→128 (page-table region 2048→4096, SwapSlotCount→1024, default Bin→1088, TotalSize→+11448); removed both linear fallbacks in TryTranslateData → `RaiseProtectionFault` (reuses IvtInvalidInstruction kill path); LoadProcess size guard; **deleted** Load/StoreBoundsTrapProvider + IsAddressInProcessRanges/GetCurrentProcessRanges/MemoryRange; migrated ~23 bounds/range tests | ~50K (inline) | Plan phase 2/6. `userExtent` includes the user stack, so legit code/data/stack pages never hit the fallback — only genuine OOB does. 603 tests (5 new MemoryProtectionTests). **Deferred:** ISA exec size guard (oversized exec already fails safe via the protection fault; C# LoadProcess guard covers the boot path). |
| 2026-07-03 | Rectification Phase 1 (FS correctness): `fs_unlink` (fixes chain leak) + `fs_mkdir_path` + `fs_readdir` + `fs_resolve_dir` + `oft_find_first` (EmitFsMaintSubroutines); FsOp 16–18, FSYS 5–7; maintain superblock FreeCount; single-open reject; pin superblock+bitmap; fixed `cache_flush` to write back pinned dirty slots | ~40K (inline) | Plan phase 1/6. 16 isolation (FsMaintTests) + 3 end-to-end (FsSyscallTests) = 621 tests. Pin exposed the flush-skips-pinned bug (pinned superblock never persisted). |

**Red flag:** any single planning/implementation task exceeding ~50K tokens — investigate what was being re-scanned and add it to CLAUDE.md or markers.

---

## Paging PTE Encoding

| PTE value | Meaning |
|-----------|---------|
| ≥ 0 | Resident; value = frame's physical base in FramePool |
| -1 (UnmappedPage) | Outside the process's mapped extent; a user access → **protection fault** (kills the process). No linear fallback |
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
