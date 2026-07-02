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
| `AttachHardware(Hardware hw)` | Called by Hardware ctor; calls `BuildOsImage`, writes it, `ReserveOsMemory`, `SeedOsData`, `LoadTraps(BuildTraps())` |
| `SeedOsData()` | Seeds quantum table (L0=1, L1=2, L2=4, L3=255), boost timer, buddy heap config, NextPid=1 |
| `LoadProcess(Process process)` | Stages entry, dispatches IvtAllocate, then IvtDiskLoad (if slot ≥ 0), seeds fds, assigns PID |
| `SetLayoutFromEntry(int entryAddress)` | Rebuilds HW layout from entry; called before resume (via SETLAYOUT) |
| `SeedPageTableIfNew(...)` | Called from SetLayoutFromEntry; seeds per-process page table once per slot reuse |

### LoadProcess flow
1. Resolve/auto-stage the program slot in `hw.Disk` if needed
2. Read program length from `hw.Disk.GetLength(slot)`
3. Write sizing fields to the process-table entry (ProgramSize, RequiredMemory, etc.)
4. `RunOsRoutineSynchronously(IvtAllocate, entryAddress)` — sets ProgramAddress in the entry
5. If ProgramAddress ≥ 0 and slot ≥ 0: `RunOsRoutineSynchronously(IvtDiskLoad, entryAddress)`
6. Seed fd table (fd[0]=fd[1]=process-table index)
7. Assign PID from NextPid, increment NextPid
8. Set entry state to Ready, priority=0, ticksUsed=0

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
| LoadBoundsTrapProvider | LOAD | address outside process ranges | bounds violation |
| StoreBoundsTrapProvider | STORE | address outside process ranges | bounds violation |

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
