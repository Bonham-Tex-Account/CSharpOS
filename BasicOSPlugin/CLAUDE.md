# BasicOSPlugin Quick Reference

## Files

`OsRoutines` is one `public static partial class` split across four files by subsystem. **To locate any Emit method or ISA routine, Grep its name** (each has a `// ===== Name ===` marker) — do NOT rely on line numbers (they drift on every edit). The table says which file to open.

| File | Holds (Grep the `// ===== ` marker to jump) |
|------|------|
| BasicOS.cs | Concrete OS; `(TextWriter)` ctor for plugin loader; CollectTraps via reflection |
| OsRoutines.cs | **Core**: `BuildOsImage()`, register/enum consts, scheduling (ContextSwitch, Schedule, Block, Wake, ResumeMlfq), lifecycle (Halt, InvalidInstruction, ExitBody, Fork, Exec, Wait, Spawn), Syscall, buddy allocator (BuddyAlloc, AllocSub, BuddyFree, bit helpers), and all shared emit helpers (R, Imm16, LoadField, StoreField*, SetupPrivilegedStack, EmitCacheSlotBase, EmitStampSlot, SpillStore/Load) |
| OsRoutines.Paging.cs | PageFault + frame/swap/COW subs (ReleaseFrames, FlushFrames, ZeroSwapSlots, SwapCopy, PairResolve, ResolveCow, CowShare, PteAddress, PageCopy) + kernel user-mem access (PageIn, EnsureUserPage, EnsureUserPageOp, UserWordAddr — Phase 3) |
| OsRoutines.Cache.cs | EmitCacheOp + EmitCacheSubroutines (cache_find/get/dirty/write_through/pin/unpin/discard/flush) |
| OsRoutines.Fs.cs | EmitFsOp + EmitFsSubroutines (fs_format/alloc_block/free_block/chain_*) + EmitFsDirSubroutines (fs_hash/root_dir/dir_lookup/dir_insert/dir_remove) + EmitFsPathSubroutines (fs_extract_component/path_resolve/mkdir) + EmitFsSyscall + EmitFsFileSubroutines (oft_alloc/resolve_parent/create_file/open_core/close_core) + EmitFsRwSubroutines (oft_from_fd/fs_grow_chain/fs_read_core/fs_write_core) + EmitFsLoadImage (fs_load_image — Phase 4) + EmitFsExecSubroutine (fs_exec_core) + EmitFsMaintSubroutines (oft_find_first/fs_unlink/fs_mkdir_path/fs_readdir/fs_resolve_dir) |
| Traps/IretTrapProvider.cs | Blocks IRET in user mode |

Cross-file calls resolve two ways: C# calls (e.g. BuildOsImage → EmitFsOp) work because it's one partial class; ISA `asm.Call("cache_get")` are runtime label strings the assembler resolves at Build, independent of which file emitted the label.

---

## BuildOsImage Structure

`OsRoutines.BuildOsImage()` emits routines in this order, recording each start address:

```
[IVT: 20 slots × 4 bytes = 80 bytes]
[CodeBase = 80]
EmitContextSwitch    → IvtContextSwitch (slot 0)        OsRoutines.cs:124
EmitSchedule         → IvtSchedule (slot 7)             OsRoutines.cs:208
EmitBlock            → IvtBlockInput + IvtBlockOutput (slots 5 & 6, same address) :216
EmitWakeEntry(Input) → IvtWakeInput (slot 3)            OsRoutines.cs:1896
EmitWakeEntry(Output)→ IvtWakeOutput (slot 4)           OsRoutines.cs:1896
EmitWakeEntry(KeyInput)→ IvtWakeKey (slot 15)           OsRoutines.cs:1896
EmitWakeBody         → (shared tail, no IVT entry)      OsRoutines.cs:1903
EmitHalt             → IvtHalt (slot 1)                 OsRoutines.cs:229
EmitInvalidInstruction→ IvtInvalidInstruction (slot 2)  OsRoutines.cs:244
EmitBuddyAlloc       → IvtAllocate (slot 8)             OsRoutines.cs:1183
EmitSpawn            → IvtSpawn (slot 12)               OsRoutines.cs:1368
EmitFork             → IvtFork (slot 9)                 OsRoutines.cs:1424
EmitExec             → IvtExec (slot 10)                OsRoutines.cs:1608
EmitWait             → IvtWait (slot 11)                OsRoutines.cs:1091
EmitReap             → IvtReap (slot 20)                (Shell §2.5 JC-A; non-blocking reap)
EmitKill             → IvtKill (slot 21)                (Shell §2.5 JC-B/E; CALLs teardown_reap; kl_catch = signal delivery)
EmitSigReturn        → IvtSigReturn (slot 22)           (Shell §2.5 JC-E; restore SignalSave → resume)
EmitSignalSubroutines→ label "sig_copy" (CALL/RET; copy a SignalSaveStride-byte regfile+level slot)
EmitSyscall          → IvtSyscall (slot 13)             OsRoutines.cs:1143
EmitPageFault        → IvtPageFault (slot 14)           OsRoutines.cs:254
EmitPageIn           → label "page_in" (CALL/RET; extracted from EmitPageFault)   (Phase 3)
EmitEnsureUserPage   → label "ensure_user_page" (CALL/RET)                        (Phase 3)
EmitEnsureUserPageOp → IvtEnsureUserPage (slot 19)      (C#-only dispatch via Hardware.UserToPhysical) (Phase 3)
EmitUserWordAddr     → label "user_word_addr" (CALL/RET)                          (Phase 3)
EmitCacheOp          → IvtCacheOp (slot 16)             (dispatch → cache_* subs)
EmitFsOp             → IvtFsOp (slot 17)                (dispatch → fs_* subs)
EmitExitBody         → label "exit_body"                OsRoutines.cs:960
EmitTeardownReap     → label "teardown_reap" (CALL/RET; free+wake+zombie/orphan; exit_body & kill_core) (Shell §2.5 JC-B)
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
EmitFsPathSubroutines→ labels "fs_extract_component/path_resolve/mkdir" (CALL/RET)
EmitFsSyscall        → IvtFsSyscall (slot 18)           (FSYS wrapper; deliver via SAVEREGS/OSRET)
EmitFsFileSubroutines→ labels "oft_alloc/resolve_parent/create_file/open_core/close_core" (CALL/RET)
EmitFsRwSubroutines  → labels "oft_from_fd/fs_grow_chain/fs_read_core/fs_write_core" (CALL/RET)
EmitFsLoadImage      → label "fs_load_image" (CALL/RET; chain→RAM copy, shared by spawn+exec) (Phase 4)
EmitExecTokenizer    → label "exec_next_token" (space tokenizer over FsArgvCmd)  (Shell §2)
EmitExecBuildArgv    → label "exec_build_argv" (write argv[]+strings into the child)  (Shell §2)
EmitFsExecSubroutine → label "fs_exec_core" (CALL; resumes on success)  (Inc 6)
EmitFsMaintSubroutines→ labels "oft_find_first/fs_unlink/fs_mkdir_path/fs_readdir/fs_resolve_dir" (Phase 1)
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
| `page_in` | EmitPageIn | EmitPageFault, ensure_user_page | Fill logic extracted from EmitPageFault (CALL/RET): fault a non-resident page (R12) into a frame. **Clobbers R11** internally |
| `ensure_user_page` | EmitEnsureUserPage | ensure_user_page_op, user_word_addr | R12=page, R11=isWrite → EAX 0/-1. page_in if non-resident, then pair_resolve if resident+write+COW; -1 if UnmappedPage. Spills isWrite across page_in (R11 clobber). **On write (R11≠0) marks the resident frame DIRTY** (mirrors STORE's StampFrame) — else fork's flush_frames / eviction drop kernel-mediated writes (INS/OUTS/FSYS/readdir) since both only write back dirty frames |
| `ensure_user_page_op` | EmitEnsureUserPageOp | Hardware.UserToPhysical (RunOsRoutineSynchronously only) | IvtEnsureUserPage slot-19 wrapper: EAX=page, isWrite from OsLayout.EnsureUserPageIsWrite → result in EnsureUserPageResult |
| `user_word_addr` | EmitUserWordAddr | fsy_read/fsy_write | EAX=va, R11=isWrite → EAX=physical addr or -1. Translates one word via ensure_user_page; spills page/offset in OsLayout.PageXlate* |
| `cache_find` | EmitCacheSubroutines | cache_get/dirty/wt/pin/unpin/discard | Scan cache slots for a resident block; EAX=block → EAX=slot base or -1 |
| `cache_get` | EmitCacheSubroutines | IvtCacheOp, future FS routines | Ensure block resident (hit→stamp; miss→evict LRU, write back if dirty, FBREAD); EAX=block → EAX=data addr or -1 |
| `cache_dirty`/`cache_write_through` | EmitCacheSubroutines | IvtCacheOp | Mark resident block dirty (lazy) / FBWRITE now + clean |
| `cache_pin`/`cache_unpin` | EmitCacheSubroutines | IvtCacheOp | Bump / floor-decrement a slot's pin count (pinned = never evicted) |
| `cache_discard` | EmitCacheSubroutines | IvtCacheOp | Drop a block with no write-back (clear valid+dirty+pin) |
| `cache_flush` | EmitCacheSubroutines | IvtCacheOp, ContextSwitch periodic hook | FBWRITE every dirty valid slot (**pinned or not** — pinning blocks eviction, not write-back), clear dirty |
| `fs_format` | EmitFsSubroutines | IvtFsOp | Write superblock (magic/geom) + empty bitmap (bits 0,1 set) through the cache |
| `fs_alloc_block` | EmitFsSubroutines | IvtFsOp, future dir/file routines | Scan bitmap for a clear bit → set it, init next=-1; EAX→block or -1 |
| `fs_free_block` | EmitFsSubroutines | IvtFsOp | Clear the bitmap bit + cache_discard the block; EAX=block |
| `fs_chain_next`/`fs_chain_set_next` | EmitFsSubroutines | IvtFsOp | Read / write a block's next-block link (offset 252) |
| `fs_hash` | EmitFsDirSubroutines | dir_lookup/insert, IvtFsOp | Hash a word-per-char name; EAX=nameAddr → EAX=hash (h=h*31+c) |
| `fs_root_dir` | EmitFsDirSubroutines | IvtFsOp | Read root dir block from superblock → EAX |
| `fs_dir_lookup` | EmitFsDirSubroutines | dir_insert/remove, IvtFsOp | EAX=dir,ECX=name → entry addr or -1; hash-reject + name-verify; stashes block in FsScratchEntryBlock |
| `fs_dir_insert` | EmitFsDirSubroutines | IvtFsOp | EBX=dir,ECX=name,EDX=type,ESI=first → entry addr or -1 (dup/full); finds free slot or extends chain |
| `fs_dir_remove` | EmitFsDirSubroutines | IvtFsOp | EAX=dir,ECX=name → 0/-1; marks entry type=free |
| `fs_extract_component` | EmitFsPathSubroutines | fs_path_resolve | Pull next path component into FsPathComponentBase; advance FsPathPos; set FsPathLast; EAX=len (pure memory) |
| `fs_path_resolve` | EmitFsPathSubroutines | IvtFsOp | EAX=path → final entry addr or -1; descends type=dir; loop state in FsPath* memory |
| `fs_mkdir` | EmitFsPathSubroutines | IvtFsOp | EBX=parent,ECX=name → new dir block or -1; alloc block + insert type=dir entry (frees block on dup) |
| `oft_alloc` | EmitFsFileSubroutines | fs_open_core | → EAX = free open-file-table index or -1 (pure memory scan) |
| `fs_resolve_parent` | EmitFsFileSubroutines | fs_create_file | EAX=path → parent dir block or -1; leaves last component in FsPathComponentBase |
| `fs_create_file` | EmitFsFileSubroutines | fs_open_core | path in FsOpenAbsPath → new file entry addr or -1; alloc block + insert type=file |
| `fs_open_core` | EmitFsFileSubroutines | IvtFsOp Open, fs_syscall | EBX=absPath,ECX=flags,EDX=proc → fd or -1; resolve/create, fill OFT, alloc fd |
| `fs_close_core` | EmitFsFileSubroutines | IvtFsOp Close, fs_syscall | EBX=fd,ECX=proc → 0/-1; clear OFT + fd slot (pure memory) |
| `fs_load_image` | EmitFsLoadImage | IvtSpawn (FS-backed), fs_exec_core | EBX=firstBlock,ECX=words,EDX=dest → copy the file's block chain into RAM through the cache (Phase 4; extracted from fs_exec_core so spawn reuses it) |
| `oft_from_fd` | EmitFsRwSubroutines | fs_read_core/fs_write_core | EBX=fd(2..7),ECX=proc → EAX=OFT entry addr or -1 (pure memory) |
| `fs_grow_chain` | EmitFsRwSubroutines | fs_write_core | EBX=firstBlock,ECX=neededBlocks → 0/-1; walk+extend chain (alloc+link). **Reuses FsRwRemaining/CurBlock/Counter/BufPtr as scratch** — callers restore them after |
| `fs_read_core` | EmitFsRwSubroutines | IvtFsOp Read, fs_syscall | EBX=fd,ECX=absBuf,EDX=count,ESI=proc → chars read or -1; clamp at size-offset, advance offset |
| `fs_write_core` | EmitFsRwSubroutines | IvtFsOp Write, fs_syscall | EBX=fd,ECX=absBuf,EDX=count,ESI=proc → chars written or -1; grow chain, cache_dirty, advance offset, grow size + dir entry |
| `exec_next_token` | EmitExecTokenizer | fs_exec_core, exec_build_argv | Pull the next space-delimited token from FsArgvCmd into FsArgvTokenBuf; EAX=len (0=done); cursor in FsArgvCursor. Leaf (models fs_extract_component, delimiter '/'→' '); clobbers EAX/EBP/R8–R15 (Shell §2) |
| `exec_build_argv` | EmitExecBuildArgv | fs_exec_core | After fs_load_image: write argv[] pointer array + arg strings into the RAM-home reservation at [ProgramAddress+newLen], set FsArgvArgc=argc. CALLs exec_next_token (Shell §2) |
| `fs_exec_core` | EmitFsExecSubroutine | fs_syscall (FsysExec) | EBX=absPath (whole command line, Shell §2) → replace running image with the FS file. exec_next_token extracts token0 (path) to resolve; tears down (free/resolve_cow/release_frames/zero_swap); reallocs with ProgramSize'=newLen+ArgvReserveBytes; copies chain→ProgramAddress; exec_build_argv fills argv; seeds EAX=argc/EBX=argv; OSRETs. Returns -1 (missing/dir); **never returns on success**. Holds entry addr in R12 (LoadField clobbers EAX) |
| `oft_find_first` | EmitFsMaintSubroutines | fs_unlink, fs_open_core | EAX=firstBlock → EAX=1 if any in-use OFT handle references that file, else 0 (pure memory) |
| `fs_unlink` | EmitFsMaintSubroutines | IvtFsOp Unlink, fs_syscall | EAX=absPath → 0/-1; refuse a dir or an open file, free the whole block chain (reads next before freeing each block), then fs_dir_remove |
| `fs_mkdir_path` | EmitFsMaintSubroutines | IvtFsOp MkdirPath, fs_syscall | EAX=absPath → new dir block/-1; fs_resolve_parent + fs_mkdir |
| `fs_readdir` | EmitFsMaintSubroutines | IvtFsOp ReadDir, fs_syscall | EBX=dir block, ECX=index, EDX=out → copies n-th in-use 64-byte entry to out, returns type or -1; skips type=free |
| `fs_resolve_dir` | EmitFsMaintSubroutines | fs_syscall (Readdir) | EAX=absPath → dir block/-1; like fs_path_resolve but returns the dir's firstBlock and maps `/`/all-separators → root |
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

**Gotcha:** `LoadField`/`StoreFieldReg`/`StoreFieldImm`/`StoreFieldMinusOne` all **clobber EAX and EBP** (they build the field address in EBP and the offset immediate in EAX). Never keep a value you still need in EAX/EBP across one of these; in particular, an entry/struct address reused across several `LoadField`s must live in a different register (e.g. `fs_exec_core` holds the dir-entry addr in R12). `SpillStore`/`SpillLoad` clobber **EBP** only.
| `Imm16(asm, reg, value)` | :2007 | MovImm16 for 16-bit constants (OsLayout offsets > 255) |
| `SetupPrivilegedStack(asm)` | :2007 | ESP = PrivilegedStackTop (required before first CALL) |
| `FrameEntryAddress(asm, frameReg, dest)` | :897 | dest = FrameTableBase + frame * FrameTableEntryBytes |
| `FrameBaseAddress(asm, frameReg, dest, pageShift)` | :909 | dest = FramePoolBase + frame << pageShift |
| `EmitPteAddress(asm, procReg, pageReg, dest, scratch, strideShift, pteShift)` | :921 | dest = &PTE[proc][page] |
| `EmitPageCopy(asm, srcReg, dstReg, prefix, valReg)` | :937 | Word loop copying PageSize bytes; needs unique prefix string |
| `EmitSwapCopy(asm, srcSlotReg, dstSlotReg, lenScratch, addrScratch)` | :615 | DREAD src→scratch, DWRITE scratch→dst |

`R(byte reg)` converts a byte register-index constant (e.g. `EAX = (byte)RegisterName.EAX`) to `RegisterName` for Assembler emit methods.

---

## ISA Authoring & Debugging (READ THIS before writing or debugging ISA)

Every expensive debug hunt in this project's history (Session Cost Log: Inc 6 "block 900", the Phase 3 multi-session hang, Inc 5b's 6 red tests) had the **same root**: a register or scratch slot clobbered across a call, or a silently-wrong immediate — bugs that throw **no exception**, so they cost 20–30K tokens to find by probing. This section exists to make that a ~2-minute lookup instead. **Cost driver #1 is not reading code — it's re-debugging this class of bug.**

### Clobber contract (what survives a call, what doesn't)

| Helper / call | Clobbers | Survives / note |
|---------------|----------|-----------------|
| `LoadField` / `StoreFieldReg` / `StoreFieldImm` / `StoreFieldMinusOne` | **EAX + EBP** (offset built in EAX, addr in EBP) | An entry/struct addr reused across several `LoadField`s must live elsewhere (e.g. R12). This is the "block 900" bug: `memory[8]` = IVT[2] ≈ 900, i.e. EAX held an addr that got overwritten. |
| `SpillStore` / `SpillLoad` | **EBP** only | Use freely to park loop state across calls that clobber registers. |
| `cache_get` / `cache_dirty` / `cache_*` | nearly all registers | **EDX and EDI survive** (convention: carry a value across `cache_*` in EDX/EDI). |
| `fs_alloc_block` / `fs_chain_set_next` | EDX/EDI too | State that must outlive these → spill to `OsLayout.FsScratch*` / `FsRw*`. |
| `fs_chain_next` | most registers | Spill loop state (`FsRw*`) before calling in a walk loop. |
| `page_in` | **R11** (internal scratch) | `ensure_user_page` spills isWrite across the `page_in` call for exactly this reason. |
| Any `Call(...)` | return addr pushed to privileged stack | `SetupPrivilegedStack` must run before the **first** CALL in a routine. |

**Convention for new subroutines:** give every `Emit*` subroutine a header comment stating `Input:` (regs), `Output:` (reg), and `Clobbers:` — the newer ones (`fs_load_image`, `ensure_user_page`, `user_word_addr`) already do. When calling one, do not hold a needed value in a clobbered register across the CALL.

### Silent-failure taxonomy → first move

| Symptom | Most likely cause | First check |
|---------|-------------------|-------------|
| **Hang**, process `state=Ready, waitReason=None` (not a legit block) | Infinite loop: a loop counter went negative/huge | **8-bit immediate truncation** — `asm.MovImm(reg, N)` emits `(byte)N`, so any `N >= 256` silently becomes `N & 0xFF` (PageSize=256 → 0). Use `Imm16`/`MovImm16` for every OsLayout constant ≥ 256. This was the Phase 3 hang. |
| **Crash / jump to a wild address** (e.g. "block 900") | A register holding an address was clobbered | Trace register liveness across each CALL; look for an addr kept in EAX/EBP across a `LoadField` (see table above). |
| **Wrong value round-trips** (off by a lot, or 1 char instead of N) | A scratch slot reused by a nested call | Check the callee's clobber row; a loop var reused as a callee's counter (Inc 5b: `fs_grow_chain` reused `FsRwRemaining`). |
| **Routine falls through to the wrong code** | Duplicate `asm.Label` name | `Assembler.Label` does `labels[name] = code.Count` — it **silently overwrites** a duplicate. Every subroutine must use a unique label prefix (`fsyr_*`, `fli_*`, `di_*`). |
| **A kernel write to a user page is lost on fork or eviction** (child/reader sees stale/zero, but a plain STORE to the same page survives) | The write didn't mark the frame **dirty** | `flush_frames` (fork) and eviction only write back *dirty* frames. STORE sets dirty via `Hardware.StampFrame`; a kernel-mediated write (INS/OUTS/FSYS/readdir out-buffer) must go through `ensure_user_page` with isWrite, which now sets the dirty bit. If you add a new kernel→user write path, route it through `user_word_addr`/`ensure_user_page` (isWrite=1), don't hand-translate. The STORE-works-but-INS-doesn't asymmetry is the tell. |
| **Scheduler crashes with a wild entry address only after a program runs a long time** (`resume_mlfq` LOADs from `ECX*EntrySize + ProcessTableOffset` where ECX is garbage) | A `Call(...)` clobbered a register the fall-through tail still needs | `resume_mlfq` uses **ECX = round-robin start index** from its caller. `EmitContextSwitch`'s periodic `Call("cache_flush")` (every `CacheFlushInterval` switches) clobbers ECX (all `cache_*` clobber nearly everything) — the tail then jumps to resume_mlfq with garbage ECX. Fixed by reloading ECX before the jump. **Lesson:** when a routine `Call`s a clobbering sub and then *falls through / jumps* to code that reads a register (not just `resume_mlfq`'s ECX — any tail), reload it after the CALL. Tell: `mem[CurrentIndexOffset]` is valid but the ECX *register* is garbage at the fault (register, not memory, corruption); the bug hides until a periodic/rare CALL path executes. |
| **A resumed process runs at a wild address / dies right after an OSRET, but only when it did something between the save and the resume** (e.g. a signal handler that makes a syscall) | You snapshot/restore a process's **register file but not its saved `ProcessEntryLevel`** — and something clobbered Level in the entry meanwhile | Persisting only the 96-byte register file (offsets 0–95) is not enough to fully checkpoint a process: OSRET resolves the resume address as `GetProgramBaseFor(entry.Level) + savedEIP`. While a process runs, an unrelated OS routine can `SaveRegs` its *current* (mid-syscall, Kernel-level) context into the entry — clobbering `entry.Level`→Kernel. If your restore copies only the regfile, OSRET then resumes at **kernel base 0 + a user EIP** = a wild OS address. This was JC-E's `sig_copy`: fixed by copying through `ProcessEntryLevel` (`SignalSaveStride`=`Hardware.ProcessEntryState`=100). **Tell:** a bare/STORE/non-output handler resumes fine but a handler that does OUT/INS crashes — the syscall is what lets `IvtWakeOutput`'s `wk_resume` SaveRegs the kernel context into the entry. **Lesson:** a full process checkpoint = regfile **+ Level**; when hand-rolling save/restore, include the level word. |

### The memory-marker diagnostic (the technique that cracked both big hangs)

When an ISA routine misbehaves silently, **write trace values into free heap and read them back with `Test.ReadWord`** after the run — even a hung run, since the markers were written before it stalled.

- Write markers with a spare register: `Imm16(asm, EBP, OsLayout.TotalSize + BIG); asm.MovImm(R(EAX), sentinel); asm.Store(R(EBP), R(EAX));`
- Useful markers: an **iteration counter** (load/inc/store a heap cell at the top of a suspect loop — if it climbs unbounded, it's an infinite loop) and a **first-divergence latch** (on the first iteration a value goes out of range, snapshot the relevant registers/scratch and set a "latched" flag so later iterations don't overwrite it).
- **Placement:** markers go in heap **above the process's allocations**, clear of both the install-staging region (just below `TotalSize`) and the buddy heap (which starts *at* `TotalSize` and grows up). In practice `TotalSize + 12000`-ish worked; **verify the marker reads back your sentinel** — if it reads garbage, a process allocation reached it, so move higher (or size the test machine with more headroom).
- **Do NOT use `Out()` for diagnostics inside an atomic, interrupt-masked OS routine** — it interacts with `KernelOutput`'s device-busy blocking and can itself hang. Memory markers are inert.
- **Remove all markers + any probe test before committing** (they leave `// DEBUG` breadcrumbs — grep for them).

### Other authoring facts

- **Program base:** User = `currentProcessInstructionStart`, Kernel = 0. JMP/CALL/RET targets and saved EIP are **base-relative** (position-independent) — never hardcode absolute code addresses.
- **Deliver-a-result-to-the-caller idiom** (FSYS / fork-style, atomic dispatch): `SaveRegs(entry)` → write the result into the entry's EAX slot → `LoadRegs(entry)` → `SetLayout(entry)` → `OsRet(level)`. This is EmitWait's reap path; it persists the captured clean trap frame and overrides only EAX.
- **Exact numbers** (offsets, IVT slots, sizes) come from the **os-facts skill**, not from reading source or trusting a table — `dotnet run --project .claude/skills/os-facts/dump -- <section>`.

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
    public override int OsMemorySize => OsLayout.TotalSize;          // 32300 (DataBase 20480 + 11820)
    protected override bool UsesFilesystemBoot => true;             // programs install to /bin, run FS-backed (Phase 4)
    public override byte[] BuildOsImage(int osMemoryBase) => OsRoutines.BuildOsImage();
    public BasicOS(TextWriter log) : base(CollectTraps(), log) { }
    protected override void OnBooted(Hardware hw) { /* magic-guarded FS auto-format */ }
    private static List<Trap> CollectTraps() { /* reflection */ }
}
```

`CollectTraps()` uses `Assembly.GetExecutingAssembly().GetTypes()` — discovers all non-abstract `ITrapProvider` in this DLL. Newly added `XxxTrapProvider` classes are included automatically.
