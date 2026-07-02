# BasicOSPlugin Quick Reference

## Files

`OsRoutines` is one `public static partial class` split across four files by subsystem. **To locate any Emit method or ISA routine, Grep its name** (each has a `// ===== Name ===` marker) — do NOT rely on line numbers (they drift on every edit). The table says which file to open.

| File | Holds (Grep the `// ===== ` marker to jump) |
|------|------|
| BasicOS.cs | Concrete OS; `(TextWriter)` ctor for plugin loader; CollectTraps via reflection |
| OsRoutines.cs | **Core**: `BuildOsImage()`, register/enum consts, scheduling (ContextSwitch, Schedule, Block, Wake, ResumeMlfq), lifecycle (Halt, InvalidInstruction, ExitBody, Fork, Exec, Wait, Spawn, DiskLoad), Syscall, buddy allocator (BuddyAlloc, AllocSub, BuddyFree, bit helpers), and all shared emit helpers (R, Imm16, LoadField, StoreField*, SetupPrivilegedStack, EmitCacheSlotBase, EmitStampSlot, SpillStore/Load) |
| OsRoutines.Paging.cs | PageFault + frame/swap/COW subs (ReleaseFrames, FlushFrames, ZeroSwapSlots, SwapCopy, PairResolve, ResolveCow, CowShare, PteAddress, PageCopy) |
| OsRoutines.Cache.cs | EmitCacheOp + EmitCacheSubroutines (cache_find/get/dirty/write_through/pin/unpin/discard/flush) |
| OsRoutines.Fs.cs | EmitFsOp + EmitFsSubroutines (fs_format/alloc_block/free_block/chain_*) + EmitFsDirSubroutines (fs_hash/root_dir/dir_lookup/dir_insert/dir_remove) |
| Traps/IretTrapProvider.cs | Blocks IRET in user mode |
| Traps/LoadBoundsTrapProvider.cs | Bounds-checks LOAD in user mode |
| Traps/StoreBoundsTrapProvider.cs | Bounds-checks STORE in user mode |

Cross-file calls resolve two ways: C# calls (e.g. BuildOsImage → EmitFsOp) work because it's one partial class; ISA `asm.Call("cache_get")` are runtime label strings the assembler resolves at Build, independent of which file emitted the label.

---

## BuildOsImage Structure

`OsRoutines.BuildOsImage()` emits routines in this order, recording each start address:

```
[IVT: 19 slots × 4 bytes = 76 bytes]
[CodeBase = 76]
EmitContextSwitch    → IvtContextSwitch (slot 0)        OsRoutines.cs:124
EmitSchedule         → IvtSchedule (slot 7)             OsRoutines.cs:208
EmitBlock            → IvtBlockInput + IvtBlockOutput (slots 5 & 6, same address) :216
EmitWakeEntry(Input) → IvtWakeInput (slot 3)            OsRoutines.cs:1896
EmitWakeEntry(Output)→ IvtWakeOutput (slot 4)           OsRoutines.cs:1896
EmitWakeEntry(KeyInput)→ IvtWakeKey (slot 16)           OsRoutines.cs:1896
EmitWakeBody         → (shared tail, no IVT entry)      OsRoutines.cs:1903
EmitHalt             → IvtHalt (slot 1)                 OsRoutines.cs:229
EmitInvalidInstruction→ IvtInvalidInstruction (slot 2)  OsRoutines.cs:244
EmitBuddyAlloc       → IvtAllocate (slot 8)             OsRoutines.cs:1183
EmitDiskLoad         → IvtDiskLoad (slot 9)             OsRoutines.cs:1352
EmitSpawn            → IvtSpawn (slot 13)               OsRoutines.cs:1368
EmitFork             → IvtFork (slot 10)                OsRoutines.cs:1424
EmitExec             → IvtExec (slot 11)                OsRoutines.cs:1608
EmitWait             → IvtWait (slot 12)                OsRoutines.cs:1091
EmitSyscall          → IvtSyscall (slot 14)             OsRoutines.cs:1143
EmitPageFault        → IvtPageFault (slot 15)           OsRoutines.cs:254
EmitCacheOp          → IvtCacheOp (slot 17)             (dispatch → cache_* subs)
EmitFsOp             → IvtFsOp (slot 18)                (dispatch → fs_* subs)
EmitExitBody         → label "exit_body"                OsRoutines.cs:960
EmitAllocSub         → label "alloc_sub" (CALL/RET)     OsRoutines.cs:1208
EmitBuddyFree        → label "buddy_free_entry"         OsRoutines.cs:1721
EmitReleaseFrames    → label "release_frames" (CALL/RET) OsRoutines.cs:501
EmitFlushFrames      → label "flush_frames" (CALL/RET)  OsRoutines.cs:533
EmitZeroSwapSlots    → label "zero_swap_slots" (CALL/RET) OsRoutines.cs:585
EmitPairResolve      → label "pair_resolve" (CALL/RET)  OsRoutines.cs:627
EmitResolveCow       → label "resolve_cow" (CALL/RET)   OsRoutines.cs:743
EmitCowShare         → label "cow_share" (CALL/RET)     OsRoutines.cs:826
EmitCacheSubroutines → labels "cache_find/get/dirty/write_through/pin/unpin/discard/flush" (CALL/RET)
EmitFsSubroutines    → labels "fs_format/alloc_block/free_block/chain_next/chain_set_next" (CALL/RET)
EmitFsDirSubroutines → labels "fs_hash/root_dir/dir_lookup/dir_insert/dir_remove" (CALL/RET)
EmitResumeMlfq       → label "resume_mlfq" (tail)       OsRoutines.cs:1941
```

*(Line numbers above predate the Inc 2 cache additions and have drifted; treat them as approximate.)*

After assembly: writes IVT entries, pre-fills CowPartner table with -1 for all 8 slots.
Guards: `OsLayout.CodeBase + code.Length > OsLayout.DataBase` → throws.

---

## Named ISA Subroutines (CALL/RET)

All subroutines require the **privileged scratch stack** (`SetupPrivilegedStack` sets ESP=PrivilegedStackTop) before the first CALL in a routine.

> The `:NNN` line refs below are **stale** (they predate the cache/FS work and the file split). Ignore them — Grep the Emit method name and open the file per the Files table. Kept only as a rough grouping hint.

| Label | Emit method (OsRoutines.cs line) | Called from | Purpose |
|-------|----------------------------------|-------------|---------|
| `alloc_sub` | EmitAllocSub :1208 | IvtSpawn, IvtFork, IvtExec, exit_body (via free_sub) | Buddy-alloc; EBX=entry; sets ProgramAddress |
| `free_sub` | EmitBuddyFree :1721 | exit_body, IvtExec | Buddy-free; EBX=entry |
| `release_frames` | EmitReleaseFrames :501 | exit_body, IvtExec | Free all frames owned by current process (no write-back) |
| `flush_frames` | EmitFlushFrames :533 | IvtFork | Write dirty frames to their backing (RAM home or swap) |
| `zero_swap_slots` | EmitZeroSwapSlots :585 | exit_body, IvtExec | DWRITE zero-page into all of current process's swap slots |
| `pair_resolve` | EmitPairResolve :627 | EmitPageFault (COW write), resolve_cow | Resolve one COW page (page in R12): give both sharers private copies |
| `resolve_cow` | EmitResolveCow :743 | IvtFork, exit_body, IvtExec | Loop all pages; call pair_resolve for each COW page; clear partnership |
| `cow_share` | EmitCowShare :826 | IvtFork | Convert current process's DATA pages to COW (mark resident frames, re-encode PTEs) |
| `cache_find` | EmitCacheSubroutines | cache_get/dirty/wt/pin/unpin/discard | Scan cache slots for a resident block; EAX=block → EAX=slot base or -1 |
| `cache_get` | EmitCacheSubroutines | IvtCacheOp, future FS routines | Ensure block resident (hit→stamp; miss→evict LRU, write back if dirty, FBREAD); EAX=block → EAX=data addr or -1 |
| `cache_dirty`/`cache_write_through` | EmitCacheSubroutines | IvtCacheOp | Mark resident block dirty (lazy) / FBWRITE now + clean |
| `cache_pin`/`cache_unpin` | EmitCacheSubroutines | IvtCacheOp | Bump / floor-decrement a slot's pin count (pinned = never evicted) |
| `cache_discard` | EmitCacheSubroutines | IvtCacheOp | Drop a block with no write-back (clear valid+dirty+pin) |
| `cache_flush` | EmitCacheSubroutines | IvtCacheOp, ContextSwitch periodic hook | FBWRITE every dirty unpinned valid slot, clear dirty |
| `fs_format` | EmitFsSubroutines | IvtFsOp | Write superblock (magic/geom) + empty bitmap (bits 0,1 set) through the cache |
| `fs_alloc_block` | EmitFsSubroutines | IvtFsOp, future dir/file routines | Scan bitmap for a clear bit → set it, init next=-1; EAX→block or -1 |
| `fs_free_block` | EmitFsSubroutines | IvtFsOp | Clear the bitmap bit + cache_discard the block; EAX=block |
| `fs_chain_next`/`fs_chain_set_next` | EmitFsSubroutines | IvtFsOp | Read / write a block's next-block link (offset 252) |
| `fs_hash` | EmitFsDirSubroutines | dir_lookup/insert, IvtFsOp | Hash a word-per-char name; EAX=nameAddr → EAX=hash (h=h*31+c) |
| `fs_root_dir` | EmitFsDirSubroutines | IvtFsOp | Read root dir block from superblock → EAX |
| `fs_dir_lookup` | EmitFsDirSubroutines | dir_insert/remove, IvtFsOp | EAX=dir,ECX=name → entry addr or -1; hash-reject + name-verify; stashes block in FsScratchEntryBlock |
| `fs_dir_insert` | EmitFsDirSubroutines | IvtFsOp | EBX=dir,ECX=name,EDX=type,ESI=first → entry addr or -1 (dup/full); finds free slot or extends chain |
| `fs_dir_remove` | EmitFsDirSubroutines | IvtFsOp | EAX=dir,ECX=name → 0/-1; marks entry type=free |
| `resume_mlfq` | EmitResumeMlfq :1941 | Every scheduling tail | Outer loop P=0..3; inner round-robin from ECX+1; first Ready process at priority P wins |

---

## Register Conventions in Routines

| Register | Role |
|----------|------|
| EAX | scratch / argument |
| EBX | current process-table entry address |
| ECX | current process-table index |
| EDX | wait reason / scan counter |
| ESI | scan index / loop counter |
| EDI | process count |
| R8–R15 | MLFQ state, frame ops, paging scratch (see comments in EmitPageFault/EmitContextSwitch) |

**Persistent across EmitPageFault:**
- R12 = faulting page
- R13 = current process index
- R14 = swap slot of faulting page (-1 for RAM-home)
- R15 = chosen frame index

---

## Key Emit Helper Methods (private, used by OsRoutines)

All defined in OsRoutines.cs; the helpers group starts at :2007.

| Method | Line | Purpose |
|--------|------|---------|
| `EntryAddress(asm, indexReg, destReg)` | :2007 | destReg = ProcessTableOffset + indexReg * EntrySize |
| `LoadField(asm, entryReg, fieldOffset, destReg)` | :2007 | destReg = *(entryReg + fieldOffset) |
| `StoreFieldReg(asm, entryReg, fieldOffset, srcReg)` | :2007 | *(entryReg + fieldOffset) = srcReg |
| `StoreFieldImm(asm, entryReg, fieldOffset, imm)` | :2007 | *(entryReg + fieldOffset) = immediate |
| `StoreFieldMinusOne(asm, entryReg, fieldOffset)` | :2007 | *(entryReg + fieldOffset) = -1 |
| `Imm16(asm, reg, value)` | :2007 | MovImm16 for 16-bit constants (OsLayout offsets > 255) |
| `SetupPrivilegedStack(asm)` | :2007 | ESP = PrivilegedStackTop (required before first CALL) |
| `FrameEntryAddress(asm, frameReg, dest)` | :897 | dest = FrameTableBase + frame * FrameTableEntryBytes |
| `FrameBaseAddress(asm, frameReg, dest, pageShift)` | :909 | dest = FramePoolBase + frame << pageShift |
| `EmitPteAddress(asm, procReg, pageReg, dest, scratch, strideShift, pteShift)` | :921 | dest = &PTE[proc][page] |
| `EmitPageCopy(asm, srcReg, dstReg, prefix, valReg)` | :937 | Word loop copying PageSize bytes; needs unique prefix string |
| `EmitSwapCopy(asm, srcSlotReg, dstSlotReg, lenScratch, addrScratch)` | :615 | DREAD src→scratch, DWRITE scratch→dst |

`R(byte reg)` converts a byte register-index constant (e.g. `EAX = (byte)RegisterName.EAX`) to `RegisterName` for Assembler emit methods.

---

## Fork COW Sequence (EmitFork)

1. `resolve_cow` — materialise any existing COW share (invariant: process resolves before forking again)
2. `flush_frames` — write-back dirty frames so the child's block-home RAM has live data
3. Flat memcpy of parent's entire memory block to child's new allocation
4. Copy register file; seed child EAX=0, parent EAX=childPid; assign child Pid/ParentPid
5. `cow_share` — convert parent's DATA pages to COW (mark resident data frames, re-encode swap PTEs)
6. Set `cowPartner[parent] = child`, `cowPartner[child] = parent`
7. → resume_mlfq

**After fork:** `SeedPageTableIfNew` (called on child's first resume via SETLAYOUT) seeds child data pages as `CowPte(partnerSlot)`.

---

## Exit / Teardown Sequence (exit_body)

1. Mark entry Terminated (hide from scans)
2. `SetupPrivilegedStack` + CALL `free_sub`
3. CALL `resolve_cow` (partner keeps private copy before frames are freed)
4. CALL `release_frames` (discard resident frames — no write-back)
5. CALL `zero_swap_slots` (prevent stale data in reused slots)
6. Reload EBX (resolve_cow clobbered it)
7. Scan for a parent blocked in wait() on this PID → deliver status + wake + reap; OR keep as Zombie; OR reap as orphan
8. Reap own zombie children
9. → resume_mlfq

---

## BasicOS

```csharp
public class BasicOS : OperatingSystem
{
    public override int OsMemorySize => OsLayout.TotalSize;          // 17584
    public override byte[] BuildOsImage(int osMemoryBase) => OsRoutines.BuildOsImage();
    public BasicOS(TextWriter log) : base(CollectTraps(), log) { }
    private static List<Trap> CollectTraps() { /* reflection */ }
}
```

`CollectTraps()` uses `Assembly.GetExecutingAssembly().GetTypes()` — discovers all non-abstract `ITrapProvider` in this DLL. Newly added `XxxTrapProvider` classes are included automatically.
