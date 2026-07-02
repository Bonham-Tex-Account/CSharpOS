namespace CSharpOS;

// Demand-paging fault handler + frame/swap/COW subroutines (see BuildOsImage / root CLAUDE.md).
public static partial class OsRoutines
{
    // ===== EmitPageFault =====================================================
    // IvtPageFault: demand-paging fault handler (Phase 2, increment 2). Entered with the
    // faulting page number in EAX when a user data access touches a non-resident page.
    // Makes the page resident by mapping it into a physical frame from the small shared
    // frame pool: it claims a free frame, or — when the pool is full — evicts the
    // least-recently-used resident frame (writing it back to its home first if dirty) and
    // flips that page's owner PTE back to non-resident. It then copies the faulting page in
    // from its block home, records the frame in the core map, points the PTE at the frame,
    // and reschedules — the faulting instruction re-runs and now translates. The faulting
    // context (IP rewound to the faulting instruction) was captured on entry; SAVEREGS
    // persists it so the process can resume.
    //
    // A DATA page is backed by a Bin-disk swap slot (DREAD in / DWRITE out); a code or
    // stack page is RAM-home (block memcpy in / out). The handler reads the faulting page's
    // non-resident PTE to tell which: a swap PTE (<= -3) encodes the swap slot, the -2
    // sentinel means RAM-home. The frame's core-map Swap field records the backing so the
    // later eviction of that frame writes it back to the right place.
    //
    // Persistent registers across the handler: R12 = faulting page, R13 = current index,
    // R14 = the faulting page's swap slot (or -1 when RAM-home), R15 = the chosen frame.
    private static void EmitPageFault(Assembler asm)
    {
        // PageSize, the page-table stride, and the PTE size are powers of two; shift by
        // their log2 (computed at emit time) instead of multiplying.
        int pageShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageSize);
        int tableStrideShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableBytesPerProcess);
        int pteShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableEntryBytes);

        asm.Mov(R(R12), R(EAX));                    // R12 = faulting page (helpers clobber EAX)

        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R13), R(EAX));                   // R13 = current index
        EntryAddress(asm, R13, EBX);                // EBX = faulting process's entry
        asm.SaveRegs(R(EBX));                        // persist faulting context (IP rewound)

        // Read the faulting PTE. A RESIDENT entry (>= 0) means the MMU re-raised the fault
        // for a write to a copy-on-write frame -> resolve the page privately and resume.
        EmitPteAddress(asm, R13, R12, R9, EDX, tableStrideShift, pteShift); // R9 = &PTE[cur][page]
        asm.Load(R(R8), R(R9));                       // R8 = pte
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Jns("pf_cow_write");                      // pte >= 0: resident COW write fault

        // Non-resident demand fault. Decode the backing into R14:
        //   COW page (pte <= -SwapCowBias): R14 = shared swap slot (DREAD read-only).
        //   private swap page (pte <= -3):  R14 = own swap slot.
        //   RAM-home page (-2):             R14 = -1.
        asm.MovImm16(R(EAX), OsLayout.SwapCowBias);
        asm.MovImm(R(EBX), 0);
        asm.Sub(R(EBX), R(EAX));                      // EBX = -SwapCowBias
        asm.Cmp(R(R8), R(EBX));
        asm.Jns("pf_src_notcow");                     // pte >= -bias: not COW
        asm.MovImm(R(EAX), 0);
        asm.Sub(R(EAX), R(R8));
        asm.MovImm16(R(EBX), OsLayout.SwapCowBias);
        asm.Sub(R(EAX), R(EBX));                      // EAX = -pte - SwapCowBias = shared slot
        asm.Mov(R(R14), R(EAX));
        asm.Jmp("pf_src_done");
        asm.Label("pf_src_notcow");
        asm.Mov(R(R11), R(R8));
        asm.MovImm(R(EAX), OsLayout.SwapPteBias);
        asm.Add(R(R11), R(EAX));                      // R11 = pte + bias
        asm.MovImm(R(EAX), 1);
        asm.Cmp(R(R11), R(EAX));
        asm.Js("pf_src_swap");                        // pte + bias < 1  =>  swap-backed
        asm.MovImm(R(R14), 0);
        asm.Dec(R(R14));                              // R14 = -1  (RAM-home marker)
        asm.Jmp("pf_src_done");
        asm.Label("pf_src_swap");
        asm.MovImm(R(R14), 0);
        asm.Sub(R(R14), R(R11));                      // R14 = -(pte + bias) = swap slot
        asm.Label("pf_src_done");

        // --- find a free frame (Occupied == 0) ---
        asm.MovImm(R(ESI), 0);                       // ESI = f
        asm.Label("pf_free");
        asm.MovImm(R(EAX), OsLayout.FrameCount);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("pf_lru");                           // no free frame -> evict LRU
        FrameEntryAddress(asm, ESI, R9);             // R9 = &frame[f]
        LoadField(asm, R9, OsLayout.FrameOccupiedField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("pf_take_free");                      // Occupied == 0
        asm.Inc(R(ESI));
        asm.Jmp("pf_free");

        asm.Label("pf_take_free");
        asm.Mov(R(R15), R(ESI));                     // frame = the free frame
        asm.Jmp("pf_fill");                          // empty frame: no eviction needed

        // --- pool full: choose the LRU victim (smallest LastUse stamp) ---
        asm.Label("pf_lru");
        asm.MovImm(R(R15), 0);                       // victim = frame 0
        FrameEntryAddress(asm, R15, R9);
        LoadField(asm, R9, OsLayout.FrameLastUseField, EDX); // EDX = minUse
        asm.MovImm(R(ESI), 1);                       // scan from frame 1
        asm.Label("pf_lru_scan");
        asm.MovImm(R(EAX), OsLayout.FrameCount);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("pf_evict");                         // scanned all frames
        FrameEntryAddress(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.FrameLastUseField, R10);
        asm.Cmp(R(R10), R(EDX));
        asm.Jns("pf_lru_next");                      // R10 >= minUse: not older
        asm.Mov(R(EDX), R(R10));                      // new minimum
        asm.Mov(R(R15), R(ESI));                      // new victim
        asm.Label("pf_lru_next");
        asm.Inc(R(ESI));
        asm.Jmp("pf_lru_scan");

        // --- evict the victim frame R15 (it is occupied); branch on its backing ---
        asm.Label("pf_evict");
        FrameEntryAddress(asm, R15, R9);             // R9 = &frame[victim]
        LoadField(asm, R9, OsLayout.FrameSwapField, R8); // R8 = victim swap slot (-1 if RAM-home)
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Js("pf_evict_ram");                      // R8 < 0: RAM-home victim

        // -- swap-backed victim: DWRITE the frame back to its swap slot if dirty --
        LoadField(asm, R9, OsLayout.FrameDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("pf_evict_swap_unmap");               // clean: drop without write-back
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base (src)
        asm.MovImm16(R(R10), OsLayout.PageSize);     // R10 = length
        asm.DWrite(R(R8), R(R11), R(R10));           // swap slot <- frame
        asm.Label("pf_evict_swap_unmap");
        // owner PTE := the non-resident encoding for this slot, preserving COW: a COW victim
        // goes back to CowPte (still shared), a private victim to SwapPte.
        FrameEntryAddress(asm, R15, R9);
        LoadField(asm, R9, OsLayout.FrameSwapField, R8);       // R8 = swap slot
        LoadField(asm, R9, OsLayout.FrameOwnerProcField, ESI); // ESI = oldProc
        LoadField(asm, R9, OsLayout.FrameOwnerPageField, EDI); // EDI = oldPage
        LoadField(asm, R9, OsLayout.FrameCowField, ECX);       // ECX = COW flag
        EmitPteAddress(asm, ESI, EDI, R11, EDX, tableStrideShift, pteShift); // R11 = &PTE
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(ECX), R(EAX));
        asm.Jz("pf_eu_swapbias");
        asm.MovImm16(R(R10), OsLayout.SwapCowBias);
        asm.Jmp("pf_eu_havebias");
        asm.Label("pf_eu_swapbias");
        asm.MovImm(R(R10), OsLayout.SwapPteBias);
        asm.Label("pf_eu_havebias");
        asm.Mov(R(EAX), R(R8));
        asm.Add(R(EAX), R(R10));                      // EAX = slot + bias
        asm.MovImm(R(R10), 0);
        asm.Sub(R(R10), R(EAX));                      // R10 = -(slot + bias)
        asm.Store(R(R11), R(R10));
        asm.Jmp("pf_fill");

        // -- RAM-home victim: copy the frame back to its block home if dirty --
        asm.Label("pf_evict_ram");
        LoadField(asm, R9, OsLayout.FrameDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("pf_evict_ram_unmap");                // clean: drop without write-back
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base (src)
        FrameEntryAddress(asm, R15, R9);
        LoadField(asm, R9, OsLayout.FrameHomeField, R8);  // R8 = home (dst)
        EmitPageCopy(asm, R11, R8, "pf_wbr", R10);   // frame -> home
        asm.Label("pf_evict_ram_unmap");
        FrameEntryAddress(asm, R15, R9);
        LoadField(asm, R9, OsLayout.FrameOwnerProcField, ESI); // ESI = oldProc
        LoadField(asm, R9, OsLayout.FrameOwnerPageField, EDI); // EDI = oldPage
        EmitPteAddress(asm, ESI, EDI, R11, EDX, tableStrideShift, pteShift); // R11 = &PTE
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Dec(R(EAX));                              // EAX = NonResidentPage (-2)
        asm.Store(R(R11), R(EAX));
        asm.Jmp("pf_fill");

        // --- fill frame R15 from the faulting page's backing (DREAD for swap, copy for RAM) ---
        asm.Label("pf_fill");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R14), R(EAX));
        asm.Js("pf_fill_ram");                       // R14 < 0: RAM-home

        // -- swap fill: DREAD the swap slot into the frame --
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base (dst)
        asm.DRead(R(R11), R(R14), R(ECX));           // frame <- swap slot (ECX = length, ignored)
        FrameEntryAddress(asm, R15, R9);             // record the occupant (swap-backed)
        StoreFieldImm(asm, R9, OsLayout.FrameOccupiedField, 1);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerProcField, R13);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerPageField, R12);
        StoreFieldImm(asm, R9, OsLayout.FrameHomeField, 0);
        StoreFieldImm(asm, R9, OsLayout.FrameDirtyField, 0);
        StoreFieldImm(asm, R9, OsLayout.FrameLastUseField, 0);
        StoreFieldReg(asm, R9, OsLayout.FrameSwapField, R14);
        // FrameCow = 1 when this page's (still-current) PTE is a COW share, so a later write
        // traps to resolve it; a private swap page is writable (0).
        EmitPteAddress(asm, R13, R12, EDI, EBX, tableStrideShift, pteShift);
        asm.Load(R(EAX), R(EDI));                     // EAX = original (pre-map) PTE
        asm.MovImm16(R(EBX), OsLayout.SwapCowBias);
        asm.MovImm(R(R10), 0);
        asm.Sub(R(R10), R(EBX));                      // R10 = -SwapCowBias
        asm.Cmp(R(EAX), R(R10));
        asm.Jns("pf_rec_notcow");                     // pte >= -bias: not COW
        FrameEntryAddress(asm, R15, R9);
        StoreFieldImm(asm, R9, OsLayout.FrameCowField, 1);
        asm.Jmp("pf_map");
        asm.Label("pf_rec_notcow");
        FrameEntryAddress(asm, R15, R9);
        StoreFieldImm(asm, R9, OsLayout.FrameCowField, 0);
        asm.Jmp("pf_map");

        // -- RAM fill: copy the page's block home into the frame --
        asm.Label("pf_fill_ram");
        EntryAddress(asm, R13, EBX);                 // EBX = entry
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R8); // R8 = ProgramAddress
        asm.Mov(R(R10), R(R12));
        asm.MovImm(R(EAX), pageShift);
        asm.Shl(R(R10), R(EAX));
        asm.Add(R(R8), R(R10));                       // R8 = home = ProgramAddress + page*PageSize
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base (dst)
        EmitPageCopy(asm, R8, R11, "pf_flr", R10);   // home -> frame
        FrameEntryAddress(asm, R15, R9);             // record the occupant (RAM-home)
        StoreFieldImm(asm, R9, OsLayout.FrameOccupiedField, 1);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerProcField, R13);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerPageField, R12);
        StoreFieldReg(asm, R9, OsLayout.FrameHomeField, R8);
        StoreFieldImm(asm, R9, OsLayout.FrameDirtyField, 0);
        StoreFieldImm(asm, R9, OsLayout.FrameLastUseField, 0);
        StoreFieldMinusOne(asm, R9, OsLayout.FrameSwapField);
        StoreFieldImm(asm, R9, OsLayout.FrameCowField, 0); // RAM-home pages are never COW

        // --- map the faulting page resident -> the frame base ---
        asm.Label("pf_map");
        EmitPteAddress(asm, R13, R12, R10, EDX, tableStrideShift, pteShift); // R10 = &PTE[cur][page]
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base
        asm.Store(R(R10), R(R11));                    // PTE := frame base (now resident)

        asm.Mov(R(ECX), R(R13));                      // resume_mlfq anchor = current index
        asm.Jmp("resume_mlfq");

        // --- resident copy-on-write write fault: resolve the page privately, then resume ---
        // The MMU re-raised the fault because the running process wrote a read-only COW
        // frame. pair_resolve gives both sharers private copies and makes this process's
        // frame writable; the faulting instruction then re-runs and the write commits.
        asm.Label("pf_cow_write");
        SetupPrivilegedStack(asm);
        asm.Call("pair_resolve");                     // resolves current process's page R12
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                     // resume_mlfq anchor = current index
        asm.Jmp("resume_mlfq");
    }

    // ===== EmitReleaseFrames =================================================
    // release_frames: free every frame owned by the current process (Occupied := 0), with
    // NO write-back — the process is exiting or replacing its memory, so the page contents
    // are discarded. Prevents a dead process's frame from later being evicted into RAM that
    // has since been freed and reused. CALL/RET subroutine (needs the privileged stack);
    // clobbers EAX/EBP/ESI/R8/R9/R10, preserves EBX.
    private static void EmitReleaseFrames(Assembler asm)
    {
        asm.Label("release_frames");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));                      // R8 = my index
        asm.MovImm(R(ESI), 0);
        asm.Label("rf_scan");
        asm.MovImm(R(EAX), OsLayout.FrameCount);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("rf_done");
        FrameEntryAddress(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.FrameOccupiedField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("rf_next");                            // already free
        LoadField(asm, R9, OsLayout.FrameOwnerProcField, R10);
        asm.Cmp(R(R10), R(R8));
        asm.Jnz("rf_next");                           // owned by another process
        StoreFieldImm(asm, R9, OsLayout.FrameOccupiedField, 0);
        asm.Label("rf_next");
        asm.Inc(R(ESI));
        asm.Jmp("rf_scan");
        asm.Label("rf_done");
        asm.Ret();
    }

    // ===== EmitFlushFrames ===================================================
    // flush_frames: write every DIRTY frame owned by the current process back to its home
    // and clear its dirty bit (the page stays resident in its frame). Used by fork before
    // it flat-memcpys the parent's home RAM, so the copy sees the parent's live data.
    // CALL/RET subroutine (needs the privileged stack). R12 = my index across the loop.
    private static void EmitFlushFrames(Assembler asm)
    {
        int pageShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageSize);
        asm.Label("flush_frames");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R12), R(EAX));                     // R12 = my index
        asm.MovImm(R(ESI), 0);
        asm.Label("ff_scan");
        asm.MovImm(R(EAX), OsLayout.FrameCount);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("ff_done");
        FrameEntryAddress(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.FrameOccupiedField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("ff_next");                            // free frame
        LoadField(asm, R9, OsLayout.FrameOwnerProcField, R10);
        asm.Cmp(R(R10), R(R12));
        asm.Jnz("ff_next");                           // owned by another process
        LoadField(asm, R9, OsLayout.FrameDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("ff_next");                            // clean: nothing to write back
        // dirty: write the frame back to its backing — a swap slot (DWRITE) or RAM home.
        LoadField(asm, R9, OsLayout.FrameSwapField, R11); // R11 = swap slot (-1 if RAM-home)
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R11), R(EAX));
        asm.Js("ff_ram");                             // R11 < 0: RAM-home frame
        FrameBaseAddress(asm, ESI, R8, pageShift);    // R8 = frame base (src)
        asm.MovImm16(R(R10), OsLayout.PageSize);      // R10 = length
        asm.DWrite(R(R11), R(R8), R(R10));            // swap slot <- frame
        asm.Jmp("ff_clean");
        asm.Label("ff_ram");
        FrameBaseAddress(asm, ESI, R8, pageShift);    // R8 = frame base (src)
        FrameEntryAddress(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.FrameHomeField, R11); // R11 = home (dst)
        EmitPageCopy(asm, R8, R11, "ff_wb", R10);     // copy frame -> home
        asm.Label("ff_clean");
        FrameEntryAddress(asm, ESI, R9);              // recompute (copy clobbered scratch)
        StoreFieldImm(asm, R9, OsLayout.FrameDirtyField, 0); // now clean
        asm.Label("ff_next");
        asm.Inc(R(ESI));
        asm.Jmp("ff_scan");
        asm.Label("ff_done");
        asm.Ret();
    }

    // ===== EmitZeroSwapSlots =================================================
    // zero_swap_slots: writes a zero page into every swap slot belonging to the current
    // process, so a slot reused by a later process (or this process after exec) never
    // serves stale data. DWRITEs the always-zero OS scratch page into each slot. CALL/RET
    // subroutine (needs the privileged stack); preserves EBX.
    private static void EmitZeroSwapSlots(Assembler asm)
    {
        asm.Label("zero_swap_slots");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));                      // R8 = my index
        asm.MovImm(R(EAX), OsLayout.SwapSlotsPerProcess);
        asm.Mul(R(R8), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.SwapBase);
        asm.Add(R(R8), R(EAX));                       // R8 = my swap-slot base
        asm.MovImm16(R(R10), OsLayout.PageSize);      // R10 = length
        asm.MovImm16(R(R11), OsLayout.ZeroPageBase);  // R11 = zero source page
        asm.MovImm(R(ESI), 0);                        // ESI = page index
        asm.Label("zs_scan");
        asm.MovImm(R(EAX), OsLayout.MaxPagesPerProcess);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("zs_done");
        asm.Mov(R(EDX), R(R8));
        asm.Add(R(EDX), R(ESI));                       // EDX = slot
        asm.DWrite(R(EDX), R(R11), R(R10));           // slot <- zero page
        asm.Inc(R(ESI));
        asm.Jmp("zs_scan");
        asm.Label("zs_done");
        asm.Ret();
    }

    // ===== EmitSwapCopy (helper) =============================================
    // Copies the contents of swap slot `srcSlotReg` to swap slot `dstSlotReg` via the OS
    // transfer page (DREAD src -> scratch, DWRITE scratch -> dst). Clobbers EAX and
    // `lenScratch`/`addrScratch`; preserves the two slot registers.
    private static void EmitSwapCopy(Assembler asm, byte srcSlotReg, byte dstSlotReg, byte lenScratch, byte addrScratch)
    {
        asm.MovImm16(R(addrScratch), OsLayout.SwapScratchBase);
        asm.DRead(R(addrScratch), R(srcSlotReg), R(lenScratch));   // scratch <- src slot
        asm.MovImm16(R(lenScratch), OsLayout.PageSize);
        asm.DWrite(R(dstSlotReg), R(addrScratch), R(lenScratch));  // dst slot <- scratch
    }

    // ===== EmitPairResolve ===================================================
    // pair_resolve: resolve the current process's copy-on-write DATA page R12 against its
    // partner Y. Both ends end up with a private copy of the shared snapshot: the current
    // process becomes private+writable (resident frame de-COW'd, or a private swap PTE), and
    // the partner's page is invalidated (its frame freed, PTE pointed at its own slot) so it
    // re-faults from its now-private slot. Leaf CALL/RET; takes page in R12, preserves R12/R15.
    private static void EmitPairResolve(Assembler asm)
    {
        int pageShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageSize);
        int tableStrideShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableBytesPerProcess);
        int pteShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableEntryBytes);

        asm.Label("pair_resolve");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));                                   // R8 = X (current)
        asm.Mov(R(EAX), R(R8));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(EAX), R(EBX));
        asm.MovImm16(R(EBX), OsLayout.CowPartnerBase);
        asm.Add(R(EAX), R(EBX));
        asm.Load(R(R9), R(EAX));                                   // R9 = Y (partner)
        // Xslot = SwapBase + X*SwapSlotsPerProcess + p ; Yslot likewise.
        asm.Mov(R(R10), R(R8));
        asm.MovImm(R(EAX), OsLayout.SwapSlotsPerProcess);
        asm.Mul(R(R10), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.SwapBase);
        asm.Add(R(R10), R(EAX));
        asm.Add(R(R10), R(R12));                                   // R10 = Xslot
        asm.Mov(R(R11), R(R9));
        asm.MovImm(R(EAX), OsLayout.SwapSlotsPerProcess);
        asm.Mul(R(R11), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.SwapBase);
        asm.Add(R(R11), R(EAX));
        asm.Add(R(R11), R(R12));                                   // R11 = Yslot
        // Read X's PTE to find the shared slot S and whether X is resident (EDI = X frame, or -1).
        EmitPteAddress(asm, R8, R12, ESI, EBX, tableStrideShift, pteShift); // ESI = &PTE[X][p]
        asm.Load(R(EDX), R(ESI));                                  // EDX = X pte
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDX), R(EAX));
        asm.Js("pr_xnonres");
        // X resident COW: frame = (pte - pool) >> pageShift ; S = frame.Swap
        asm.Mov(R(EDI), R(EDX));
        asm.MovImm16(R(EAX), OsLayout.FramePoolBase);
        asm.Sub(R(EDI), R(EAX));
        asm.MovImm(R(EAX), pageShift);
        asm.Shr(R(EDI), R(EAX));                                   // EDI = X frame index
        FrameEntryAddress(asm, EDI, EBX);
        LoadField(asm, EBX, OsLayout.FrameSwapField, R13);         // R13 = S
        asm.Jmp("pr_haves");
        asm.Label("pr_xnonres");
        asm.MovImm(R(EAX), 0);
        asm.Sub(R(EAX), R(EDX));
        asm.MovImm16(R(EBX), OsLayout.SwapCowBias);
        asm.Sub(R(EAX), R(EBX));                                   // EAX = -pte - bias = S
        asm.Mov(R(R13), R(EAX));                                   // R13 = S
        asm.MovImm(R(EDI), 0);
        asm.Dec(R(EDI));                                           // EDI = -1 (X not resident)
        asm.Label("pr_haves");
        // Materialise: copy S into Xslot and Yslot where they differ from S.
        asm.Cmp(R(R10), R(R13));
        asm.Jz("pr_skipx");
        EmitSwapCopy(asm, R13, R10, EAX, EBX);
        asm.Label("pr_skipx");
        asm.Cmp(R(R11), R(R13));
        asm.Jz("pr_skipy");
        EmitSwapCopy(asm, R13, R11, EAX, EBX);
        asm.Label("pr_skipy");
        // Finalise X: resident -> de-COW its frame + point backing at Xslot; else private PTE.
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDI), R(EAX));
        asm.Js("pr_xfin_nonres");
        FrameEntryAddress(asm, EDI, EBX);
        StoreFieldImm(asm, EBX, OsLayout.FrameCowField, 0);
        StoreFieldReg(asm, EBX, OsLayout.FrameSwapField, R10);
        asm.Jmp("pr_xfin_done");
        asm.Label("pr_xfin_nonres");
        EmitPteAddress(asm, R8, R12, ESI, EBX, tableStrideShift, pteShift);
        asm.Mov(R(EAX), R(R10));
        asm.MovImm(R(EBX), OsLayout.SwapPteBias);
        asm.Add(R(EAX), R(EBX));
        asm.MovImm(R(EBX), 0);
        asm.Sub(R(EBX), R(EAX));
        asm.Store(R(ESI), R(EBX));                                 // PTE[X][p] = SwapPte(Xslot)
        asm.Label("pr_xfin_done");
        // Finalise Y: free Y's resident frame for page p (if any), then point PTE[Y][p] at Yslot.
        asm.MovImm(R(ESI), 0);
        asm.Label("pr_yscan");
        asm.MovImm(R(EAX), OsLayout.FrameCount);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("pr_ydone");
        FrameEntryAddress(asm, ESI, EBX);
        LoadField(asm, EBX, OsLayout.FrameOccupiedField, EAX);
        asm.MovImm(R(EBP), 0);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jz("pr_ynext");
        LoadField(asm, EBX, OsLayout.FrameOwnerProcField, EAX);
        asm.Cmp(R(EAX), R(R9));
        asm.Jnz("pr_ynext");
        LoadField(asm, EBX, OsLayout.FrameOwnerPageField, EAX);
        asm.Cmp(R(EAX), R(R12));
        asm.Jnz("pr_ynext");
        StoreFieldImm(asm, EBX, OsLayout.FrameOccupiedField, 0);   // free Y's resident copy
        asm.Label("pr_ynext");
        asm.Inc(R(ESI));
        asm.Jmp("pr_yscan");
        asm.Label("pr_ydone");
        EmitPteAddress(asm, R9, R12, ESI, EBX, tableStrideShift, pteShift);
        asm.Mov(R(EAX), R(R11));
        asm.MovImm(R(EBX), OsLayout.SwapPteBias);
        asm.Add(R(EAX), R(EBX));
        asm.MovImm(R(EBX), 0);
        asm.Sub(R(EBX), R(EAX));
        asm.Store(R(ESI), R(EBX));                                 // PTE[Y][p] = SwapPte(Yslot)
        asm.Ret();
    }

    // ===== EmitResolveCow ====================================================
    // resolve_cow: materialise private copies of all of the current process's COW data pages
    // (against its partner), then clear the partnership both ways. Called before fork/exit/
    // exec so a teardown or re-fork never leaves a dangling COW share. CALL/RET; loops pages
    // in R15 (preserved across pair_resolve) and calls pair_resolve for each COW page.
    private static void EmitResolveCow(Assembler asm)
    {
        int pageShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageSize);
        int tableStrideShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableBytesPerProcess);
        int pteShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableEntryBytes);

        asm.Label("resolve_cow");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));                                   // R8 = X
        asm.Mov(R(EAX), R(R8));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(EAX), R(EBX));
        asm.MovImm16(R(EBX), OsLayout.CowPartnerBase);
        asm.Add(R(EAX), R(EBX));
        asm.Load(R(EAX), R(EAX));                                  // EAX = cowPartner[X]
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("rc_done");                                         // no partner: nothing to resolve
        asm.MovImm(R(R15), 0);                                     // R15 = page index
        asm.Label("rc_scan");
        asm.MovImm(R(EAX), OsLayout.MaxPagesPerProcess);
        asm.Cmp(R(R15), R(EAX));
        asm.Jns("rc_clear");
        EmitPteAddress(asm, R8, R15, EDI, EBX, tableStrideShift, pteShift);
        asm.Load(R(EDX), R(EDI));                                  // EDX = PTE[X][p]
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDX), R(EAX));
        asm.Js("rc_nonres");
        // resident: COW only if its frame's COW flag is set
        asm.Mov(R(ESI), R(EDX));
        asm.MovImm16(R(EAX), OsLayout.FramePoolBase);
        asm.Sub(R(ESI), R(EAX));
        asm.MovImm(R(EAX), pageShift);
        asm.Shr(R(ESI), R(EAX));
        FrameEntryAddress(asm, ESI, EBX);
        LoadField(asm, EBX, OsLayout.FrameCowField, EAX);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jz("rc_next");                                         // resident but not COW
        asm.Jmp("rc_resolve");
        asm.Label("rc_nonres");
        // non-resident COW iff pte <= -SwapCowBias
        asm.MovImm16(R(EAX), OsLayout.SwapCowBias);
        asm.MovImm(R(EBX), 0);
        asm.Sub(R(EBX), R(EAX));                                   // EBX = -SwapCowBias
        asm.Cmp(R(EDX), R(EBX));
        asm.Jns("rc_next");                                        // pte >= -bias: not COW
        asm.Label("rc_resolve");
        asm.Mov(R(R12), R(R15));
        asm.Call("pair_resolve");
        asm.Label("rc_next");
        asm.Inc(R(R15));
        asm.Jmp("rc_scan");
        asm.Label("rc_clear");
        // Clear both ends of the partnership (all shared pages are now private).
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));
        asm.Mov(R(EAX), R(R8));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(EAX), R(EBX));
        asm.MovImm16(R(EBX), OsLayout.CowPartnerBase);
        asm.Add(R(EAX), R(EBX));                                   // EAX = &cowPartner[X]
        asm.Load(R(R9), R(EAX));                                   // R9 = Y
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));
        asm.Store(R(EAX), R(EBX));                                 // cowPartner[X] = -1
        asm.Mov(R(EAX), R(R9));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(EAX), R(EBX));
        asm.MovImm16(R(EBX), OsLayout.CowPartnerBase);
        asm.Add(R(EAX), R(EBX));                                   // EAX = &cowPartner[Y]
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));
        asm.Store(R(EAX), R(EBX));                                 // cowPartner[Y] = -1
        asm.Label("rc_done");
        asm.Ret();
    }

    // ===== EmitCowShare ======================================================
    // cow_share: convert the current process's (the forking parent's) DATA pages to copy-on-
    // write so a freshly forked child can share their snapshot read-only. A resident data
    // frame is marked COW (read-only; the parent's next write traps); a non-resident private
    // swap page is re-encoded from SwapPte to CowPte (same slot, now shared). Code/stack
    // (RAM-home) pages are untouched. Run after flush_frames so the slots are current.
    // CALL/RET leaf; uses R8 = index, ESI = page loop.
    private static void EmitCowShare(Assembler asm)
    {
        int pageShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageSize);
        int tableStrideShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableBytesPerProcess);
        int pteShift = System.Numerics.BitOperations.Log2((uint)OsLayout.PageTableEntryBytes);

        asm.Label("cow_share");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(R8), R(EAX));                                   // R8 = index
        asm.MovImm(R(ESI), 0);                                     // ESI = page
        asm.Label("cs_scan");
        asm.MovImm(R(EAX), OsLayout.MaxPagesPerProcess);
        asm.Cmp(R(ESI), R(EAX));
        asm.Jns("cs_done");
        EmitPteAddress(asm, R8, ESI, EDI, EBX, tableStrideShift, pteShift); // EDI = &PTE
        asm.Load(R(EDX), R(EDI));                                  // EDX = pte
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDX), R(EAX));
        asm.Js("cs_nonres");
        // resident: mark COW iff it is a data frame (frame.Swap >= 0)
        asm.Mov(R(R9), R(EDX));
        asm.MovImm16(R(EAX), OsLayout.FramePoolBase);
        asm.Sub(R(R9), R(EAX));
        asm.MovImm(R(EAX), pageShift);
        asm.Shr(R(R9), R(EAX));                                    // R9 = frame index
        FrameEntryAddress(asm, R9, EBX);
        LoadField(asm, EBX, OsLayout.FrameSwapField, EAX);         // EAX = frame.Swap
        asm.MovImm(R(R10), 0);
        asm.Cmp(R(EAX), R(R10));
        asm.Js("cs_next");                                         // Swap < 0: RAM-home, skip
        FrameEntryAddress(asm, R9, EBX);
        StoreFieldImm(asm, EBX, OsLayout.FrameCowField, 1);        // mark resident data frame COW
        asm.Jmp("cs_next");
        asm.Label("cs_nonres");
        // already COW? pte <= -SwapCowBias -> skip
        asm.MovImm(R(EAX), 0);
        asm.Sub(R(EAX), R(EDX));                                   // EAX = -pte
        asm.MovImm16(R(EBX), OsLayout.SwapCowBias);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jns("cs_next");                                        // -pte >= bias: already COW
        // RAM-home (-2) / unmapped (-1)? pte >= -2 -> skip
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));
        asm.Dec(R(EBX));                                           // EBX = -2
        asm.Cmp(R(EDX), R(EBX));
        asm.Jns("cs_next");                                        // pte >= -2: not a swap data page
        // private swap data page: slot = -pte - SwapPteBias; PTE = -(slot + SwapCowBias)
        asm.MovImm(R(EAX), 0);
        asm.Sub(R(EAX), R(EDX));
        asm.MovImm(R(EBX), OsLayout.SwapPteBias);
        asm.Sub(R(EAX), R(EBX));                                   // EAX = slot
        asm.MovImm16(R(EBX), OsLayout.SwapCowBias);
        asm.Add(R(EAX), R(EBX));                                   // EAX = slot + SwapCowBias
        asm.MovImm(R(EBX), 0);
        asm.Sub(R(EBX), R(EAX));                                   // EBX = -(slot + SwapCowBias) = CowPte
        EmitPteAddress(asm, R8, ESI, EDI, EAX, tableStrideShift, pteShift);
        asm.Store(R(EDI), R(EBX));
        asm.Label("cs_next");
        asm.Inc(R(ESI));
        asm.Jmp("cs_scan");
        asm.Label("cs_done");
        asm.Ret();
    }

    // ===== FrameEntryAddress (helper) ========================================
    // Computes the absolute address of frame `frameReg`'s core-map entry into `dest`
    // (= FrameTableBase + frame * FrameTableEntryBytes). Clobbers EAX and `dest`.
    private static void FrameEntryAddress(Assembler asm, byte frameReg, byte dest)
    {
        asm.Mov(R(dest), R(frameReg));
        asm.MovImm(R(EAX), OsLayout.FrameTableEntryBytes);
        asm.Mul(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.FrameTableBase);
        asm.Add(R(dest), R(EAX));
    }

    // ===== FrameBaseAddress (helper) =========================================
    // Computes the absolute physical base of frame `frameReg` into `dest`
    // (= FramePoolBase + frame * PageSize). Clobbers EAX and `dest`.
    private static void FrameBaseAddress(Assembler asm, byte frameReg, byte dest, int pageShift)
    {
        asm.Mov(R(dest), R(frameReg));
        asm.MovImm(R(EAX), pageShift);
        asm.Shl(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.FramePoolBase);
        asm.Add(R(dest), R(EAX));
    }

    // ===== EmitPteAddress (helper) ===========================================
    // Computes &PTE[procReg][pageReg] into `dest` (= PageTableBase + proc*stride + page*4).
    // Clobbers EAX, `dest`, and `scratch`.
    private static void EmitPteAddress(Assembler asm, byte procReg, byte pageReg, byte dest, byte scratch, int strideShift, int pteShift)
    {
        asm.Mov(R(dest), R(procReg));
        asm.MovImm(R(EAX), strideShift);
        asm.Shl(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.PageTableBase);
        asm.Add(R(dest), R(EAX));
        asm.Mov(R(scratch), R(pageReg));
        asm.MovImm(R(EAX), pteShift);
        asm.Shl(R(scratch), R(EAX));
        asm.Add(R(dest), R(scratch));
    }

    // ===== EmitPageCopy (helper) =============================================
    // Emits a word-by-word copy of PageSize bytes from [srcReg] to [dstReg]. `valReg` holds
    // each word in flight. Clobbers EAX, EDI, and `valReg`; preserves srcReg/dstReg. Label
    // names are derived from `prefix`, so each call site must pass a unique prefix.
    private static void EmitPageCopy(Assembler asm, byte srcReg, byte dstReg, string prefix, byte valReg)
    {
        asm.MovImm(R(EDI), 0);                        // EDI = byte offset
        asm.Label(prefix + "_c");
        asm.MovImm16(R(EAX), OsLayout.PageSize);
        asm.Cmp(R(EDI), R(EAX));
        asm.Jns(prefix + "_d");                       // offset >= PageSize: done
        asm.Mov(R(EAX), R(srcReg));
        asm.Add(R(EAX), R(EDI));
        asm.Load(R(valReg), R(EAX));                  // valReg = *(src + offset)
        asm.Mov(R(EAX), R(dstReg));
        asm.Add(R(EAX), R(EDI));
        asm.Store(R(EAX), R(valReg));                 // *(dst + offset) = valReg
        asm.MovImm(R(EAX), 4);
        asm.Add(R(EDI), R(EAX));                       // offset += 4
        asm.Jmp(prefix + "_c");
        asm.Label(prefix + "_d");
    }

}
