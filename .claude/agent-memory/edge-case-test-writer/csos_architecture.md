---
name: csos-architecture
description: CSharpOS ISA emulator architecture: OsLayout data section, ProcessEntry field layout, IVT slots, MLFQ scheduler design, key offset values
metadata:
  type: project
---

## OsLayout (DataBase = 2048)
- ProcessCountOffset    = DataBase+0
- CurrentIndexOffset    = DataBase+4   (-1 = idle)
- FreeRangeCountOffset  = DataBase+8
- PendingCountOffset    = DataBase+12
- BoostTimerOffset      = DataBase+16  (MLFQ, new)
- QuantumTableOffset    = DataBase+20  (MLFQ, 4×4 bytes, new)
- ProcessTableOffset    = DataBase+36  (SHIFTED from old DataBase+16 by +20)
- FreeRangeTableOffset  = ProcessTableOffset + MaxProcesses(8) × ProcessEntrySize(160) = 3364
- PendingQueueOffset    = FreeRangeTableOffset + MaxFreeRanges(16) × 8 = 3364+128 = 3492

## ProcessEntry field offsets (within entry)
- RegisterFile: 0  (96 bytes = 24 registers × 4)
- Level: 96
- State: 100  (0=Ready, 1=Blocked, 2=Terminated)
- WaitReason: 104
- ProgramAddress: 108
- ProgramSize: 112
- RequiredMemory: 116
- RequiredStackSize: 120
- TotalSize: 124
- Priority: 128  (MLFQ queue level 0=highest, NEW)
- TicksUsed: 132  (ticks used at current level, NEW)
- EntrySize: 160

## MLFQ Constants
- QueueCount = 4, BoostInterval = 20
- L0 threshold=1, L1=2, L2=4, L3=255 (never demote)
- New processes start at Priority=0, TicksUsed=0

## IVT slots (0-based, each 4 bytes at address slot*4)
- 0=ContextSwitch, 1=Halt, 2=InvalidInstruction, 3=WakeInput, 4=WakeOutput
- 5=BlockInput, 6=BlockOutput, 7=Schedule, 8=LoadProcess

## Register convention in OS routines
ECX=currentIndex, EDI=processCount, ESI=scan counter, EDX=wait reason
EBX=entry address, R8-R15=MLFQ scratch

## Isolation test pattern
```
Hardware hw = Test.NewHardware(8192, new FakeOS());
hw.ReserveOsMemory(OsLayout.TotalSize);
hw.WriteBytes(0, OsRoutines.BuildOsImage());
// SeedMlfqDefaults(hw) for quantum+timer; WriteWord for count/index
// SeedEntry for each process slot
hw.DispatchOsRoutine(Hardware.IvtContextSwitch); // or other slot
RunRoutine(hw); // step until !Privileged
```

## Key invariants
- Boost loop fires when BoostTimer decrements to 0; resets to BoostInterval
- Boost SKIPS when currentIndex < 0 (idle path jumps over entire boost section)
- Wake routines do NOT preempt: save+immediately restore the interrupted process
- Block routine does NOT touch Priority/TicksUsed (only ContextSwitch and Wake do)
