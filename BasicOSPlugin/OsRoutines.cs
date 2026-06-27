namespace CSharpOS;

/// <summary>
/// Emits the OS routines as ISA code that runs in the OS memory region. Each
/// routine is entered through the IVT in Privileged mode (program base 0, so all
/// addresses are absolute) and returns to a process with OSRET. Built and tested
/// in isolation; BasicOS adopts them once the full set is proven.
///
/// Register convention across a routine: ECX = current index, EDI = process count,
/// ESI = scan/loop counter, EDX = a carried argument (wait reason) or the chosen
/// candidate index; EAX, EBX, EBP are scratch (EBX usually holds an entry address).
/// R8-R15 are used by ContextSwitch, resume_mlfq, and the buddy allocator.
///
/// Buddy allocator bitmap: 1 bit per tree node (bit=1 FREE, bit=0 used/split),
/// stored as 8 × 32-bit words at BuddyBitmapOffset. Node i (1-indexed) → bit i-1
/// → word (i-1)/32, bit-in-word (i-1)%32. Bit ops (AND/OR/XOR/NOT/SHL/SHR) are
/// used to pack/unpack bits. For heaps with ≤32 nodes (4-level, common case) every
/// tree operation touches only word 0.
/// </summary>
public static class OsRoutines
{
    private const byte EAX = (byte)RegisterName.EAX;
    private const byte EBX = (byte)RegisterName.EBX;
    private const byte ECX = (byte)RegisterName.ECX;
    private const byte EDX = (byte)RegisterName.EDX;
    private const byte ESI = (byte)RegisterName.ESI;
    private const byte EDI = (byte)RegisterName.EDI;
    private const byte ESP = (byte)RegisterName.ESP;
    private const byte EBP = (byte)RegisterName.EBP;
    private const byte R8  = (byte)RegisterName.R8;
    private const byte R9  = (byte)RegisterName.R9;
    private const byte R10 = (byte)RegisterName.R10;
    private const byte R11 = (byte)RegisterName.R11;
    private const byte R12 = (byte)RegisterName.R12;
    private const byte R13 = (byte)RegisterName.R13;
    private const byte R14 = (byte)RegisterName.R14;
    private const byte R15 = (byte)RegisterName.R15;

    private const int Ready      = (int)ProcessState.Ready;
    private const int Blocked    = (int)ProcessState.Blocked;
    private const int Terminated = (int)ProcessState.Terminated;
    private const int Zombie     = (int)ProcessState.Zombie;
    private const int WaitNone   = (int)WaitReason.None;
    private const int WaitChild  = (int)WaitReason.ChildProcess;
    private const int User       = (int)PrivilegeLevel.User;
    private const int EntrySize  = Hardware.ProcessEntrySize;

    // Byte offset of the EAX / EIP / ESP slots within an entry's saved register file
    // (the register file mirrors the live registers: slot = register index * 4).
    private const int EaxSlot    = (int)RegisterName.EAX * 4;
    private const int EipSlot    = (int)RegisterName.EIP * 4;
    private const int EspSlot    = (int)RegisterName.ESP * 4;

    public static byte[] BuildOsImage()
    {
        Assembler asm = new Assembler();

        int contextSwitch = OsLayout.CodeBase + asm.CodeLength; EmitContextSwitch(asm);
        int schedule      = OsLayout.CodeBase + asm.CodeLength; EmitSchedule(asm);
        int block         = OsLayout.CodeBase + asm.CodeLength; EmitBlock(asm);
        int wakeInput     = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, (int)WaitReason.Input);
        int wakeOutput    = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, (int)WaitReason.Output);
        EmitWakeBody(asm);
        int halt          = OsLayout.CodeBase + asm.CodeLength; EmitHalt(asm);
        int invalid       = OsLayout.CodeBase + asm.CodeLength; EmitInvalidInstruction(asm);
        int allocate      = OsLayout.CodeBase + asm.CodeLength; EmitBuddyAlloc(asm);
        int diskLoad      = OsLayout.CodeBase + asm.CodeLength; EmitDiskLoad(asm);
        int spawn         = OsLayout.CodeBase + asm.CodeLength; EmitSpawn(asm);
        int fork          = OsLayout.CodeBase + asm.CodeLength; EmitFork(asm);
        int exec          = OsLayout.CodeBase + asm.CodeLength; EmitExec(asm);
        int wait          = OsLayout.CodeBase + asm.CodeLength; EmitWait(asm);
        int syscall       = OsLayout.CodeBase + asm.CodeLength; EmitSyscall(asm);
        int pageFault     = OsLayout.CodeBase + asm.CodeLength; EmitPageFault(asm);
        EmitExitBody(asm);      // shared label "exit_body" (HLT/EXIT/fault tail)
        EmitAllocSub(asm);      // shared subroutine "alloc_sub"; ends with Ret
        EmitBuddyFree(asm);     // label "buddy_free_entry"; ends with Jmp("resume_mlfq")
        EmitReleaseFrames(asm); // shared subroutine "release_frames"; ends with Ret
        EmitFlushFrames(asm);   // shared subroutine "flush_frames"; ends with Ret
        EmitResumeMlfq(asm);    // label "resume_mlfq"

        byte[] code = asm.Build(OsLayout.CodeBase);

        if (OsLayout.CodeBase + code.Length > OsLayout.DataBase)
        {
            throw new InvalidOperationException(
                $"OS code ({code.Length} bytes) overruns the data section at offset {OsLayout.DataBase}; raise OsLayout.DataBase.");
        }

        byte[] image = new byte[OsLayout.TotalSize];
        Array.Copy(code, 0, image, OsLayout.CodeBase, code.Length);
        WriteWord(image, Hardware.IvtContextSwitch * 4,      contextSwitch);
        WriteWord(image, Hardware.IvtSchedule * 4,           schedule);
        WriteWord(image, Hardware.IvtBlockInput * 4,         block);
        WriteWord(image, Hardware.IvtBlockOutput * 4,        block);
        WriteWord(image, Hardware.IvtWakeInput * 4,          wakeInput);
        WriteWord(image, Hardware.IvtWakeOutput * 4,         wakeOutput);
        WriteWord(image, Hardware.IvtHalt * 4,               halt);
        WriteWord(image, Hardware.IvtInvalidInstruction * 4, invalid);
        WriteWord(image, Hardware.IvtAllocate * 4,           allocate);
        WriteWord(image, Hardware.IvtDiskLoad * 4,           diskLoad);
        WriteWord(image, Hardware.IvtSpawn * 4,              spawn);
        WriteWord(image, Hardware.IvtFork * 4,               fork);
        WriteWord(image, Hardware.IvtExec * 4,               exec);
        WriteWord(image, Hardware.IvtWait * 4,               wait);
        WriteWord(image, Hardware.IvtSyscall * 4,            syscall);
        WriteWord(image, Hardware.IvtPageFault * 4,          pageFault);
        return image;
    }

    // ---- scheduling routines (unchanged) ------------------------------------

    private static void EmitContextSwitch(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("cs_skip");

        EntryAddress(asm, ECX, EBX);
        asm.SaveRegs(R(EBX));

        LoadField(asm, EBX, Hardware.ProcessEntryTicksUsed, R8);
        asm.Inc(R(R8));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryTicksUsed, R8);

        LoadField(asm, EBX, Hardware.ProcessEntryPriority, R9);
        asm.MovImm(R(R10), OsLayout.QueueCount - 1);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("cs_no_demote");

        asm.Mov(R(R10), R(R9));
        asm.MovImm(R(R11), 4);
        asm.Mul(R(R10), R(R11));
        asm.MovImm16(R(R11), OsLayout.QuantumTableOffset);
        asm.Add(R(R10), R(R11));
        asm.Load(R(R10), R(R10));

        asm.Cmp(R(R8), R(R10));
        asm.Js("cs_no_demote");

        asm.Inc(R(R9));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryPriority, R9);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_no_demote");

        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.Load(R(R10), R(R8));
        asm.Dec(R(R10));
        asm.Store(R(R8), R(R10));

        asm.MovImm(R(R11), 0);
        asm.Cmp(R(R10), R(R11));
        asm.Jnz("cs_boost_skip");

        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);

        asm.Label("cs_boost_loop");
        asm.Mov(R(R11), R(EDI));
        asm.Cmp(R(R11), R(ESI));
        asm.Jz("cs_boost_done");
        asm.Js("cs_boost_done");

        asm.Mov(R(R12), R(ESI));
        asm.MovImm(R(R13), EntrySize);
        asm.Mul(R(R12), R(R13));
        asm.MovImm16(R(R13), OsLayout.ProcessTableOffset);
        asm.Add(R(R12), R(R13));

        LoadField(asm, R12, Hardware.ProcessEntryState, R13);
        asm.MovImm(R(R14), Terminated);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("cs_boost_skip_entry");

        StoreFieldImm(asm, R12, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R12, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_boost_skip_entry");
        asm.Inc(R(ESI));
        asm.Jmp("cs_boost_loop");

        asm.Label("cs_boost_done");
        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.MovImm(R(R10), OsLayout.BoostInterval);
        asm.Store(R(R8), R(R10));

        asm.Label("cs_boost_skip");
        asm.Label("cs_skip");
        asm.Jmp("resume_mlfq");
    }

    private static void EmitSchedule(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    private static void EmitBlock(Assembler asm)
    {
        asm.Mov(R(EDX), R(EAX));
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Blocked);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryWaitReason, EDX);
        asm.SaveRegs(R(EBX));
        asm.Jmp("resume_mlfq");
    }

    // Halt (HLT / EXIT) and InvalidInstruction both terminate the running process via the
    // shared exit_body: free its memory, then either hand its exit status to a parent
    // waiting in wait(), keep it as a Zombie until the parent waits, or reap it outright
    // (orphan). HLT/EXIT carry an exit status in EAX; a fault uses status -1.
    private static void EmitHalt(Assembler asm)
    {
        asm.Mov(R(R8), R(EAX));                    // R8 = exit status (dispatch arg)
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryExitStatus, R8);
        asm.Jmp("exit_body");
    }

    private static void EmitInvalidInstruction(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryExitStatus); // fault status = -1
        asm.Jmp("exit_body");
    }

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
    // Persistent registers across the handler: R12 = faulting page, R13 = current index,
    // R14 = the faulting page's block home, R15 = the chosen frame index.
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

        // home = entry.ProgramAddress + faultingPage * PageSize  (the page's block home)
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R14);
        asm.Mov(R(R8), R(R12));
        asm.MovImm(R(EAX), pageShift);
        asm.Shl(R(R8), R(EAX));
        asm.Add(R(R14), R(R8));                      // R14 = home

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

        // --- evict the victim frame R15 (it is occupied) ---
        asm.Label("pf_evict");
        FrameEntryAddress(asm, R15, R9);             // R9 = &frame[victim]
        LoadField(asm, R9, OsLayout.FrameDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("pf_evict_unmap");                    // clean: drop without write-back
        // dirty: write the frame back to its home
        FrameBaseAddress(asm, R15, R8, pageShift);   // R8 = frame physical base (src)
        LoadField(asm, R9, OsLayout.FrameHomeField, R11); // R11 = home (dst)
        EmitPageCopy(asm, R8, R11, "pf_wb", R10);    // copy PageSize bytes (word value in R10)

        asm.Label("pf_evict_unmap");
        // flip the evicted page's owner PTE back to non-resident
        FrameEntryAddress(asm, R15, R9);             // recompute &frame[victim]
        LoadField(asm, R9, OsLayout.FrameOwnerProcField, R8);  // R8 = oldProc
        LoadField(asm, R9, OsLayout.FrameOwnerPageField, R10); // R10 = oldPage
        EmitPteAddress(asm, R8, R10, R11, EDX, tableStrideShift, pteShift); // R11 = &PTE[oldProc][oldPage]
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Dec(R(EAX));                              // EAX = NonResidentPage (-2)
        asm.Store(R(R11), R(EAX));

        // --- fill: copy the faulting page from its home into frame R15 ---
        asm.Label("pf_fill");
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame physical base (dst)
        asm.Mov(R(R8), R(R14));                       // R8 = home (src)
        EmitPageCopy(asm, R8, R11, "pf_fl", R10);    // copy home -> frame

        // --- record the new occupant in the core map ---
        FrameEntryAddress(asm, R15, R9);             // R9 = &frame[R15]
        StoreFieldImm(asm, R9, OsLayout.FrameOccupiedField, 1);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerProcField, R13);
        StoreFieldReg(asm, R9, OsLayout.FrameOwnerPageField, R12);
        StoreFieldReg(asm, R9, OsLayout.FrameHomeField, R14);
        StoreFieldImm(asm, R9, OsLayout.FrameDirtyField, 0);
        StoreFieldImm(asm, R9, OsLayout.FrameLastUseField, 0); // the MMU stamps it on the retry

        // --- map the faulting page resident -> the frame base ---
        EmitPteAddress(asm, R13, R12, R10, EDX, tableStrideShift, pteShift); // R10 = &PTE[cur][page]
        FrameBaseAddress(asm, R15, R11, pageShift);  // R11 = frame base
        asm.Store(R(R10), R(R11));                    // PTE := frame base (now resident)

        asm.Mov(R(ECX), R(R13));                      // resume_mlfq anchor = current index
        asm.Jmp("resume_mlfq");
    }

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
        FrameBaseAddress(asm, ESI, R8, pageShift);    // R8 = frame base (src)
        LoadField(asm, R9, OsLayout.FrameHomeField, R11); // R11 = home (dst)
        EmitPageCopy(asm, R8, R11, "ff_wb", R10);     // copy frame -> home
        FrameEntryAddress(asm, ESI, R9);              // recompute (copy clobbered scratch)
        StoreFieldImm(asm, R9, OsLayout.FrameDirtyField, 0); // now clean
        asm.Label("ff_next");
        asm.Inc(R(ESI));
        asm.Jmp("ff_scan");
        asm.Label("ff_done");
        asm.Ret();
    }

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

    // exit_body: tear down the running process (entry in EBX, ExitStatus already set).
    // Frees its memory, then resolves who collects its status:
    //   - a parent currently blocked in wait() on this PID -> deliver status, wake it,
    //     and reap this entry;
    //   - else if the parent is still alive -> keep this entry as a Zombie;
    //   - else (no/dead parent) -> reap this entry now.
    private static void EmitExitBody(Assembler asm)
    {
        asm.Label("exit_body");
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated); // hide from scans
        SetupPrivilegedStack(asm);
        asm.Call("free_sub");
        // Reclaim this process's physical frames (no write-back — its memory is gone), so
        // they cannot later be evicted into freed/reused RAM. EBX (entry) is preserved.
        asm.Call("release_frames");

        // Scan for a parent blocked in wait() on this PID.
        LoadField(asm, EBX, Hardware.ProcessEntryPid, R8);   // R8 = my Pid
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);

        asm.Label("xb_scan");
        asm.Mov(R(R9), R(ESI));
        asm.Cmp(R(R9), R(EDI));
        asm.Jns("xb_no_waiter");
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Blocked);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("xb_next");
        LoadField(asm, R10, Hardware.ProcessEntryWaitReason, R11);
        asm.MovImm(R(R12), WaitChild);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("xb_next");
        LoadField(asm, R10, Hardware.ProcessEntryWaitTarget, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("xb_next");
        // Found the waiting parent: deliver status (into its EAX), wake it, reap myself.
        LoadField(asm, EBX, Hardware.ProcessEntryExitStatus, R11);
        StoreFieldReg(asm, R10, EaxSlot, R11);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryTicksUsed, 0);
        StoreFieldMinusOne(asm, R10, Hardware.ProcessEntryWaitTarget);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        asm.Jmp("xb_done");

        asm.Label("xb_next");
        asm.Inc(R(ESI));
        asm.Jmp("xb_scan");

        asm.Label("xb_no_waiter");
        // No waiter: Zombie if the parent is still alive, else reap (orphan).
        LoadField(asm, EBX, Hardware.ProcessEntryParentPid, R8); // R8 = ParentPid
        asm.MovImm(R(EAX), 1);
        asm.Cmp(R(R8), R(EAX));
        asm.Js("xb_reap");                                       // ParentPid < 1: no parent

        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);
        asm.Label("xb_pscan");
        asm.Mov(R(R9), R(ESI));
        asm.Cmp(R(R9), R(EDI));
        asm.Jns("xb_reap");                                      // parent not found: orphan
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Terminated);
        asm.Cmp(R(R11), R(R12));
        asm.Jz("xb_pnext");
        asm.MovImm(R(R12), Zombie);
        asm.Cmp(R(R11), R(R12));
        asm.Jz("xb_pnext");
        LoadField(asm, R10, Hardware.ProcessEntryPid, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jz("xb_zombie");                                     // a live parent exists
        asm.Label("xb_pnext");
        asm.Inc(R(ESI));
        asm.Jmp("xb_pscan");

        asm.Label("xb_zombie");
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Zombie);
        asm.Jmp("xb_done");

        asm.Label("xb_reap");
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);

        asm.Label("xb_done");
        // Reap any of my own zombie children: now that I am gone, no one will wait()
        // for them, so they would otherwise leak. (A child still running becomes an
        // orphan and is reaped when it later exits.)
        LoadField(asm, EBX, Hardware.ProcessEntryPid, R8);   // R8 = my Pid
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);
        asm.Label("xb_orphan_scan");
        asm.Mov(R(R9), R(ESI));
        asm.Cmp(R(R9), R(EDI));
        asm.Jns("xb_orphan_done");
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Zombie);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("xb_orphan_next");
        LoadField(asm, R10, Hardware.ProcessEntryParentPid, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("xb_orphan_next");
        StoreFieldImm(asm, R10, Hardware.ProcessEntryState, Terminated);
        asm.Label("xb_orphan_next");
        asm.Inc(R(ESI));
        asm.Jmp("xb_orphan_scan");
        asm.Label("xb_orphan_done");

        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    // IvtWait: block the caller until child PID (in EAX) terminates. If that child is
    // already a zombie, reap it and return its status immediately; otherwise mark the
    // caller Blocked on ChildProcess and switch away — exit_body wakes it later.
    //   EBX = caller entry, R8 = target child PID
    private static void EmitWait(Assembler asm)
    {
        asm.Mov(R(R8), R(EAX));                    // R8 = target child PID
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);               // EBX = caller (parent) entry

        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);

        asm.Label("wt_scan");
        asm.Mov(R(R9), R(ESI));
        asm.Cmp(R(R9), R(EDI));
        asm.Jns("wt_block");
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Zombie);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("wt_next");
        LoadField(asm, R10, Hardware.ProcessEntryPid, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("wt_next");
        // Found the zombie child: reap it and resume the caller with its status in EAX.
        LoadField(asm, R10, Hardware.ProcessEntryExitStatus, R11);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryState, Terminated);
        asm.SaveRegs(R(EBX));
        StoreFieldReg(asm, EBX, EaxSlot, R11);
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));

        asm.Label("wt_next");
        asm.Inc(R(ESI));
        asm.Jmp("wt_scan");

        asm.Label("wt_block");
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Blocked);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitChild);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryWaitTarget, R8);
        asm.SaveRegs(R(EBX));
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    // IvtSyscall: the shared IN/OUT syscall handler. Entered (not dispatched) by
    // EnterKernel, which leaves interrupts enabled (the handler is preemptible) and sets
    // EBP = this process's trap-frame base. The frame, on the process's kernel stack,
    // holds the saved user register file at offset 0 and trap info at KernelTrapInfoOffset.
    // Kernel addresses absolutely (base 0), so frame fields are read at EBP + offset.
    // Uses EBP as the frame pointer; EAX/EBX/ECX/EDX/ESI are scratch. Returns via IRET.
    private static void EmitSyscall(Assembler asm)
    {
        asm.Label("syscall");
        asm.MovImm(R(EAX), Hardware.KernelTrapInfoOffset);
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(EBX), R(EAX));                 // EBX = faulting opcode

        asm.MovImm(R(EAX), Hardware.KernelTrapInfoOffset + 4);
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(ECX), R(EAX));                 // ECX = operand byte-offset within the save area
        asm.Add(R(ECX), R(EBP));                  // ECX = absolute address of the operand's save slot

        asm.MovImm(R(EDX), Instruction.OUT);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_out");
        asm.MovImm(R(EDX), Instruction.IN);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_in");
        asm.Iret();                               // unknown cause — return (should not happen)

        asm.Label("syscall_out");
        asm.Load(R(ESI), R(ECX));                 // ESI = user's operand value (from the save area)
        asm.Out(R(ESI));                          // real device write (kernel level)
        asm.Iret();

        asm.Label("syscall_in");
        asm.In(R(ESI));                           // real device read (kernel level)
        asm.Store(R(ECX), R(ESI));                // write the result back into the save-area slot
        asm.Iret();
    }

    // ---- buddy allocator ---------------------------------------------------

    // BuddyAlloc: allocate memory for the staged process-table entry (address in EAX).
    // Reads entry.TotalSize, walks the buddy tree to find the smallest free block that
    // fits, splits ancestors as needed, records the base address in entry.ProgramAddress.
    // Sets ProgramAddress = -1 when no block fits. Returns via OSRET (no process switch).
    //
    // Registers during execution:
    //   EBX = entry address, ECX = needed (TotalSize)
    //   ESI = targetLevel, EDI = blockSize at targetLevel
    //   EDX = BuddyLevels (max depth), R9 = HeapSize (for level computation)
    //   R8  = searchLevel (outer scan; decrements toward root)
    //   R10 = current scan node (inner loop) or leftChild (split loop)
    //   R11, R12, R13 = scratch within split/merge steps
    //   EAX, EBP, R14, R15 = dedicated scratch for bit operations
    // IvtAllocate: allocate memory for the staged entry (address in EAX). Delegates to
    // the shared alloc_sub subroutine, then returns to a process via OSRET (no switch).
    private static void EmitBuddyAlloc(Assembler asm)
    {
        asm.Mov(R(EBX), R(EAX));                          // EBX = entry
        SetupPrivilegedStack(asm);
        asm.Call("alloc_sub");                            // sets entry.ProgramAddress or -1
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // alloc_sub: the buddy allocator as a CALL/RET subroutine. Expects EBX = entry;
    // finds the smallest free block fitting entry.TotalSize, splits ancestors as
    // needed, and records the base in entry.ProgramAddress (or -1 if none fits).
    // Reused by IvtAllocate, IvtSpawn, fork, and exec.
    private static void EmitAllocSub(Assembler asm)
    {
        asm.Label("alloc_sub");

        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, ECX); // ECX = needed

        // Load heap parameters from OS data.
        Imm16(asm, EAX, OsLayout.BuddyHeapSizeOffset);
        asm.Load(R(R9), R(EAX));                           // R9 = HeapSize
        Imm16(asm, EAX, OsLayout.BuddyLevelsOffset);
        asm.Load(R(EDX), R(EAX));                          // EDX = BuddyLevels

        // Compute target level: smallest level where blockSize >= needed.
        // Start at level 0 (blockSize = HeapSize), halve until blockSize/2 < needed.
        asm.MovImm(R(ESI), 0);                             // ESI = targetLevel = 0
        asm.Mov(R(R10), R(R9));                            // R10 = currentBlockSize = HeapSize

        asm.Label("ba_find_level");
        asm.MovImm(R(R11), 2);
        asm.Mov(R(EBP), R(R10));
        asm.Div(R(EBP), R(R11));                           // EBP = currentBlockSize / 2
        asm.Cmp(R(EBP), R(ECX));
        asm.Js("ba_level_done");                           // blockSize/2 < needed: stop here
        asm.Mov(R(R11), R(EDX));
        asm.Cmp(R(R11), R(ESI));
        asm.Jz("ba_level_done");                           // targetLevel == BuddyLevels: stop
        asm.Js("ba_level_done");                           // targetLevel > BuddyLevels: stop
        asm.Inc(R(ESI));                                   // targetLevel++
        asm.Mov(R(R10), R(EBP));                           // blockSize = blockSize/2
        asm.Jmp("ba_find_level");

        asm.Label("ba_level_done");
        asm.Mov(R(EDI), R(R10));                           // EDI = blockSize at targetLevel (save)

        // Guard: if the smallest available block (blockSize) is still less than needed
        // (happens when needed > heapSize), fail immediately.
        asm.Cmp(R(EDI), R(ECX));
        asm.Js("ba_fail");

        // Scan from targetLevel up toward root for any free node.
        // R8 = searchLevel (starts at targetLevel, decrements toward 0).
        asm.Mov(R(R8), R(ESI));                            // R8 = searchLevel = targetLevel

        asm.Label("ba_scan_outer");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Js("ba_fail");                                 // searchLevel < 0: no memory

        // firstNode = 1 << searchLevel; endNode = 2 * firstNode.
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(R8));                           // EBP = 2^searchLevel = firstNode
        asm.Mov(R(R9), R(EBP));
        asm.Add(R(R9), R(EBP));                            // R9 = endNode = 2 * firstNode

        asm.Mov(R(R10), R(EBP));                           // R10 = currentScanNode = firstNode

        asm.Label("ba_scan_inner");
        asm.Cmp(R(R9), R(R10));
        asm.Jz("ba_scan_next_level");                      // currentNode == endNode: exhausted
        asm.Js("ba_scan_next_level");

        // Check if bit(R10) is set (node is free).
        EmitReadBit(asm, R10);                             // sets ZF if bit=0; clobbers EAX,EBP,R14,R15
        asm.Jz("ba_scan_bit_zero");
        asm.Jmp("ba_found");                               // bit=1: this node is free

        asm.Label("ba_scan_bit_zero");
        asm.Inc(R(R10));
        asm.Jmp("ba_scan_inner");

        asm.Label("ba_scan_next_level");
        asm.Dec(R(R8));                                    // searchLevel--
        asm.Jmp("ba_scan_outer");

        // Found a free node at R10, searchLevel in R8.
        asm.Label("ba_found");
        asm.Cmp(R(R8), R(ESI));
        asm.Jz("ba_exact");                                // foundLevel == targetLevel: just allocate

        // Split from R8 (foundLevel) down to ESI (targetLevel).
        // At each step: clear bit(currentNode), set bit(rightChild), descend left.
        asm.Mov(R(R11), R(R10));                           // R11 = currentSplitNode

        asm.Label("ba_split");
        asm.Cmp(R(R8), R(ESI));
        asm.Jz("ba_split_done");                           // reached targetLevel: R11 is allocated

        EmitClearBit(asm, R11);                            // clear ancestor (now split)

        asm.MovImm(R(EAX), 2);
        asm.Mov(R(R12), R(R11));
        asm.Mul(R(R12), R(EAX));                           // R12 = leftChild = 2*R11
        asm.Mov(R(R13), R(R12));
        asm.Inc(R(R13));                                   // R13 = rightChild = 2*R11+1

        EmitSetBit(asm, R13);                              // right child = free (buddy)

        asm.Mov(R(R11), R(R12));                           // descend into left child
        asm.Inc(R(R8));                                    // level++
        asm.Jmp("ba_split");

        asm.Label("ba_split_done");
        // R11 is at targetLevel; its bit was never set → it is the allocated block.
        asm.Jmp("ba_addr");

        asm.Label("ba_exact");
        // R10 is at targetLevel and is free; clear its bit to allocate it.
        EmitClearBit(asm, R10);
        asm.Mov(R(R11), R(R10));                           // R11 = allocated node

        // Compute physical address: HeapStart + (node - 2^targetLevel) * blockSize.
        asm.Label("ba_addr");
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(ESI));                          // EBP = 2^targetLevel
        asm.Sub(R(R11), R(EBP));                          // R11 = block_j = node - firstNode
        asm.Mul(R(R11), R(EDI));                          // R11 = block_j * blockSize

        Imm16(asm, EAX, OsLayout.BuddyHeapStartOffset);
        asm.Load(R(EBP), R(EAX));                         // EBP = HeapStart
        asm.Add(R(R11), R(EBP));                          // R11 = PhysAddr

        asm.Mov(R(EBP), R(EBX));
        asm.MovImm(R(EAX), Hardware.ProcessEntryProgramAddress);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(R11));
        asm.Jmp("ba_done");

        asm.Label("ba_fail");
        asm.Mov(R(EBP), R(EBX));
        asm.MovImm(R(EAX), Hardware.ProcessEntryProgramAddress);
        asm.Add(R(EBP), R(EAX));
        asm.MovImm(R(R11), 0);
        asm.Dec(R(R11));                                   // R11 = -1
        asm.Store(R(EBP), R(R11));

        asm.Label("ba_done");
        asm.Ret();
    }

    // DiskLoad: copy a process's program image from its disk slot into the RAM the
    // allocator reserved for it. Entered (via IvtDiskLoad) with the process-table
    // entry address in EAX, after a successful IvtAllocate set ProgramAddress. Keeps
    // the disk concern out of the allocator, which stays a pure, reusable primitive.
    //   EBX = entry, R9 = ProgramAddress (dest), R10 = disk slot, R11 = byte count out
    private static void EmitDiskLoad(Assembler asm)
    {
        asm.Mov(R(EBX), R(EAX));                                       // EBX = entry
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9); // R9 = dest RAM address
        LoadField(asm, EBX, Hardware.ProcessEntryDiskSlot, R10);       // R10 = disk slot
        asm.DRead(R(R9), R(R10), R(R11));                             // DREAD ProgramAddress, slot, lenOut
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // IvtSpawn: create a process from scratch (boot creation). Entered with the
    // process-table entry address in EAX; the host pre-seeds ProgramSize,
    // RequiredMemory, RequiredStackSize, TotalSize and DiskSlot. Allocates the region,
    // DREADs the image from disk, and seeds the saved register file, scheduling state,
    // and a fresh PID — all in ISA. The kernel-section image and fd table are seeded by
    // the host after this returns.
    //   EBX = entry, R9 = programAddress, R10 = disk slot, R11 = scratch,
    //   R12 = ESP offset, R13 = NextPid
    private static void EmitSpawn(Assembler asm)
    {
        asm.Mov(R(EBX), R(EAX));                   // EBX = entry
        SetupPrivilegedStack(asm);
        asm.Call("alloc_sub");                     // sets entry.ProgramAddress or -1

        // Bail out (no seeding) if allocation failed.
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9); // R9 = programAddress
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R9), R(EAX));
        asm.Js("sp_done");

        // Copy the program image from disk into the allocated RAM.
        LoadField(asm, EBX, Hardware.ProcessEntryDiskSlot, R10);
        asm.DRead(R(R9), R(R10), R(R11));          // DREAD programAddress, slot, lenOut

        // Saved registers: EIP offset = 0 (program start); ESP offset = top of the user
        // stack = TotalSize - KernelStackSize (the kernel stack sits above it).
        StoreFieldImm(asm, EBX, EipSlot, 0);
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R12);
        asm.MovImm(R(EAX), Hardware.KernelStackSize);
        asm.Sub(R(R12), R(EAX));                   // R12 = ESP offset
        StoreFieldReg(asm, EBX, EspSlot, R12);

        // Scheduling + identity state.
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryLevel, User);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryParentPid);
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryWaitTarget);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryExitStatus, 0);

        // PID = NextPid++.
        Imm16(asm, EAX, OsLayout.NextPidOffset);
        asm.Load(R(R13), R(EAX));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryPid, R13);
        asm.Inc(R(R13));
        Imm16(asm, EAX, OsLayout.NextPidOffset);
        asm.Store(R(EAX), R(R13));

        asm.Label("sp_done");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // IvtFork: duplicate the running (parent) process. Entered via a user FORK trap.
    // Creates a child in a free slot: copies the parent's sizing fields, buddy-allocs
    // the child's region, ISA-memcpys the parent's RAM into it, copies the parent's
    // saved register file (position-independent, so no relocation), assigns a fresh PID
    // and parentage, then delivers the child PID to the parent (EAX) and 0 to the child
    // and re-enters the scheduler. On no free slot / no memory, the parent gets -1.
    //   ECX = parent index, R8 = parent entry, ESI = child index, R9 = child entry,
    //   EBX = child entry (across alloc_sub), EDX = child PID
    private static void EmitFork(Assembler asm)
    {
        // Parent index + entry, then persist the parent's current frame to its entry.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                  // ECX = parent index
        EntryAddress(asm, ECX, R8);                // R8 = parent entry
        asm.SaveRegs(R(R8));                        // parent entry now holds current regs/EIP/level

        // Flush the parent's dirty pages from their frames back to their block homes, so the
        // flat memcpy below (which copies the parent's home RAM) sees the parent's live data.
        SetupPrivilegedStack(asm);
        asm.Call("flush_frames");

        // Find a free child slot: a Terminated slot in [0, count), else a fresh slot if
        // the table is not full; otherwise fork fails.
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                  // EDI = count
        asm.MovImm(R(ESI), 0);                     // ESI = i

        asm.Label("fk_scan");
        asm.Mov(R(R10), R(ESI));
        asm.Cmp(R(R10), R(EDI));
        asm.Jns("fk_fresh");                       // i >= count: no recycled slot
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryState, R10);
        asm.MovImm(R(R11), Terminated);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("fk_got");                          // reusable slot found
        asm.Inc(R(ESI));
        asm.Jmp("fk_scan");

        asm.Label("fk_fresh");
        asm.MovImm(R(R10), OsLayout.MaxProcesses);
        asm.Cmp(R(ESI), R(R10));
        asm.Jns("fk_fail");                        // table full
        asm.Mov(R(R11), R(ESI));
        asm.Inc(R(R11));
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Store(R(EAX), R(R11));                 // bump count for the fresh slot

        asm.Label("fk_got");
        EntryAddress(asm, ESI, R9);                // R9 = child entry

        // Copy the parent's sizing fields to the child (ProgramAddress is set by alloc).
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryProgramSize);
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryRequiredMemory);
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryRequiredStackSize);
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryTotalSize);
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryDiskSlot);

        // Allocate the child's region (clobbers all registers except EBX = child entry).
        asm.Mov(R(EBX), R(R9));
        SetupPrivilegedStack(asm);
        asm.Call("alloc_sub");                     // sets child.ProgramAddress or -1

        // Reload the parent entry (alloc clobbered R8) from the unchanged current index.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, R8);                // R8 = parent entry

        // If the child allocation failed, free the slot and return -1 to the parent.
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jns("fk_alloc_ok");                    // ProgramAddress >= 0
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        StoreFieldMinusOne(asm, R8, EaxSlot);      // parent EAX = -1
        asm.Jmp("fk_resume");

        asm.Label("fk_alloc_ok");
        // memcpy the parent's RAM region [parentBase, +TotalSize) into the child region.
        LoadField(asm, R8, Hardware.ProcessEntryProgramAddress, R10);  // R10 = parent base
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R11); // R11 = child base
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R12);      // R12 = total
        asm.MovImm(R(R13), 0);                     // R13 = offset

        asm.Label("fk_copy");
        asm.Cmp(R(R13), R(R12));
        asm.Jns("fk_copy_done");                   // offset >= total
        asm.Mov(R(R14), R(R10));
        asm.Add(R(R14), R(R13));
        asm.Load(R(R15), R(R14));                  // R15 = *(parentBase + offset)
        asm.Mov(R(R14), R(R11));
        asm.Add(R(R14), R(R13));
        asm.Store(R(R14), R(R15));                 // *(childBase + offset) = R15
        asm.MovImm(R(EAX), 4);
        asm.Add(R(R13), R(EAX));                   // offset += 4
        asm.Jmp("fk_copy");
        asm.Label("fk_copy_done");

        // Copy the parent's saved register file (entry bytes 0..95) to the child. ESP
        // and the saved EIP are base-relative, so the copy needs no relocation.
        asm.MovImm(R(R13), 0);
        asm.Label("fk_regs");
        asm.MovImm(R(EAX), 96);
        asm.Cmp(R(R13), R(EAX));
        asm.Jns("fk_regs_done");
        asm.Mov(R(R14), R(R8));
        asm.Add(R(R14), R(R13));
        asm.Load(R(R15), R(R14));                  // R15 = parentEntry[offset]
        asm.Mov(R(R14), R(EBX));
        asm.Add(R(R14), R(R13));
        asm.Store(R(R14), R(R15));                 // childEntry[offset] = R15
        asm.MovImm(R(EAX), 4);
        asm.Add(R(R13), R(EAX));
        asm.Jmp("fk_regs");
        asm.Label("fk_regs_done");

        // Child scheduling + identity state.
        LoadField(asm, R8, Hardware.ProcessEntryLevel, R10);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryLevel, R10);      // inherit parent level
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryWaitTarget);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryExitStatus, 0);
        LoadField(asm, R8, Hardware.ProcessEntryPid, R10);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryParentPid, R10);  // ParentPid = parent.Pid

        // Child PID = NextPid++ (keep the child PID in EDX to deliver to the parent).
        Imm16(asm, EAX, OsLayout.NextPidOffset);
        asm.Load(R(EDX), R(EAX));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryPid, EDX);
        asm.Mov(R(R10), R(EDX));
        asm.Inc(R(R10));
        Imm16(asm, EAX, OsLayout.NextPidOffset);
        asm.Store(R(EAX), R(R10));

        // The child owns its own I/O device (device == slot index shim): fds = child idx.
        asm.Mov(R(R11), R(EBX));
        asm.MovImm16(R(EAX), OsLayout.ProcessTableOffset);
        asm.Sub(R(R11), R(EAX));
        asm.MovImm(R(EAX), EntrySize);
        asm.Div(R(R11), R(EAX));                   // R11 = child index
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryFdTable + StdIn * 4, R11);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryFdTable + StdOut * 4, R11);

        // Deliver results: child sees 0, parent sees the child PID (in their EAX slots).
        StoreFieldImm(asm, EBX, EaxSlot, 0);
        StoreFieldReg(asm, R8, EaxSlot, EDX);

        asm.Label("fk_resume");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                  // ECX = current (parent) index for round-robin
        asm.Jmp("resume_mlfq");

        asm.Label("fk_fail");
        // Table full: nothing allocated; the parent gets -1.
        StoreFieldMinusOne(asm, R8, EaxSlot);
        asm.Jmp("fk_resume");
    }

    // IvtExec: replace the running process's image with the program in the disk slot
    // delivered in EAX. Frees the old region, reallocates for the new program's size,
    // DREADs the new image plus the (disk-staged) kernel image, resets the register file
    // (keeping the PID/parentage), and resumes the process running the new program. On
    // out-of-memory the process is terminated.
    //   EBX = entry (kept across free_sub/alloc_sub), R8..R15 = scratch
    private static void EmitExec(Assembler asm)
    {
        asm.Mov(R(R8), R(EAX));                    // R8 = new program slot
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);               // EBX = current entry
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryDiskSlot, R8); // entry.DiskSlot = new slot

        // Free the OLD region first — the entry still holds the old ProgramAddress and
        // TotalSize that free_sub reads (DiskSlot, which we changed, is not read by it).
        SetupPrivilegedStack(asm);
        asm.Call("free_sub");
        // Release the old image's physical frames (its memory is being replaced); the new
        // image faults its pages in fresh after the page table is reseeded on resume.
        asm.Call("release_frames");

        // Recompute sizing from the entry (still holds old ProgramSize/TotalSize; the new
        // slot is in DiskSlot): newLen via DLEN, newTotal = oldTotal - oldProgramSize + newLen.
        LoadField(asm, EBX, Hardware.ProcessEntryDiskSlot, R8);
        asm.DLen(R(R8), R(R9));                    // R9 = newLen
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);   // R10 = oldTotal
        LoadField(asm, EBX, Hardware.ProcessEntryProgramSize, R11); // R11 = oldProgramSize
        asm.Mov(R(R12), R(R10));
        asm.Sub(R(R12), R(R11));
        asm.Add(R(R12), R(R9));                    // R12 = newTotal
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryProgramSize, R9);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryTotalSize, R12);

        // Allocate the new region (reads entry.TotalSize = newTotal).
        SetupPrivilegedStack(asm);
        asm.Call("alloc_sub");                     // sets entry.ProgramAddress or -1

        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R9), R(EAX));
        asm.Jns("ex_ok");
        // Out of memory: the old image is already gone, so terminate the process.
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");

        asm.Label("ex_ok");
        // DREAD the new program image into the allocated region (R9 = ProgramAddress).
        LoadField(asm, EBX, Hardware.ProcessEntryDiskSlot, R10);
        asm.DRead(R(R9), R(R10), R(R11));

        // (The syscall handler is now shared OS code, so there is no per-process kernel
        // image to reload here.)

        // Reset the register file to zero (24 words), so the new program starts fresh.
        asm.MovImm(R(R13), 0);
        asm.Label("ex_clear");
        asm.MovImm(R(EAX), 96);
        asm.Cmp(R(R13), R(EAX));
        asm.Jns("ex_clear_done");
        asm.Mov(R(R14), R(EBX));
        asm.Add(R(R14), R(R13));
        asm.MovImm(R(R15), 0);
        asm.Store(R(R14), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Add(R(R13), R(EAX));
        asm.Jmp("ex_clear");
        asm.Label("ex_clear_done");

        // ESP = top of the user stack = newTotal - KernelStackSize (EIP stays 0).
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);
        asm.MovImm(R(EAX), Hardware.KernelStackSize);
        asm.Sub(R(R10), R(EAX));
        StoreFieldReg(asm, EBX, EspSlot, R10);

        // Scheduling state (keep Pid/ParentPid — exec preserves identity).
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryLevel, User);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        // Resume the process running its new image (it is still the current process).
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
    }

    private const int StdIn  = Hardware.StdIn;
    private const int StdOut = Hardware.StdOut;

    // Copies one 4-byte field from the source entry to the destination entry.
    private static void ForkCopyField(Assembler asm, byte srcEntry, byte dstEntry, int fieldOffset)
    {
        LoadField(asm, srcEntry, fieldOffset, R10);
        StoreFieldReg(asm, dstEntry, fieldOffset, R10);
    }

    // BuddyFree: mark the terminated process's memory block as free in the buddy tree
    // and merge with its buddy recursively while the buddy is also free. Expects
    // EBX = process-table entry (already marked Terminated). Ends with Jmp("resume_mlfq").
    //
    // Registers:
    //   EBX = entry, R9 = programAddress, R10 = totalSize
    //   ESI = level, EDI = blockSize at level
    //   EDX = BuddyLevels, R11 = heapSize (level computation)
    //   R8 = current level (merge loop), R10 = current node (merge loop)
    //   R11 = buddy node (merge loop), R12 = parent node (merge loop)
    //   EAX, EBP, R14, R15 = bit-op scratch
    private static void EmitBuddyFree(Assembler asm)
    {
        asm.Label("free_sub");

        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9);  // R9 = programAddress
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);       // R10 = totalSize

        // Load heap parameters.
        Imm16(asm, EAX, OsLayout.BuddyHeapSizeOffset);
        asm.Load(R(R11), R(EAX));                          // R11 = heapSize

        // Skip bitmap update if heap is not configured (heapSize == 0) or the entry
        // was never allocated via the buddy allocator (TotalSize == 0).
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R11), R(EAX));
        asm.Jz("bf_done");                                 // heapSize == 0: no heap
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("bf_done");                                 // totalSize == 0: not a buddy alloc

        Imm16(asm, EAX, OsLayout.BuddyLevelsOffset);
        asm.Load(R(EDX), R(EAX));                          // EDX = BuddyLevels

        // Compute level (same rule as alloc): halve blockSize while blockSize/2 >= totalSize.
        asm.MovImm(R(ESI), 0);                             // ESI = level = 0
        asm.Mov(R(EDI), R(R11));                           // EDI = currentBlockSize = heapSize

        asm.Label("bf_find_level");
        asm.MovImm(R(EAX), 2);
        asm.Mov(R(EBP), R(EDI));
        asm.Div(R(EBP), R(EAX));                          // EBP = blockSize / 2
        asm.Cmp(R(EBP), R(R10));
        asm.Js("bf_level_done");                           // blockSize/2 < totalSize: stop
        asm.Mov(R(EAX), R(EDX));
        asm.Cmp(R(EAX), R(ESI));
        asm.Jz("bf_level_done");
        asm.Js("bf_level_done");
        asm.Inc(R(ESI));
        asm.Mov(R(EDI), R(EBP));
        asm.Jmp("bf_find_level");

        asm.Label("bf_level_done");
        // ESI = level, EDI = blockSize at level.

        // block_j = (programAddress - HeapStart) / blockSize.
        Imm16(asm, EAX, OsLayout.BuddyHeapStartOffset);
        asm.Load(R(EBP), R(EAX));                         // EBP = HeapStart
        asm.Sub(R(R9), R(EBP));                           // R9 = offset from heap start
        asm.Div(R(R9), R(EDI));                            // R9 = block_j

        // node = 2^level + block_j.
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(ESI));                          // EBP = 2^level
        asm.Add(R(R9), R(EBP));                            // R9 = node (1-indexed)

        // Mark the freed block as free.
        EmitSetBit(asm, R9);                               // bit(R9) = 1

        // Merge loop: while level > 0 and buddy is free, merge with buddy.
        asm.Mov(R(R8), R(ESI));                            // R8 = current level
        asm.Mov(R(R10), R(R9));                            // R10 = current node

        asm.Label("bf_merge");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Jz("bf_done");                                 // level == 0: at root, done

        // buddy = currentNode XOR 1.
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), 1);
        asm.Xor(R(R11), R(EAX));                          // R11 = buddy index

        // Check if buddy is free.
        EmitReadBit(asm, R11);                             // ZF set if buddy bit=0 (not free)
        asm.Jz("bf_done");                                 // buddy not free: stop merging

        // Both current and buddy are free: merge into parent.
        EmitClearBit(asm, R10);                            // clear current node
        EmitClearBit(asm, R11);                            // clear buddy

        // parent = currentNode / 2.
        asm.MovImm(R(EAX), 2);
        asm.Mov(R(R12), R(R10));
        asm.Div(R(R12), R(EAX));                          // R12 = parent

        EmitSetBit(asm, R12);                              // parent = free

        asm.Mov(R(R10), R(R12));                           // ascend to parent
        asm.Dec(R(R8));                                    // level--
        asm.Jmp("bf_merge");

        asm.Label("bf_done");
        asm.Ret();
    }

    // ---- bit operation helpers ---------------------------------------------
    // Each helper operates on the buddy bitmap stored in OS data memory.
    // Node index (1-indexed) is passed in nodeReg.
    // Scratch registers clobbered: EAX, EBP, R14, R15.
    // After EmitReadBit: ZF is set if the bit is 0 (node NOT free).

    // Computes word_addr → EBP, mask (1 << bit_in_word) → R15, bit_in_word → EAX.
    // Clobbers EAX, EBP, R14, R15.
    private static void EmitComputeBitAddress(Assembler asm, byte nodeReg)
    {
        // EAX = nodeReg - 1  (bit_pos, 0-indexed)
        asm.Mov(R(EAX), R(nodeReg));
        asm.Dec(R(EAX));

        // R14 = word_idx = bit_pos / 32
        asm.Mov(R(R14), R(EAX));
        asm.MovImm(R(EBP), 32);
        asm.Div(R(R14), R(EBP));

        // EAX = bit_in_word = bit_pos - word_idx*32
        asm.Mov(R(R15), R(R14));
        asm.Mul(R(R15), R(EBP));                          // R15 = word_idx * 32
        asm.Sub(R(EAX), R(R15));                          // EAX = bit_in_word

        // EBP = word_addr = BuddyBitmapOffset + word_idx * 4
        asm.MovImm(R(EBP), 4);
        asm.Mov(R(R15), R(R14));
        asm.Mul(R(R15), R(EBP));                          // R15 = word_idx * 4
        asm.MovImm16(R(EBP), OsLayout.BuddyBitmapOffset);
        asm.Add(R(EBP), R(R15));                          // EBP = word_addr

        // R15 = mask = 1 << bit_in_word
        asm.MovImm(R(R15), 1);
        asm.Shl(R(R15), R(EAX));                          // R15 = mask
    }

    // ReadBit: ZF set if bit(nodeReg) == 0. Clobbers EAX, EBP, R14, R15.
    private static void EmitReadBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.And(R(R14), R(R15));                           // R14 &= mask; ZF set if bit=0
    }

    // SetBit: set bit(nodeReg) = 1 (mark free). Clobbers EAX, EBP, R14, R15.
    private static void EmitSetBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.Or(R(R14), R(R15));                            // R14 |= mask (set bit)
        asm.Store(R(EBP), R(R14));
    }

    // ClearBit: set bit(nodeReg) = 0 (mark used). Clobbers EAX, EBP, R14, R15.
    private static void EmitClearBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.Not(R(R15));                                   // R15 = ~mask
        asm.And(R(R14), R(R15));                           // R14 &= ~mask (clear bit)
        asm.Store(R(EBP), R(R14));
    }

    // ---- wake routines (unchanged) ----------------------------------------

    private static void EmitWakeEntry(Assembler asm, int reason)
    {
        asm.MovImm(R(EBP), reason);
        asm.Jmp("wk_body");
    }

    private static void EmitWakeBody(Assembler asm)
    {
        asm.Label("wk_body");
        asm.Mov(R(EDX), R(EBP));
        EntryAddress(asm, EAX, EBX);
        LoadField(asm, EBX, Hardware.ProcessEntryState, EAX);
        asm.MovImm(R(EBP), Blocked);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jnz("wk_resume");
        LoadField(asm, EBX, Hardware.ProcessEntryWaitReason, EAX);
        asm.Cmp(R(EAX), R(EDX));
        asm.Jnz("wk_resume");

        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("wk_resume");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("wk_idle");
        EntryAddress(asm, ECX, EBX);
        asm.SaveRegs(R(EBX));
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
        asm.Label("wk_idle");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ---- MLFQ scheduler tail (unchanged) -----------------------------------

    private static void EmitResumeMlfq(Assembler asm)
    {
        asm.Label("resume_mlfq");
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));

        asm.MovImm(R(R8), 0);

        asm.Label("rn_level");
        asm.MovImm(R(R9), OsLayout.QueueCount);
        asm.Cmp(R(R8), R(R9));
        asm.Jns("rn_idle");

        asm.MovImm(R(ESI), 0);

        asm.Label("rn_scan");
        asm.Inc(R(ESI));
        asm.Mov(R(R9), R(EDI));
        asm.Cmp(R(R9), R(ESI));
        asm.Js("rn_next_level");

        asm.Mov(R(R10), R(ECX));
        asm.Add(R(R10), R(ESI));
        asm.Cmp(R(R10), R(EDI));
        asm.Js("rn_in_range");
        asm.Sub(R(R10), R(EDI));
        asm.Label("rn_in_range");

        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(R12), EntrySize);
        asm.Mul(R(R11), R(R12));
        asm.MovImm16(R(R12), OsLayout.ProcessTableOffset);
        asm.Add(R(R11), R(R12));

        LoadField(asm, R11, Hardware.ProcessEntryState, R13);
        asm.MovImm(R(R14), Ready);
        asm.Cmp(R(R13), R(R14));
        asm.Jnz("rn_scan");

        LoadField(asm, R11, Hardware.ProcessEntryPriority, R13);
        asm.Cmp(R(R13), R(R8));
        asm.Jnz("rn_scan");

        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(R10));
        asm.LoadRegs(R(R11));
        asm.SetLayout(R(R11));
        LoadField(asm, R11, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));

        asm.Label("rn_next_level");
        asm.Inc(R(R8));
        asm.Jmp("rn_level");

        asm.Label("rn_idle");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));
        asm.Store(R(EAX), R(EBX));
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ---- emit helpers -------------------------------------------------------

    private static RegisterName R(byte index) { return (RegisterName)index; }

    private static void Imm16(Assembler asm, byte dest, int value)
    {
        asm.MovImm16(R(dest), value);
    }

    private static void EntryAddress(Assembler asm, byte indexReg, byte dest)
    {
        asm.Mov(R(dest), R(indexReg));
        asm.MovImm(R(EAX), EntrySize);
        asm.Mul(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.ProcessTableOffset);
        asm.Add(R(dest), R(EAX));
    }

    private static void LoadField(Assembler asm, byte entry, int fieldOffset, byte dest)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Load(R(dest), R(EBP));
    }

    private static void StoreFieldReg(Assembler asm, byte entry, int fieldOffset, byte valueReg)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(valueReg));
    }

    private static void StoreFieldImm(Assembler asm, byte entry, int fieldOffset, int value)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.MovImm(R(EAX), value);
        asm.Store(R(EBP), R(EAX));
    }

    // Stores -1 into a field (MovImm is 8-bit, so build -1 as 0 then decrement).
    private static void StoreFieldMinusOne(Assembler asm, byte entry, int fieldOffset)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Store(R(EBP), R(EAX));
    }

    // Points the live ESP at the privileged scratch stack so a routine can CALL/RET
    // shared subroutines. Safe to clobber: an OS routine treats the live registers as
    // scratch (the interrupted process's real ESP is held in its saved frame).
    private static void SetupPrivilegedStack(Assembler asm)
    {
        Imm16(asm, ESP, OsLayout.PrivilegedStackTop);
    }

    private static void WriteWord(byte[] buffer, int offset, int value)
    {
        buffer[offset]     = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8)  & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
