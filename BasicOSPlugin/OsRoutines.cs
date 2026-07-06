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
public static partial class OsRoutines
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
    private const int WaitNone        = (int)WaitReason.None;
    private const int WaitChild       = (int)WaitReason.ChildProcess;
    private const int WaitInput       = (int)WaitReason.Input;
    private const int WaitOutput      = (int)WaitReason.Output;
    private const int WaitStringInput = (int)WaitReason.StringInput;
    private const int WaitKeyInput    = (int)WaitReason.KeyInput;
    private const int User       = (int)PrivilegeLevel.User;
    private const int EntrySize  = Hardware.ProcessEntrySize;

    // Byte offset of the EAX / EIP / ESP slots within an entry's saved register file
    // (the register file mirrors the live registers: slot = register index * 4).
    private const int EaxSlot    = (int)RegisterName.EAX * 4;
    private const int EbxSlot    = (int)RegisterName.EBX * 4;
    private const int EdxSlot    = (int)RegisterName.EDX * 4;
    private const int EipSlot    = (int)RegisterName.EIP * 4;
    private const int EspSlot    = (int)RegisterName.ESP * 4;

    public static byte[] BuildOsImage()
    {
        Assembler asm = new Assembler();

        int contextSwitch = OsLayout.CodeBase + asm.CodeLength; EmitContextSwitch(asm);
        int schedule      = OsLayout.CodeBase + asm.CodeLength; EmitSchedule(asm);
        int block         = OsLayout.CodeBase + asm.CodeLength; EmitBlock(asm);
        int wakeInput     = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, WaitInput);
        int wakeOutput    = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, WaitOutput);
        int wakeKey       = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, WaitKeyInput);
        EmitWakeBody(asm);
        int halt          = OsLayout.CodeBase + asm.CodeLength; EmitHalt(asm);
        int invalid       = OsLayout.CodeBase + asm.CodeLength; EmitInvalidInstruction(asm);
        int allocate      = OsLayout.CodeBase + asm.CodeLength; EmitBuddyAlloc(asm);
        int spawn         = OsLayout.CodeBase + asm.CodeLength; EmitSpawn(asm);
        int fork          = OsLayout.CodeBase + asm.CodeLength; EmitFork(asm);
        int exec          = OsLayout.CodeBase + asm.CodeLength; EmitExec(asm);
        int wait          = OsLayout.CodeBase + asm.CodeLength; EmitWait(asm);
        int reap          = OsLayout.CodeBase + asm.CodeLength; EmitReap(asm);
        int kill          = OsLayout.CodeBase + asm.CodeLength; EmitKill(asm);
        int sigReturn     = OsLayout.CodeBase + asm.CodeLength; EmitSigReturn(asm);
        int syscall       = OsLayout.CodeBase + asm.CodeLength; EmitSyscall(asm);
        int pageFault     = OsLayout.CodeBase + asm.CodeLength; EmitPageFault(asm);
        EmitPageIn(asm);          // shared subroutine "page_in"; ends with Ret
        EmitEnsureUserPage(asm);  // shared subroutine "ensure_user_page"; ends with Ret
        int ensureUserPageOp = OsLayout.CodeBase + asm.CodeLength; EmitEnsureUserPageOp(asm);
        EmitUserWordAddr(asm);    // shared subroutine "user_word_addr"; ends with Ret
        int cacheOp       = OsLayout.CodeBase + asm.CodeLength; EmitCacheOp(asm);
        int fsOp          = OsLayout.CodeBase + asm.CodeLength; EmitFsOp(asm);
        int fsSyscall     = OsLayout.CodeBase + asm.CodeLength; EmitFsSyscall(asm);
        EmitExitBody(asm);      // shared label "exit_body" (HLT/EXIT/fault tail)
        EmitTeardownReap(asm);  // shared subroutine "teardown_reap" (CALL/RET; exit_body + kill_core)
        EmitSignalSubroutines(asm); // shared subroutine "sig_copy" (CALL/RET; kill_core + sigreturn)
        EmitAllocSub(asm);      // shared subroutine "alloc_sub"; ends with Ret
        EmitBuddyFree(asm);     // label "buddy_free_entry"; ends with Jmp("resume_mlfq")
        EmitReleaseFrames(asm); // shared subroutine "release_frames"; ends with Ret
        EmitFlushFrames(asm);   // shared subroutine "flush_frames"; ends with Ret
        EmitZeroSwapSlots(asm); // shared subroutine "zero_swap_slots"; ends with Ret
        EmitPairResolve(asm);   // shared subroutine "pair_resolve"; ends with Ret
        EmitResolveCow(asm);    // shared subroutine "resolve_cow"; ends with Ret
        EmitCowShare(asm);      // shared subroutine "cow_share"; ends with Ret
        EmitCacheSubroutines(asm); // "cache_find/get/dirty/write_through/pin/unpin/discard/flush"
        EmitFsSubroutines(asm);    // "fs_format/alloc_block/free_block/chain_next/chain_set_next"
        EmitFsDirSubroutines(asm); // "fs_hash/root_dir/dir_lookup/dir_insert/dir_remove"
        EmitFsPathSubroutines(asm);// "fs_extract_component/path_resolve/mkdir"
        EmitFsFileSubroutines(asm);// "oft_alloc/resolve_parent/create_file/open_core/close_core"
        EmitFsRwSubroutines(asm);  // "oft_from_fd/grow_chain/read_core/write_core"
        EmitFsLoadImage(asm);      // "fs_load_image" (chain→RAM copy; shared by spawn + exec)
        EmitExecTokenizer(asm);    // "exec_next_token" (space tokenizer over the captured command line)
        EmitExecBuildArgv(asm);    // "exec_build_argv" (write argv[] + strings into the child image)
        EmitFsExecSubroutine(asm); // "fs_exec_core"
        EmitFsMaintSubroutines(asm); // "oft_find_first/fs_unlink/fs_mkdir_path/fs_readdir"
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
        WriteWord(image, Hardware.IvtWakeKey * 4,            wakeKey);
        WriteWord(image, Hardware.IvtHalt * 4,               halt);
        WriteWord(image, Hardware.IvtInvalidInstruction * 4, invalid);
        WriteWord(image, Hardware.IvtAllocate * 4,           allocate);
        WriteWord(image, Hardware.IvtSpawn * 4,              spawn);
        WriteWord(image, Hardware.IvtFork * 4,               fork);
        WriteWord(image, Hardware.IvtExec * 4,               exec);
        WriteWord(image, Hardware.IvtWait * 4,               wait);
        WriteWord(image, Hardware.IvtReap * 4,               reap);
        WriteWord(image, Hardware.IvtKill * 4,               kill);
        WriteWord(image, Hardware.IvtSyscall * 4,            syscall);
        WriteWord(image, Hardware.IvtPageFault * 4,          pageFault);
        WriteWord(image, Hardware.IvtCacheOp * 4,            cacheOp);
        WriteWord(image, Hardware.IvtFsOp * 4,               fsOp);
        WriteWord(image, Hardware.IvtFsSyscall * 4,          fsSyscall);
        WriteWord(image, Hardware.IvtEnsureUserPage * 4,     ensureUserPageOp);
        WriteWord(image, Hardware.IvtSigReturn * 4,          sigReturn);

        // Default every process's copy-on-write partner to -1 (none) in the image itself, so
        // even minimal hand-seeded test images (which skip SeedOsData) never see a process
        // exit as if it had a COW partner — a 0 here would mean "partner is slot 0".
        for (int i = 0; i < OsLayout.MaxProcesses; i++)
        {
            WriteWord(image, OsLayout.CowPartnerAddress(i), -1);
        }
        return image;
    }

    // ---- scheduling routines (unchanged) ------------------------------------

    // ===== EmitContextSwitch =================================================
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
        // Periodic write-back: tick the flush countdown; on reaching zero, flush all dirty
        // unpinned cache slots to disk and reload the interval. An empty/clean cache makes
        // cache_flush a no-op. NOTE: cache_flush (like all cache_* subs) clobbers ECX, and
        // resume_mlfq uses ECX as the round-robin start index — so cs_flush_skip reloads ECX from
        // CurrentIndexOffset before jumping there. (This periodic flush only fires every
        // CacheFlushInterval context switches, so the missing reload was a latent bug that only
        // surfaced under OUTS/INS-heavy programs racking up hundreds of context switches.)
        Imm16(asm, R8, OsLayout.CacheFlushTimerOffset);
        asm.Load(R(R10), R(R8));
        asm.Dec(R(R10));
        asm.Store(R(R8), R(R10));
        asm.MovImm(R(R11), 1);
        asm.Cmp(R(R10), R(R11));
        asm.Jns("cs_flush_skip");                 // timer >= 1: not time yet
        SetupPrivilegedStack(asm);
        asm.Call("cache_flush");
        Imm16(asm, R8, OsLayout.CacheFlushTimerOffset);
        Imm16(asm, R10, OsLayout.CacheFlushInterval);
        asm.Store(R(R8), R(R10));
        asm.Label("cs_flush_skip");
        // Reload ECX = current index: cache_flush above clobbers it, and resume_mlfq round-robins
        // from ECX. Harmless on the no-flush path (ECX already held this value).
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    // ===== EmitSchedule ======================================================
    private static void EmitSchedule(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    // ===== EmitBlock =========================================================
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

    // ===== EmitHalt ==========================================================
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

    // ===== EmitInvalidInstruction ============================================
    private static void EmitInvalidInstruction(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryExitStatus); // fault status = -1
        asm.Jmp("exit_body");
    }

    // ===== EmitExitBody ======================================================
    // exit_body: tear down the running process (entry in EBX, ExitStatus already set).
    // Frees its memory, then resolves who collects its status:
    //   - a parent currently blocked in wait() on this PID -> deliver status, wake it,
    //     and reap this entry;
    //   - else if the parent is still alive -> keep this entry as a Zombie;
    //   - else (no/dead parent) -> reap this entry now.
    private static void EmitExitBody(Assembler asm)
    {
        asm.Label("exit_body");
        SetupPrivilegedStack(asm);
        asm.Call("teardown_reap");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    // ===== EmitTeardownReap ==================================================
    // teardown_reap: free the process whose entry is EBX (index in CurrentIndexOffset) and resolve
    // who collects its exit status — wake a parent blocked in wait(), else keep a Zombie, else reap
    // as an orphan — then reap this process's own zombie children. CALL/RET; the caller MUST run
    // SetupPrivilegedStack first (this sub must not, or it would reset ESP and lose its own return
    // address). Extracted from exit_body (Shell §2.5) so kill_core can run the identical teardown on
    // an arbitrary target by pointing CurrentIndexOffset (+ EBX) at it before the call.
    private static void EmitTeardownReap(Assembler asm)
    {
        asm.Label("teardown_reap");
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated); // hide from scans
        asm.Call("free_sub");
        // Materialise any copy-on-write share first, so the partner keeps a private copy of
        // the shared pages before this process frees its frames and zeroes its slots.
        asm.Call("resolve_cow");
        // Reclaim this process's physical frames (no write-back — its memory is gone), so
        // they cannot later be evicted into freed/reused RAM. EBX (entry) is preserved.
        asm.Call("release_frames");
        // Zero this process's swap slots so a slot reused by a later process never serves
        // the dead process's stale data.
        asm.Call("zero_swap_slots");
        // resolve_cow clobbered EBX; reload it = the target process's entry (CurrentIndexOffset).
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(EBX), R(EAX));
        EntryAddress(asm, EBX, EBX);

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
        asm.Ret();
    }

    // ===== EmitWait ==========================================================
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

    // ===== EmitReap ==========================================================
    // IvtReap: non-blocking reap (Shell §2.5 job control). EAX = target pid on entry (0 = reap
    // any dead child of the caller; > 0 = reap that specific child). Resumes the caller with the
    // reaped pid in EAX (0 if no matching dead child) and its exit status in EDX. Never blocks.
    //
    // A Zombie has already released its memory/frames/swap in exit_body — it only holds the
    // process-table slot for its pid/status — so reaping is just marking the slot Terminated,
    // exactly like EmitWait's reap path. This routine duplicates that ~12-line scan (rather than
    // sharing it with EmitWait) to keep the well-tested blocking WAIT path untouched.
    //   Input: EAX = target pid. Scratch: EAX/EBX/ECX/ESI/EDI/R8-R15 (+ EBP via LoadField).
    private static void EmitReap(Assembler asm)
    {
        asm.Mov(R(R8), R(EAX));                     // R8 = target pid (0 = any)
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);                // EBX = caller (parent) entry
        LoadField(asm, EBX, Hardware.ProcessEntryPid, R9);  // R9 = caller's own pid (parent match)

        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                   // EDI = process count
        asm.MovImm(R(ESI), 0);                      // ESI = scan index

        asm.Label("rp_scan");
        asm.Cmp(R(ESI), R(EDI));
        asm.Jns("rp_none");                          // scanned all entries → nothing to reap
        EntryAddress(asm, ESI, R10);                 // R10 = candidate entry
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Zombie);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("rp_next");                          // not a zombie
        asm.MovImm(R(R13), 0);
        asm.Cmp(R(R8), R(R13));
        asm.Jz("rp_any");                            // target == 0 → match any child of caller
        // Targeted: match Pid == target (preserves WAIT-style by-pid semantics, no parent check).
        LoadField(asm, R10, Hardware.ProcessEntryPid, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("rp_next");
        asm.Jmp("rp_found");
        asm.Label("rp_any");
        // Reap-any: match ParentPid == caller's pid, so we only reap our own children.
        LoadField(asm, R10, Hardware.ProcessEntryParentPid, R11);
        asm.Cmp(R(R11), R(R9));
        asm.Jnz("rp_next");

        asm.Label("rp_found");
        LoadField(asm, R10, Hardware.ProcessEntryPid, R14);         // R14 = reaped pid
        LoadField(asm, R10, Hardware.ProcessEntryExitStatus, R15);  // R15 = exit status
        StoreFieldImm(asm, R10, Hardware.ProcessEntryState, Terminated); // free the slot
        asm.Jmp("rp_deliver");

        asm.Label("rp_next");
        asm.Inc(R(ESI));
        asm.Jmp("rp_scan");

        asm.Label("rp_none");
        asm.MovImm(R(R14), 0);                       // no dead child: pid = 0
        asm.MovImm(R(R15), 0);                       // status = 0

        asm.Label("rp_deliver");
        asm.SaveRegs(R(EBX));                         // persist the caller's captured trap frame
        StoreFieldReg(asm, EBX, EaxSlot, R14);        // EAX = reaped pid (0 if none)
        StoreFieldReg(asm, EBX, EdxSlot, R15);        // EDX = exit status
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
    }

    // ===== EmitKill ==========================================================
    // IvtKill: apply a signal to an arbitrary process (Shell §2.5 job control). EAX = target pid on
    // entry; the signal number is in OsLayout.KillSig. Resumes the caller with 0 (delivered) or -1
    // (no such live pid) in EAX. SigTerm/SigKill run the identical teardown as exit_body via the
    // shared teardown_reap (freeing memory, waking a wait()ing parent, zombie/orphan handling);
    // SigStop/SigCont are no-ops here (wired in JC-C). Killing self behaves exactly like EXIT
    // (teardown + reschedule, never returns). Killing another process resumes the killer.
    //   Scratch: EAX/EBX/ECX/ESI/EDI/R8-R12 (+ teardown_reap clobbers all).
    private static void EmitKill(Assembler asm)
    {
        asm.Mov(R(R8), R(EAX));                    // R8 = target pid
        // Resolve target pid -> live process index in R9 (-1 = not found). Skip dead slots
        // (Terminated + Zombie) so we never re-tear-down an already-freed process.
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);
        asm.MovImm(R(R9), 0);
        asm.Dec(R(R9));                            // R9 = -1 (MovImm is 8-bit; -1 must be built, not immediate)
        asm.Label("kl_scan");
        asm.Cmp(R(ESI), R(EDI));
        asm.Jns("kl_resolved");
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Terminated);
        asm.Cmp(R(R11), R(R12));
        asm.Jz("kl_next");
        asm.MovImm(R(R12), Zombie);
        asm.Cmp(R(R11), R(R12));
        asm.Jz("kl_next");
        LoadField(asm, R10, Hardware.ProcessEntryPid, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("kl_next");
        asm.Mov(R(R9), R(ESI));                    // found: R9 = target index
        asm.Jmp("kl_resolved");
        asm.Label("kl_next");
        asm.Inc(R(ESI));
        asm.Jmp("kl_scan");

        asm.Label("kl_resolved");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                  // ECX = killer index
        EntryAddress(asm, ECX, EBX);               // EBX = killer entry (for result delivery)
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R9), R(EAX));
        asm.Js("kl_deliver_fail");                 // R9 < 0: no such live pid -> -1

        Imm16(asm, EAX, OsLayout.KillSig);
        asm.Load(R(R10), R(EAX));                  // R10 = signal
        asm.MovImm(R(R11), Hardware.SigTerm);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("kl_catch");                        // SigTerm is catchable: handler, else default teardown
        asm.MovImm(R(R11), Hardware.SigInt);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("kl_catch");                        // SigInt is catchable (Ctrl-C)
        asm.MovImm(R(R11), Hardware.SigKill);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("kl_term");                         // SigKill is uncatchable: always teardown
        asm.MovImm(R(R11), Hardware.SigStop);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("kl_stop");
        asm.MovImm(R(R11), Hardware.SigCont);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("kl_cont");
        asm.Jmp("kl_deliver_ok");                  // unknown signal: no-op, deliver 0

        // kl_catch: a catchable signal (SigTerm/SigInt). If the target installed a handler (SIGACTION),
        // deliver to it — snapshot the target's register file into SignalSave, redirect its saved EIP to
        // the handler, mark it in-handler + runnable — instead of the default teardown. No handler →
        // fall through to kl_term (default action = teardown, status -1). Already inside a handler
        // (InHandler=1) → leave the signal pending, delivered when the handler SIGRETURNs.
        //   Live here: R8=target pid, R9=target index, R10=sig, ECX=killer index.
        asm.Label("kl_catch");
        EntryAddress(asm, R9, R15);                          // R15 = target entry
        LoadField(asm, R15, Hardware.ProcessEntrySigHandler, R11);
        asm.MovImm(R(R12), 0);
        asm.Cmp(R(R11), R(R12));
        asm.Jz("kl_term");                                  // no handler → default action
        LoadField(asm, R15, Hardware.ProcessEntryInHandler, R12);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R12), R(EAX));
        asm.Jnz("kl_defer");                                // mid-handler → queue as pending
        // If we are signalling ourselves (killer == target — e.g. Ctrl-C to the running foreground
        // process), the live context is only in the HW capture buffer; persist it into the entry with
        // SaveRegs so the snapshot is the true pre-signal state.
        asm.Cmp(R(ECX), R(R9));
        asm.Jnz("kl_catch_snap");
        asm.SaveRegs(R(R15));
        asm.Label("kl_catch_snap");
        SetupPrivilegedStack(asm);
        // Snapshot entry.registerFile (offset 0) → SignalSave[targetIndex]. R13=src, R14=dst.
        asm.Mov(R(R13), R(R15));
        asm.Mov(R(R14), R(R9));
        asm.MovImm(R(EAX), OsLayout.SignalSaveStride);       // 96 (< 256 → 8-bit immediate is safe)
        asm.Mul(R(R14), R(EAX));
        Imm16(asm, EAX, OsLayout.SignalSaveBase);
        asm.Add(R(R14), R(EAX));
        asm.Call("sig_copy");
        // Redirect the target's saved EIP to the handler; mark in-handler and runnable. (Only the
        // register file is saved/restored, not State — a Blocked target thus restarts its syscall
        // after the handler, EINTR-style.)
        LoadField(asm, R15, Hardware.ProcessEntrySigHandler, R11);
        StoreFieldReg(asm, R15, EipSlot, R11);
        StoreFieldImm(asm, R15, Hardware.ProcessEntryInHandler, 1);
        StoreFieldImm(asm, R15, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, R15, Hardware.ProcessEntryStopped, 0);
        // Self → resume the target (self) directly at the handler. Other → deliver 0 to the killer and
        // let the target run its handler when next scheduled.
        asm.Cmp(R(ECX), R(R9));
        asm.Jnz("kl_catch_other");
        StoreFieldImm(asm, R15, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R15, Hardware.ProcessEntryTicksUsed, 0);
        asm.LoadRegs(R(R15));
        asm.SetLayout(R(R15));
        LoadField(asm, R15, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
        asm.Label("kl_catch_other");
        EntryAddress(asm, ECX, EBX);                        // EBX = killer entry (for delivery)
        asm.Jmp("kl_deliver_ok");

        // kl_defer: the target is mid-handler; queue the signal as pending (one slot — a later signal
        // overwrites it). Deliver 0 to the killer (or, if self, resume self unchanged — the pending
        // signal fires on this handler's SIGRETURN).
        asm.Label("kl_defer");
        StoreFieldReg(asm, R15, Hardware.ProcessEntrySigPending, R10);
        EntryAddress(asm, ECX, EBX);                        // EBX = killer entry
        asm.Jmp("kl_deliver_ok");

        asm.Label("kl_term");
        // Suicide (killer == target) is exactly EXIT: set our own status and fall into exit_body.
        asm.Cmp(R(ECX), R(R9));
        asm.Jnz("kl_other");
        EntryAddress(asm, R9, EBX);                // EBX = self entry
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryExitStatus); // killed => status -1
        asm.Jmp("exit_body");                      // teardown + reschedule (never returns)

        asm.Label("kl_other");
        // Tear down a different process: teardown_reap runs on "the current process", so save the
        // killer index, repoint CurrentIndex at the target for the teardown, then restore it.
        Imm16(asm, EAX, OsLayout.KillSaveIndex);
        asm.Store(R(EAX), R(ECX));                 // save killer index
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(R9));                  // CurrentIndex = target index
        EntryAddress(asm, R9, EBX);                // EBX = target entry
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryExitStatus); // killed => status -1
        SetupPrivilegedStack(asm);
        asm.Call("teardown_reap");
        Imm16(asm, EAX, OsLayout.KillSaveIndex);
        asm.Load(R(ECX), R(EAX));                  // ECX = killer index
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(ECX));                 // restore CurrentIndex = killer
        EntryAddress(asm, ECX, EBX);               // EBX = killer entry
        asm.Jmp("kl_deliver_ok");

        // SIGCONT: clear the target's stop flag; it becomes schedulable again from its saved context
        // (its underlying state — Ready or Blocked — was preserved while stopped). R8=pid, R9=index.
        asm.Label("kl_cont");
        EntryAddress(asm, R9, R15);                // R15 = target entry
        StoreFieldImm(asm, R15, Hardware.ProcessEntryStopped, 0);
        EntryAddress(asm, ECX, EBX);               // EBX = killer entry (for delivery)
        asm.Jmp("kl_deliver_ok");

        // SIGSTOP: set the target's stop flag (the scheduler now skips it). Wake a parent blocked in
        // WAIT on this target with the "stopped" status -2 (WUNTRACED) WITHOUT reaping the target — it
        // is stopped, not dead. If we stopped ourselves, persist our context and reschedule (we cannot
        // return). R8=target pid, R9=target index, ECX=killer index, EBX=killer entry.
        asm.Label("kl_stop");
        EntryAddress(asm, R9, R15);                // R15 = target entry
        StoreFieldImm(asm, R15, Hardware.ProcessEntryStopped, 1);
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);
        asm.Label("kls_scan");
        asm.Cmp(R(ESI), R(EDI));
        asm.Jns("kls_done");
        EntryAddress(asm, ESI, R10);
        LoadField(asm, R10, Hardware.ProcessEntryState, R11);
        asm.MovImm(R(R12), Blocked);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("kls_next");
        LoadField(asm, R10, Hardware.ProcessEntryWaitReason, R11);
        asm.MovImm(R(R12), WaitChild);
        asm.Cmp(R(R11), R(R12));
        asm.Jnz("kls_next");
        LoadField(asm, R10, Hardware.ProcessEntryWaitTarget, R11);
        asm.Cmp(R(R11), R(R8));
        asm.Jnz("kls_next");
        // Found the waiting parent: deliver -2, wake it, clear its wait target (leave the child stopped).
        asm.MovImm(R(R11), 0);
        asm.Dec(R(R11));
        asm.Dec(R(R11));                           // R11 = -2 (stopped status; MovImm is 8-bit)
        StoreFieldReg(asm, R10, EaxSlot, R11);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R10, Hardware.ProcessEntryTicksUsed, 0);
        StoreFieldMinusOne(asm, R10, Hardware.ProcessEntryWaitTarget);
        asm.Jmp("kls_done");
        asm.Label("kls_next");
        asm.Inc(R(ESI));
        asm.Jmp("kls_scan");
        asm.Label("kls_done");
        // Self-stop? killer (ECX) == target (R9): we cannot resume ourselves; save our context (so a
        // later SIGCONT resumes us after KILL with EAX=0) and reschedule. Otherwise deliver 0 to the killer.
        asm.Cmp(R(ECX), R(R9));
        asm.Jnz("kl_stop_other");
        EntryAddress(asm, R9, EBX);                // EBX = self entry
        asm.SaveRegs(R(EBX));
        Imm16(asm, EAX, OsLayout.KillNoDeliver);
        asm.Load(R(R10), R(EAX));
        asm.MovImm(R(R11), 0);
        asm.Cmp(R(R10), R(R11));
        asm.Jnz("kls_self_noeax");                 // terminal Ctrl-Z: leave the stopped job's EAX intact
        StoreFieldImm(asm, EBX, EaxSlot, 0);       // KILL returns 0 to us when continued
        asm.Label("kls_self_noeax");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
        asm.Label("kl_stop_other");
        EntryAddress(asm, ECX, EBX);               // EBX = killer entry (for delivery)

        asm.Label("kl_deliver_ok");
        asm.MovImm(R(R8), 0);                      // result = 0 (delivered)
        asm.Jmp("kl_deliver");
        asm.Label("kl_deliver_fail");
        asm.MovImm(R(R8), 0);
        asm.Dec(R(R8));                            // result = -1 (MovImm is 8-bit; build -1)
        asm.Label("kl_deliver");
        asm.SaveRegs(R(EBX));                      // persist the killer's captured trap frame
        Imm16(asm, EAX, OsLayout.KillNoDeliver);
        asm.Load(R(R9), R(EAX));
        asm.MovImm(R(R10), 0);
        asm.Cmp(R(R9), R(R10));
        asm.Jnz("kl_deliver_noeax");               // terminal signal: no killer, so don't clobber EAX
        StoreFieldReg(asm, EBX, EaxSlot, R8);       // override EAX = result
        asm.Label("kl_deliver_noeax");
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
    }

    // ===== EmitSigReturn =====================================================
    // IvtSigReturn: return from a catchable-signal handler (Shell §2.5 job control, JC-E). The running
    // process executed SIGRETURN. Restore its pre-signal register file from SignalSave, clear the
    // in-handler flag, and — if a signal was left pending during the handler — immediately re-deliver
    // it (re-snapshot the just-restored context, redirect to the handler again). Then resume.
    //   Scratch: EAX/EBX/ECX/R9/R11/R12/R13/R14/R15 (+ sig_copy). Never returns (OSRETs).
    private static void EmitSigReturn(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                            // ECX = running (returning) index
        EntryAddress(asm, ECX, R15);                         // R15 = entry
        SetupPrivilegedStack(asm);
        // Restore SignalSave[ECX] → entry.registerFile. R13=src, R14=dst.
        asm.Mov(R(R13), R(ECX));
        asm.MovImm(R(EAX), OsLayout.SignalSaveStride);
        asm.Mul(R(R13), R(EAX));
        Imm16(asm, EAX, OsLayout.SignalSaveBase);
        asm.Add(R(R13), R(EAX));                             // src = &SignalSave[ECX]
        asm.Mov(R(R14), R(R15));                             // dst = entry (register file at offset 0)
        asm.Call("sig_copy");
        StoreFieldImm(asm, R15, Hardware.ProcessEntryInHandler, 0);
        // Pending signal queued during the handler? Re-deliver it (handler is still installed).
        LoadField(asm, R15, Hardware.ProcessEntrySigPending, R9);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R9), R(EAX));
        asm.Jz("sr_resume");
        StoreFieldImm(asm, R15, Hardware.ProcessEntrySigPending, 0);
        // Re-snapshot the restored context → SignalSave[ECX], then redirect to the handler.
        asm.Mov(R(R13), R(R15));
        asm.Mov(R(R14), R(ECX));
        asm.MovImm(R(EAX), OsLayout.SignalSaveStride);
        asm.Mul(R(R14), R(EAX));
        Imm16(asm, EAX, OsLayout.SignalSaveBase);
        asm.Add(R(R14), R(EAX));
        asm.Call("sig_copy");
        LoadField(asm, R15, Hardware.ProcessEntrySigHandler, R11);
        StoreFieldReg(asm, R15, EipSlot, R11);
        StoreFieldImm(asm, R15, Hardware.ProcessEntryInHandler, 1);
        asm.Label("sr_resume");
        asm.LoadRegs(R(R15));
        asm.SetLayout(R(R15));
        LoadField(asm, R15, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
    }

    // ===== EmitSignalSubroutines =============================================
    // sig_copy: copy one register file (SignalSaveStride bytes) from R13 (src) to R14 (dst). CALL/RET;
    // requires the privileged stack. Clobbers EAX/R11/R12; preserves R13/R14 (bases; the loop indexes
    // with R12). Used by kill_core (snapshot) and sigreturn (restore + re-snapshot).
    private static void EmitSignalSubroutines(Assembler asm)
    {
        asm.Label("sig_copy");
        asm.MovImm(R(R12), 0);                               // byte offset
        asm.Label("sc_loop");
        asm.MovImm(R(R11), OsLayout.SignalSaveStride);       // 96 (< 256 → 8-bit immediate is safe)
        asm.Cmp(R(R12), R(R11));
        asm.Jns("sc_done");
        asm.Mov(R(EAX), R(R13));
        asm.Add(R(EAX), R(R12));
        asm.Load(R(R11), R(EAX));                            // R11 = *(src + off)
        asm.Mov(R(EAX), R(R14));
        asm.Add(R(EAX), R(R12));
        asm.Store(R(EAX), R(R11));                           // *(dst + off) = R11
        asm.MovImm(R(R11), 4);
        asm.Add(R(R12), R(R11));
        asm.Jmp("sc_loop");
        asm.Label("sc_done");
        asm.Ret();
    }

    // ===== EmitSyscall =======================================================
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
        asm.MovImm(R(EDX), Instruction.OUTS);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_outs");
        asm.MovImm(R(EDX), Instruction.INS);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_ins");
        asm.MovImm(R(EDX), Instruction.INK);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_ink");
        asm.MovImm(R(EDX), Instruction.INPOLL);
        asm.Cmp(R(EBX), R(EDX));
        asm.Jz("syscall_inpoll");
        asm.Iret();                               // unknown cause — return (should not happen)

        asm.Label("syscall_out");
        asm.Load(R(ESI), R(ECX));                 // ESI = user's operand value (from the save area)
        asm.Out(R(ESI));                          // real device write (kernel level)
        asm.Iret();

        asm.Label("syscall_in");
        asm.In(R(ESI));                           // real device read (kernel level)
        asm.Store(R(ECX), R(ESI));                // write the result back into the save-area slot
        asm.Iret();

        // OUTS: load ptr from save area (b1 slot = ECX), load len from +12 slot, call kernel OUTS.
        asm.Label("syscall_outs");
        asm.Load(R(ESI), R(ECX));                 // ESI = ptr value
        asm.MovImm(R(EAX), Hardware.KernelTrapInfoOffset + 12);
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(EAX), R(EAX));                 // EAX = byte-offset of len register in save area
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(EDX), R(EAX));                 // EDX = len value
        asm.Outs(R(ESI), R(EDX));                 // kernel-level string output
        asm.Iret();

        // INS: load ptr from save area (b1 slot = ECX), load maxLen from +12 slot, call kernel INS.
        asm.Label("syscall_ins");
        asm.Load(R(ESI), R(ECX));                 // ESI = ptr value
        asm.MovImm(R(EAX), Hardware.KernelTrapInfoOffset + 12);
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(EAX), R(EAX));                 // EAX = byte-offset of maxLen register in save area
        asm.Add(R(EAX), R(EBP));
        asm.Load(R(EDX), R(EAX));                 // EDX = maxLen value
        asm.Ins(R(ESI), R(EDX));                  // kernel-level string input (blocks if empty)
        asm.Iret();

        // INK: block until a raw keypress; write keycode to the caller's save-area slot.
        asm.Label("syscall_ink");
        asm.Ink(R(ESI));                           // kernel-level key read (blocks if empty)
        asm.Store(R(ECX), R(ESI));                 // write keycode to save-area slot
        asm.Iret();

        // INPOLL: non-blocking key read; write keycode (or -1) to the caller's save-area slot.
        asm.Label("syscall_inpoll");
        asm.InkPoll(R(ESI));                       // non-blocking; ESI = keycode or -1
        asm.Store(R(ECX), R(ESI));
        asm.Iret();
    }

    // ---- buddy allocator ---------------------------------------------------

    // ===== EmitBuddyAlloc ====================================================
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

    // ===== EmitAllocSub ======================================================
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

    // ===== EmitSpawn =========================================================
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

        // Load the program image into the allocated RAM. A slot-backed process (DiskSlot >= 0)
        // DREADs its disk image; an FS-backed process (DiskSlot < 0, Phase 4) chain-loads its
        // program from the FS via fs_load_image using the entry's FirstBlock + ProgramSize.
        LoadField(asm, EBX, Hardware.ProcessEntryDiskSlot, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Js("sp_fs");                           // DiskSlot < 0 → FS-backed
        asm.DRead(R(R9), R(R10), R(R11));          // DREAD programAddress, slot, lenOut
        asm.Jmp("sp_loaded");

        asm.Label("sp_fs");
        // fs_load_image(EBX=firstBlock, ECX=words, EDX=dest). It clobbers EBX (the entry),
        // so stash the entry addr in FsScratchArgA (fs_load_image only touches FsRw*) and
        // restore it after. words = ceil(ProgramSize / 4).
        SpillStore(asm, OsLayout.FsScratchArgA, EBX);
        asm.Mov(R(EDX), R(R9));                     // dest = programAddress
        LoadField(asm, EBX, Hardware.ProcessEntryFirstBlock, R8);   // firstBlock
        LoadField(asm, EBX, Hardware.ProcessEntryProgramSize, R9);  // ProgramSize (bytes)
        asm.MovImm(R(EAX), 3);
        asm.Add(R(R9), R(EAX));
        asm.MovImm(R(EAX), 4);
        asm.Div(R(R9), R(EAX));                     // words = ceil(ProgramSize/4)
        asm.Mov(R(EBX), R(R8));                     // EBX = firstBlock
        asm.Mov(R(ECX), R(R9));                     // ECX = words
        asm.Call("fs_load_image");
        SpillLoad(asm, OsLayout.FsScratchArgA, EBX); // restore entry addr
        asm.Label("sp_loaded");

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

    // ===== EmitFork ==========================================================
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

        // Prepare copy-on-write sharing: resolve any pre-existing COW (so this fork starts
        // from a clean private state), flush the parent's dirty frames to their backings (so
        // the snapshot slots are current), then convert the parent's data pages to COW. The
        // child will share these read-only via the page table; a write later resolves them.
        SetupPrivilegedStack(asm);
        asm.Call("resolve_cow");
        asm.Call("flush_frames");
        asm.Call("cow_share");
        EntryAddress(asm, ECX, R8);                // restore R8 = parent entry (subroutines clobbered it)

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
        ForkCopyField(asm, R8, R9, Hardware.ProcessEntryFirstBlock); // keep FS-backing identity

        // Record the copy-on-write partnership: parent and child each point at the other.
        // The child's page table then seeds its data pages COW (sharing the parent's slots,
        // see SeedPageTableIfNew); the parent's were converted by cow_share above. A write by
        // either side later traps and resolves just that page. ECX = parent, ESI = child.
        asm.Mov(R(R10), R(ECX));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R10), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.CowPartnerBase);
        asm.Add(R(R10), R(EAX));
        asm.Store(R(R10), R(ESI));                  // cowPartner[parent] = child
        asm.Mov(R(R10), R(ESI));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R10), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.CowPartnerBase);
        asm.Add(R(R10), R(EAX));
        asm.Store(R(R10), R(ECX));                  // cowPartner[child] = parent

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

    // ===== EmitExec ==========================================================
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
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryFirstBlock); // slot-backed now, not FS

        // Free the OLD region first — the entry still holds the old ProgramAddress and
        // TotalSize that free_sub reads (DiskSlot, which we changed, is not read by it).
        SetupPrivilegedStack(asm);
        asm.Call("free_sub");
        // Materialise any COW share so a fork partner keeps its copy before exec wipes this
        // process's pages.
        asm.Call("resolve_cow");
        // Release the old image's physical frames (its memory is being replaced); the new
        // image faults its pages in fresh after the page table is reseeded on resume.
        asm.Call("release_frames");
        // Zero this process's swap slots so the new image's data pages start blank rather
        // than inheriting the old image's swapped-out data.
        asm.Call("zero_swap_slots");
        // resolve_cow clobbered EBX; reload it = this (current) process's entry.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(EBX), R(EAX));
        EntryAddress(asm, EBX, EBX);

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

    // ===== ForkCopyField (helper) ============================================
    // Copies one 4-byte field from the source entry to the destination entry.
    private static void ForkCopyField(Assembler asm, byte srcEntry, byte dstEntry, int fieldOffset)
    {
        LoadField(asm, srcEntry, fieldOffset, R10);
        StoreFieldReg(asm, dstEntry, fieldOffset, R10);
    }

    // ===== EmitBuddyFree =====================================================
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

    // ===== EmitComputeBitAddress (helper) ====================================
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

    // ===== EmitReadBit =======================================================
    // ReadBit: ZF set if bit(nodeReg) == 0. Clobbers EAX, EBP, R14, R15.
    private static void EmitReadBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.And(R(R14), R(R15));                           // R14 &= mask; ZF set if bit=0
    }

    // ===== EmitSetBit ========================================================
    // SetBit: set bit(nodeReg) = 1 (mark free). Clobbers EAX, EBP, R14, R15.
    private static void EmitSetBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.Or(R(R14), R(R15));                            // R14 |= mask (set bit)
        asm.Store(R(EBP), R(R14));
    }

    // ===== EmitClearBit ======================================================
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

    // ===== EmitWakeEntry =====================================================
    private static void EmitWakeEntry(Assembler asm, int reason)
    {
        asm.MovImm(R(EBP), reason);
        asm.Jmp("wk_body");
    }

    // ===== EmitWakeBody ======================================================
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
        asm.Jz("wk_do_wake");
        // Also wake processes blocked on StringInput when this is the Input wake path:
        // if EDX == Input AND WaitReason == StringInput → wake.
        asm.MovImm(R(R8), WaitInput);
        asm.Cmp(R(EDX), R(R8));
        asm.Jnz("wk_resume");
        asm.MovImm(R(R8), WaitStringInput);
        asm.Cmp(R(EAX), R(R8));
        asm.Jnz("wk_resume");
        asm.Label("wk_do_wake");

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

    // ===== EmitResumeMlfq ====================================================
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

        // Skip job-control-stopped processes even when Ready (Shell §2.5 JC-C): a Stopped process is
        // not schedulable until SIGCONT clears the flag. Keeping Stopped orthogonal to State lets a
        // Blocked-then-stopped process keep its wait state (its wake still fires underneath the flag).
        LoadField(asm, R11, Hardware.ProcessEntryStopped, R13);
        asm.MovImm(R(R14), 0);
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

    // ===== Emit Helpers (R, Imm16, EntryAddress, LoadField, StoreField, etc.) =
    private static RegisterName R(byte index) { return (RegisterName)index; }

    // Spill a register to / reload it from a fixed OsLayout scratch word (EBP is the address
    // scratch). Used by the directory routines to survive register-clobbering cache calls.
    private static void SpillStore(Assembler asm, int address, byte srcReg)
    {
        Imm16(asm, EBP, address);
        asm.Store(R(EBP), R(srcReg));
    }

    private static void SpillLoad(Assembler asm, int address, byte dstReg)
    {
        Imm16(asm, EBP, address);
        asm.Load(R(dstReg), R(EBP));
    }

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

    // dest = CacheSlotTableBase + indexReg * CacheSlotSize (absolute address of a cache slot).
    // Clobbers EAX; dest must not be EAX. (CacheSlotSize/Base exceed 8 bits, so MovImm16.)
    private static void EmitCacheSlotBase(Assembler asm, byte indexReg, byte dest)
    {
        asm.Mov(R(dest), R(indexReg));
        asm.MovImm16(R(EAX), OsLayout.CacheSlotSize);
        asm.Mul(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.CacheSlotTableBase);
        asm.Add(R(dest), R(EAX));
    }

    // Bumps the global cache LRU clock and stamps the slot at slotBase with the new value,
    // so the most-recently-accessed slot always carries the highest stamp. Clobbers EAX,
    // EBP, R15; slotBase is preserved.
    private static void EmitStampSlot(Assembler asm, byte slotBase)
    {
        Imm16(asm, EBP, OsLayout.CacheClockOffset);
        asm.Load(R(R15), R(EBP));
        asm.Inc(R(R15));
        asm.Store(R(EBP), R(R15));               // clock++
        asm.Mov(R(EBP), R(slotBase));
        asm.MovImm(R(EAX), OsLayout.CacheStampField);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(R15));               // slot.stamp = clock
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
