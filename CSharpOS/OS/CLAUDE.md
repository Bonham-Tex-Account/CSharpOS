# CSharpOS/OS Quick Reference

## Files

| File | Role |
|------|------|
| OsLayout.cs | All OS region offsets and paging constants (see root CLAUDE.md tables) |
| OperatingSystem.cs | Abstract base: boot, load, seed, layout |
| IOperatingSystem.cs | Interface consumed by Hardware |
| ITrapProvider.cs | Interface for trap handlers; auto-discovered by BasicOS via reflection |
| OsPluginLoader.cs | `Load(string dllPath, TextWriter log)` — reflects over a .dll to find an OS |

---

## OperatingSystem (abstract)

### Abstract members to implement
| Member | Notes |
|--------|-------|
| `OsMemorySize` | Size of the OS region to reserve |
| `BuildOsImage(int osMemoryBase) → byte[]` | Returns full OS image (IVT + code + data zeroed) |

### Key methods (called by infrastructure)
| Method | Notes |
|--------|-------|
| `AttachHardware(Hardware hw)` | Called by Hardware ctor; `LoadTraps(traps)`, `ReserveOsMemory`, `WriteBytes(BuildOsImage)`, `SeedOsData`, then `OnBooted(hw)` hook |
| `OnBooted(Hardware hw)` | Virtual post-boot hook; base no-op. `BasicOS` overrides it to auto-format the FS once (magic-guarded) |
| `SeedOsData()` | Seeds quantum table (L0=1, L1=2, L2=4, L3=255), boost timer, buddy heap config, NextPid=1, cache/swap/COW init |
| `LoadProcess(Process process)` | Stages entry sizing, dispatches **IvtSpawn** (one routine: allocs region + DREADs image + seeds regs/PID), seeds fds, syncs descriptor |
| `SetLayoutFromEntry(int entryAddress)` | Rebuilds HW layout from entry; called before resume (via SETLAYOUT) |
| `SeedPageTableIfNew(...)` | Called from SetLayoutFromEntry; seeds per-process page table once per slot reuse |

### LoadProcess flow
1. Resolve/auto-stage the program slot in `hw.Disk` (a file-path `Process` reads bytes → `Disk.Store`); disk-full → log + bail
2. `programLength = hw.Disk.GetLength(slot)`; floor `RequiredMemory` to `GetRegisterFileSize()+4`
3. `total = programLength + RequiredMemory + RequiredStackSize + KernelStackSize`
4. `FindFreeSlot`; process-table full → log + bail
5. Write the entry's sizing fields (ProgramSize/RequiredMemory/RequiredStackSize/TotalSize/DiskSlot) + State=Terminated placeholder
6. `RunOsRoutineSynchronously(IvtSpawn, entry)` — allocs the region, DREADs the image, seeds saved regs (EIP/ESP), scheduling state, and a fresh PID; sets ProgramAddress=-1 on OOM
7. ProgramAddress < 0 → log + bail; else read back the assigned PID
8. Seed fd table (fd[0]=stdin, fd[1]=stdout → the process's own device id = slot); grow ProcessCount high-water mark; sync the C# `Process` descriptor + name map

---

## IOperatingSystem

```csharp
public interface IOperatingSystem
{
    void AttachHardware(Hardware hw);
    // (all other OS behaviour is via IVT dispatch, not direct method calls)
}
```

Hardware never calls OS methods other than `AttachHardware` at boot. All OS behaviour after that runs as ISA code entered via the IVT.

---

## ITrapProvider

```csharp
public interface ITrapProvider
{
    Trap GetTrap();
}
```

`Trap` struct:
```csharp
public struct Trap
{
    public byte Opcode;
    public Func<Hardware, byte, byte, byte, bool>? Condition;
    public string? Reason;
}
```

Implement in `BasicOSPlugin/Traps/` as `public sealed class XxxTrapProvider : ITrapProvider`. `BasicOS.CollectTraps()` auto-discovers via `Assembly.GetExecutingAssembly().GetTypes()` — no manual registration.

**Existing traps:**
| Provider | Opcode | Condition | Reason |
|----------|--------|-----------|--------|
| IretTrapProvider | IRET | user mode | "IRET not allowed in user mode" |

---

## OsPluginLoader

```csharp
OperatingSystem os = OsPluginLoader.Load(string dllPath, TextWriter log);
```

- Reflects over the DLL; finds the first non-abstract `OperatingSystem` subclass with a `(TextWriter)` constructor
- Uses `GetConstructor(new[] { typeof(TextWriter) })` explicitly (not `Activator.CreateInstance` blindly)
- Throws `InvalidOperationException` if no matching class found
- Logs candidate types and the result to `log`

**Gotcha:** OSTests.dll contains `TrappingOS : OperatingSystem` with a `(List<Trap>, TextWriter)` constructor — the explicit constructor check prevents accidentally loading it.

---

## Bin (CSharpOS/Disk/Bin.cs)

Flat fixed-slot block store. Slots numbered 0..SlotCount-1. Short blobs recorded at true length; slack tail is zeroed.

```csharp
Bin(int slotCount, int slotSize)                                    // slot-only (no file region)
Bin(int slotCount, int slotSize, int fileBlockCount, int fileBlockSize) // + file-block region

// ---- slot region (variable-length, occupied/length directory) ----
int  Store(byte[] data)          // first free slot → index; -1 if full; throws if data > slotSize
void Store(int slot, byte[] data) // overwrite specific slot; throws if data > slotSize
byte[] Load(int slot)            // defensive copy at true length; throws if free
int  GetLength(int slot)         // true content length; throws if free
void Free(int slot)              // zero + mark free; idempotent
bool IsOccupied(int slot)
int  FreeSlotCount               // property
int  SlotCount / SlotSize        // properties

// ---- file-block region (fixed-size, raw, block-addressed) ----
byte[] ReadFileBlock(int block)         // fresh copy; zeros if never written; never throws for "empty"
void   WriteFileBlock(int block, byte[]) // data must be exactly FileBlockSize; else ArgumentException
int    FileBlockCount / FileBlockSize   // properties (0 for a slot-only Bin)
void   Save(string path) / Load(string path) // persist ONLY the file-block region (CSFS header)
```

Default disk: `Bin(DefaultDiskSlots + SwapSlotCount, DefaultDiskSlotSize, DefaultFileBlockCount, DefaultFileBlockSize)` = `Bin(576, 1024, 256, 256)`.
Image slots: 0–63. Swap slots: 64–575 (`SwapSlot(proc, page) = 64 + proc*64 + page`). File blocks: 0–255 (independent address space, moved by `FBREAD`/`FBWRITE`).

---

## OsLayout Key Helpers

```csharp
OsLayout.ProcessEntryAddress(int index)        // absolute addr of process-table entry
OsLayout.PageTableAddress(int index)           // absolute addr of process's page table
OsLayout.FrameTableEntry(int frame)            // absolute addr of frame's core-map entry
OsLayout.FrameBase(int frame)                  // absolute physical base of frame in pool
OsLayout.SwapSlot(int processIndex, int page)  // deterministic Bin-disk swap slot
OsLayout.CowPartnerAddress(int processIndex)   // absolute addr of process's COW-partner word
OsLayout.IsDataPage(int page, int programSize, int requiredMemory) → bool
OsLayout.SwapPte(int slot) / SwapSlotFromPte(int pte) / IsSwapPte(int pte)
OsLayout.CowPte(int slot) / CowSlotFromPte(int pte) / IsCowPte(int pte)
```
