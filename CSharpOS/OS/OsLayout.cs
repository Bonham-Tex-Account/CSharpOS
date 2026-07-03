namespace CSharpOS;

/// <summary>
/// Layout of the OS in-memory region: an interrupt vector table, the assembled OS
/// routines, then the OS data structures (scheduler state, process table, buddy
/// allocator bitmap). Offsets are absolute addresses, since OS code runs in
/// Privileged mode with a program base of 0. Shared between the routine assembler
/// (which references these as immediates) and the C# side (which seeds the data).
/// </summary>
public static class OsLayout
{
    // Code begins right after the IVT; the data section sits at a fixed base so its
    // field offsets are compile-time constants the routines can load directly. The
    // base must clear the assembled routines; BuildOsImage guards against overrun.
    public const int CodeBase = Hardware.IvtSize;
    // The assembled OS routines (scheduler, allocator, disk, and the spawning family:
    // spawn/fork/exec/wait/exit) sit between CodeBase and DataBase; BuildOsImage guards
    // against overrun. Raised to 8192 once the spawning routines were added, then to 12288
    // once the paging family (page-fault handler + frame/swap/COW subroutines) was added,
    // then to 16384 once the filesystem family (cache manager + block/directory/path
    // routines, Increments 2–4) was added, then to 20480 once the file read/write and
    // exec-by-path routines (Increments 5–6) pushed the code to ~16.7 KB.
    public const int DataBase = 20480;

    // ---- scheduler state header (4-byte fields at the data section base) ---
    public const int ProcessCountOffset    = DataBase + 0;
    public const int CurrentIndexOffset    = DataBase + 4;   // -1 when the CPU is idle
    public const int BuddyHeapStartOffset  = DataBase + 8;   // start address of managed heap
    public const int BuddyHeapSizeOffset   = DataBase + 12;  // power-of-2 heap size
    public const int BoostTimerOffset      = DataBase + 16;  // MLFQ: ticks until global priority reset
    public const int QuantumTableOffset    = DataBase + 20;  // MLFQ: 4 × 4-byte tick thresholds per level
    public const int BuddyMinBlockOffset   = DataBase + 36;  // minimum allocatable block size (power of 2)
    public const int BuddyLevelsOffset     = DataBase + 40;  // tree depth: log2(HeapSize / MinBlock)
    public const int NextPidOffset         = DataBase + 44;  // monotonic PID counter (spawning)

    // ---- MLFQ constants ---------------------------------------------------
    public const int QueueCount    = 4;
    public const int BoostInterval = 20;

    // ---- buddy allocator constants ----------------------------------------
    // Default minimum block size; stored in OS data at BuddyMinBlockOffset so
    // tests can override it per instance by writing a different value before
    // seeding. The ISA allocator reads this from memory rather than baking it in.
    public const int BuddyDefaultMinBlock = 256;
    // Fixed number of bitmap words loaded into R8-R15 on each allocator call.
    // 8 words × 32 bits = 256 bits → supports trees with up to 255 nodes
    // (8 levels, heap up to 255 × MinBlock bytes).
    public const int BuddyBitmapWords = 8;

    // ---- process table -----------------------------------------------------
    public const int MaxProcesses       = 8;
    public const int ProcessTableOffset = DataBase + 48;  // after header + buddy fields + NextPid

    // ---- buddy bitmap (compact: 1 bit per tree node, bit=1 means FREE) ----
    // Stored as BuddyBitmapWords consecutive 4-byte words immediately after the
    // process table. Initially only the root bit (bit 0 of word 0) is set.
    public const int BuddyBitmapOffset = ProcessTableOffset + MaxProcesses * Hardware.ProcessEntrySize;

    // ---- privileged scratch stack -----------------------------------------
    // A small stack the privileged OS routines point ESP at so they can CALL/RET
    // shared subroutines (e.g. the buddy allocator). Safe as a single shared region
    // because privileged routines run atomically and never nest.
    public const int PrivilegedStackSize = 64;
    public const int PrivilegedStackOffset = BuddyBitmapOffset + BuddyBitmapWords * 4;
    public const int PrivilegedStackTop = PrivilegedStackOffset + PrivilegedStackSize;

    // ---- paging: per-process page tables (virtual memory, Phase 1) --------
    // The MMU translates user-mode virtual addresses through the running process's page
    // table, which lives here in the OS region. Phase 1 keeps each process in one
    // contiguous buddy-allocated block and seeds its table linearly (page p -> the page's
    // physical base ProgramAddress + p*PageSize), so translation yields the same physical
    // address as the old base+offset scheme (behavior-preserving). Placed after the
    // privileged stack so the process-table/bitmap/stack offsets above are unchanged; only
    // TotalSize grows. A PTE holds the **physical base address** of the mapped page (exact
    // for any block alignment), or -1 for an unmapped page. Phase 2's frame allocator can
    // store frame*PageSize in the same field once frames are page-aligned by construction.
    public const int PageSize = 256;                  // == BuddyDefaultMinBlock
    public const int MaxPagesPerProcess = 128;        // 128 * 256 = 32 KiB of mapped virtual space per process
    public const int PageTableEntryBytes = 4;
    // PTE sentinels. >= 0 is a resident page's physical base. UnmappedPage marks a page
    // outside the process's address space — a user access to it is a protection fault that
    // terminates the process (the MMU is the sole memory-protection mechanism; there is no
    // linear fallback and no bounds trap). NonResidentPage marks a page that belongs to the
    // process but is not currently resident — touching it raises a demand page fault.
    public const int UnmappedPage = -1;
    public const int NonResidentPage = -2;
    public const int PageTableBytesPerProcess = MaxPagesPerProcess * PageTableEntryBytes;
    public const int PageTableBase = PrivilegedStackTop;
    public const int PageTableRegionSize = MaxProcesses * PageTableBytesPerProcess;

    // ---- paging Phase 2 increment 2: physical frame pool + frame table ----
    // Demand paging now maps resident pages into a SMALL shared pool of physical frames
    // instead of each page's own block home, so resident capacity is scarce and the ISA
    // page-fault handler must evict an LRU victim (writing it back to its home when dirty)
    // to make room. A resident PTE holds the frame's physical base (within the pool); the
    // page's content otherwise lives at its block home (ProgramAddress + page*PageSize) —
    // increment 3 will move that home onto a Bin-disk swap slot. The frame table ("core
    // map") records, per frame, which process/page owns it (so eviction can flip the
    // owner's PTE back to non-resident), the page's home (so a dirty page writes back to
    // the right place), a dirty bit, and an LRU stamp.
    public const int FrameCount = 4;            // small on purpose: forces eviction in demos/tests
    // Frame-table entry fields (4-byte words), one entry per physical frame.
    public const int FrameOccupiedField  = 0;   // 0 = free, 1 = holds a page
    public const int FrameOwnerProcField = 4;   // owning process-table index
    public const int FrameOwnerPageField = 8;   // owning virtual page number
    public const int FrameHomeField      = 12;  // physical base of a RAM-home page's block home
    public const int FrameDirtyField     = 16;  // 0 = clean (drop on evict), 1 = needs write-back
    public const int FrameLastUseField   = 20;  // LRU stamp (the MMU bumps it on each access)
    public const int FrameSwapField      = 24;  // swap slot this frame is backed by, or -1 for a RAM-home frame
    public const int FrameCowField       = 28;  // 1 = copy-on-write share (read-only; a write traps to copy)
    public const int FrameTableEntryBytes = 32;
    public const int FrameTableBase = PageTableBase + PageTableRegionSize;
    public const int FrameTableSize = FrameCount * FrameTableEntryBytes;
    // The frame pool: FrameCount contiguous PageSize frames. Frame f's physical base is
    // FramePoolBase + f * PageSize.
    public const int FramePoolBase = FrameTableBase + FrameTableSize;
    public const int FramePoolSize = FrameCount * PageSize;

    // ---- paging Phase 2 increment 3: Bin-disk swap backing for DATA pages ----
    // A page in the process's DATA region (between the program image and the user stack)
    // is backed by a dedicated Bin-disk swap slot instead of its RAM-block home: it DREADs
    // in on fault and DWRITEs back on dirty eviction, so the data heap can live on disk and
    // need not occupy RAM beyond its frame. Code pages (fetched untranslated) and stack
    // pages (the kernel stack is pinned) keep their RAM-block home. Each (process, page)
    // gets a fixed, deterministically-computed swap slot — no allocator. The swap region
    // sits above the disk's image slots; spawn/exec zero a process's data slots and fork
    // copies them, so a data slot is always occupied before its first DREAD.
    public const int SwapSlotsPerProcess = MaxPagesPerProcess;
    public const int SwapSlotCount = MaxProcesses * SwapSlotsPerProcess;
    public const int SwapBase = Hardware.DefaultDiskSlots;   // swap slots follow the image slots
    // A non-resident DATA page's PTE encodes its swap slot as -(slot + SwapPteBias); a
    // non-resident RAM-home page is NonResidentPage (-2); unmapped is -1. The bias keeps
    // every swap encoding <= -3, distinct from the -1 / -2 sentinels.
    public const int SwapPteBias = 3;

    // Two OS scratch pages (PageSize each), appended after the frame pool: a zero page
    // (never written, used as the DWRITE source when zeroing a slot) and a transfer page
    // (DREAD target then DWRITE source when fork deep-copies a parent slot to a child slot).
    public const int ZeroPageBase = FramePoolBase + FramePoolSize;
    public const int SwapScratchBase = ZeroPageBase + PageSize;

    // ---- paging Phase 3: copy-on-write fork (data pages) ------------------
    // fork shares the parent's data-page snapshot with the child instead of copying it: a
    // shared DATA page is non-resident with a COW PTE that references the shared swap slot,
    // or resident in a write-protected COW frame. A write traps (the MMU re-raises the page
    // fault for a resident COW frame) and the handler copies the page privately for the
    // writer's own deterministic slot, leaving the partner with the snapshot. Because the
    // slots are deterministic 2-way, a process resolves any existing COW (materialises its
    // partner's private copies) before it forks/exits/execs again, so COW is only ever a
    // clean parent<->child relationship between one fork and the next teardown.
    //
    // A COW PTE encodes the SHARED swap slot as -(slot + SwapCowBias); SwapCowBias sits well
    // above the largest plain-swap magnitude (MaxSlot + SwapPteBias) so the two encodings
    // never overlap, and stays within a 16-bit immediate so the ISA can build it.
    public const int SwapCowBias = 4096;
    // Per-process COW partner index (the other end of the share), or -1 for none. One word
    // per process; appended after the scratch pages so existing offsets are unchanged.
    public const int CowPartnerBase = SwapScratchBase + PageSize;
    public const int CowPartnerRegionSize = MaxProcesses * 4;

    // ---- filesystem RAM write-back cache (Increment 2) --------------------
    // A fixed pool of RAM cache slots buffering the disk's file blocks: the ISA cache
    // manager (OsRoutines) reads/writes file data here and only touches the disk on a miss,
    // on eviction of a dirty slot, or on a periodic flush. Sized as a fraction of the disk's
    // block count (≈ 1/20) — small on purpose so demos/tests exercise eviction. Placed after
    // the COW table so every offset above is unchanged; only TotalSize grows.
    //
    // Header (3 words): a monotonic LRU clock the manager stamps slots with on each access;
    // a periodic-flush countdown decremented once per context switch (the sim analog of a
    // ~30 s timer — the only clean ISA clock, mirroring BoostTimer); and a scratch word the
    // IvtCacheOp dispatcher parks a subroutine's return value in (RunOsRoutineSynchronously
    // restores registers, so tests read the result from memory, not a register).
    public const int CacheClockOffset      = CowPartnerBase + CowPartnerRegionSize;
    public const int CacheFlushTimerOffset = CacheClockOffset + 4;
    public const int CacheResultOffset     = CacheFlushTimerOffset + 4;
    public const int CacheHeaderSize       = 12;

    // Context-switch ticks between periodic flushes. Not literally 30 s — the simulation's
    // periodic-write-back cadence, tunable like BoostInterval.
    public const int CacheFlushInterval = 200;

    // One slot per cached block. Slot fields (4-byte words then the block data):
    //   valid  — 0 = empty (zero-init means the whole pool starts empty; no seeding needed)
    //   block  — the file-block number this slot caches
    //   dirty  — 1 = modified since load; written back on evict/flush (write-back)
    //   pin    — pin count; a pinned slot is never chosen as an eviction victim
    //   stamp  — LRU stamp (the manager writes the current clock on each access)
    //   data   — the block's bytes (FileBlockSize)
    public const int CacheSlotCount  = 13;   // ≈ Hardware.DefaultFileBlockCount / 20
    public const int CacheValidField = 0;
    public const int CacheBlockField = 4;
    public const int CacheDirtyField = 8;
    public const int CachePinField   = 12;
    public const int CacheStampField = 16;
    public const int CacheDataField  = 20;
    public const int CacheSlotSize   = CacheDataField + Hardware.DefaultFileBlockSize; // 276
    public const int CacheSlotTableBase = CacheClockOffset + CacheHeaderSize;
    public const int CacheRegionSize    = CacheSlotCount * CacheSlotSize;

    // ---- filesystem op result scratch (Increment 3) -----------------------
    // Where the IvtFsOp dispatcher parks a filesystem op's return value (an allocated block
    // number, a chain's next pointer, etc.), read back from memory by a synchronous test
    // dispatch — same rationale as CacheResult.
    public const int FsResultOffset = CacheSlotTableBase + CacheRegionSize;

    // ---- filesystem routine scratch (Increment 4) -------------------------
    // The cache subroutines clobber almost every register (only EDX/EDI reliably survive a
    // cache_get), so the directory routines spill any state that must persist across a nested
    // cache/chain call to these fixed OS-RAM words instead of registers.
    public const int FsScratchBase       = FsResultOffset + 4;
    public const int FsScratchName       = FsScratchBase + 0;   // lookup/insert: name buffer addr
    public const int FsScratchHash       = FsScratchBase + 4;   // lookup: target name hash
    public const int FsScratchType       = FsScratchBase + 8;   // insert: entry type
    public const int FsScratchFirst      = FsScratchBase + 12;  // insert: first-block field
    public const int FsScratchDir        = FsScratchBase + 16;  // insert: directory block
    public const int FsScratchEntryBlock = FsScratchBase + 20;  // block holding the matched/new entry
    public const int FsScratchFreeBlock  = FsScratchBase + 24;  // insert: newly chained block
    public const int FsScratchArgA       = FsScratchBase + 28;  // mkdir: name addr (survives fs_dir_insert)
    public const int FsScratchArgB       = FsScratchBase + 32;  // mkdir: new dir block (survives fs_dir_insert)
    public const int FsScratchWords      = 10;                  // one spare

    // ---- filesystem path-resolution scratch (Increment 4b) ----------------
    // fs_path_resolve walks a "/a/b/c" path (word-per-char) component by component. All of its
    // loop state lives in memory (fs_dir_lookup clobbers the registers): the current read
    // position, the current directory block, a "last component" flag, and a buffer the current
    // path component is extracted into for the per-directory lookup.
    public const int FsPathBase          = FsScratchBase + FsScratchWords * 4;
    public const int FsPathPos           = FsPathBase + 0;   // read cursor into the path string
    public const int FsPathDir           = FsPathBase + 4;   // directory currently being descended
    public const int FsPathLast          = FsPathBase + 8;   // 1 = the extracted component is the last
    public const int FsPathComponentBase = FsPathBase + 12;  // NameMaxChars words: the extracted name
    public const char FsPathSeparator    = '/';

    // ---- open-file table + open-syscall scratch (Increment 5) -------------
    // A system-wide table of open files. A process's fd slot (2..FdCount-1) stores an OFT
    // index + 1 (0 = free), so a zeroed fd table means "no files open" without any seeding.
    // Each OFT entry remembers the file's first block, the read/write byte offset, the file
    // size, and the location of its directory entry (block + byte offset within the block)
    // so a write can update the on-disk size.
    public const int MaxOpenFiles     = 8;
    public const int OftInUse         = 0;
    public const int OftFirstBlock    = 4;
    public const int OftOffset        = 8;
    public const int OftSize          = 12;
    public const int OftDirBlock      = 16;
    public const int OftEntryOffset   = 20;   // byte offset of the dir entry within its block
    public const int OftEntryBytes    = 24;
    public const int OftBase          = FsPathComponentBase + FsLayout.NameMaxChars * 4;
    public const int OftRegionSize    = MaxOpenFiles * OftEntryBytes;

    // Working scratch for fs_open_core (spilled across the cache/dir calls it makes).
    public const int FsOpenBase        = OftBase + OftRegionSize;
    public const int FsOpenAbsPath     = FsOpenBase + 0;
    public const int FsOpenFlags       = FsOpenBase + 4;
    public const int FsOpenProc        = FsOpenBase + 8;
    public const int FsOpenEntryAddr   = FsOpenBase + 12;
    public const int FsOpenFirst       = FsOpenBase + 16;
    public const int FsOpenSize        = FsOpenBase + 20;
    public const int FsOpenDirBlock    = FsOpenBase + 24;
    public const int FsOpenEntryOffset = FsOpenBase + 28;
    public const int FsOpenWords        = 8;

    // Working scratch for fs_read_core / fs_write_core (byte-level I/O across a block chain;
    // every loop variable is spilled here because the cache/chain calls clobber registers).
    public const int FsRwBase        = FsOpenBase + FsOpenWords * 4;
    public const int FsRwFd          = FsRwBase + 0;
    public const int FsRwBuf         = FsRwBase + 4;
    public const int FsRwCount       = FsRwBase + 8;
    public const int FsRwProc        = FsRwBase + 12;
    public const int FsRwOft         = FsRwBase + 16;   // OFT entry addr for this fd
    public const int FsRwCurBlock    = FsRwBase + 20;   // current block in the walk
    public const int FsRwRemaining   = FsRwBase + 24;   // chars left to copy
    public const int FsRwCharInBlock = FsRwBase + 28;   // char position within the current block
    public const int FsRwBufPtr      = FsRwBase + 32;   // running user-buffer address
    public const int FsRwCopied      = FsRwBase + 36;   // total chars to copy / return value
    public const int FsRwCounter     = FsRwBase + 40;   // generic loop counter (skip / grow)
    public const int FsRwWords        = 12;

    // ---- kernel-mediated user-memory access (Phase 3 rectification) -------
    // Scratch for `user_word_addr` (ISA): translates one virtual word address through the
    // current process's page table, faulting/COW-resolving via ensure_user_page as needed.
    // va's page/offset are spilled here because ensure_user_page clobbers the registers.
    public const int PageXlateBase   = FsRwBase + FsRwWords * 4;
    public const int PageXlateOffset = PageXlateBase + 0;
    public const int PageXlatePage   = PageXlateBase + 4;
    public const int PageXlateWords  = 2;

    // Scratch for the C#<->ISA handoff around IvtEnsureUserPage: Hardware.UserToPhysical
    // writes IsWrite before dispatching, then reads Result after the routine returns.
    public const int EnsureUserPageBase       = PageXlateBase + PageXlateWords * 4;
    public const int EnsureUserPageIsWrite    = EnsureUserPageBase + 0;
    public const int EnsureUserPageResult     = EnsureUserPageBase + 4;
    public const int EnsureUserPageWords      = 2;

    // Scratch for the FSYS read/write wrapper's page-chunked copy loop (fsy_read/fsy_write):
    // it walks the user buffer's virtual pointer one page-chunk at a time, translating each
    // chunk's start address via user_word_addr and delegating the actual transfer to the
    // (unchanged, absolute-address) fs_read_core/fs_write_core for that chunk.
    public const int FsWrapBase      = EnsureUserPageBase + EnsureUserPageWords * 4;
    public const int FsWrapFd        = FsWrapBase + 0;
    public const int FsWrapPtr       = FsWrapBase + 4;    // virtual pointer, advances per chunk
    public const int FsWrapRemaining = FsWrapBase + 8;    // words left to transfer
    public const int FsWrapCopied    = FsWrapBase + 12;   // total words transferred so far
    public const int FsWrapIsWrite   = FsWrapBase + 16;   // 1 = writing into the user buffer (Read), 0 = reading from it (Write)
    public const int FsWrapChunk     = FsWrapBase + 20;   // words in the current page-chunk
    public const int FsWrapWords     = 6;

    public static int OftAddress(int index)
    {
        return OftBase + index * OftEntryBytes;
    }

    // Total OS region size.
    public const int TotalSize = FsWrapBase + FsWrapWords * 4;

    // Absolute address of cache slot `i`.
    public static int CacheSlotAddress(int i)
    {
        return CacheSlotTableBase + i * CacheSlotSize;
    }

    public static int ProcessEntryAddress(int index)
    {
        return ProcessTableOffset + index * Hardware.ProcessEntrySize;
    }

    // Absolute address (within the OS region) of process slot `index`'s page table.
    public static int PageTableAddress(int index)
    {
        return PageTableBase + index * PageTableBytesPerProcess;
    }

    // Absolute address of frame `frame`'s core-map entry in the frame table.
    public static int FrameTableEntry(int frame)
    {
        return FrameTableBase + frame * FrameTableEntryBytes;
    }

    // Absolute physical base address of frame `frame` in the frame pool.
    public static int FrameBase(int frame)
    {
        return FramePoolBase + frame * PageSize;
    }

    // The fixed Bin-disk swap slot backing process `processIndex`'s virtual page `page`.
    public static int SwapSlot(int processIndex, int page)
    {
        return SwapBase + processIndex * SwapSlotsPerProcess + page;
    }

    // The non-resident PTE encoding for a privately swap-backed page on `swapSlot`, and its
    // inverse. A COW PTE (below) is more negative, so test IsCowPte first.
    public static int SwapPte(int swapSlot) { return -(swapSlot + SwapPteBias); }
    public static int SwapSlotFromPte(int pte) { return -pte - SwapPteBias; }
    public static bool IsSwapPte(int pte) { return pte <= -SwapPteBias && !IsCowPte(pte); }

    // The non-resident PTE encoding for a copy-on-write page sharing `swapSlot`, its inverse,
    // and its test. IsCowPte holds for the COW range only (more negative than any plain swap).
    public static int CowPte(int swapSlot) { return -(swapSlot + SwapCowBias); }
    public static int CowSlotFromPte(int pte) { return -pte - SwapCowBias; }
    public static bool IsCowPte(int pte) { return pte <= -SwapCowBias; }

    // Absolute address of process `processIndex`'s COW-partner word.
    public static int CowPartnerAddress(int processIndex)
    {
        return CowPartnerBase + processIndex * 4;
    }

    // True when virtual page `page` of a process whose image is `programSize` bytes and
    // whose data region is `requiredMemory` bytes falls in the swap-backed DATA region
    // (its page start lies within [programSize, programSize + requiredMemory)). Code and
    // stack pages return false (they keep their RAM-block home).
    public static bool IsDataPage(int page, int programSize, int requiredMemory)
    {
        int pageStart = page * PageSize;
        return pageStart >= programSize && pageStart < programSize + requiredMemory;
    }
}
