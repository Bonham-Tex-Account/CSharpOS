using System.Collections.Concurrent;

namespace CSharpOS;

/// <summary>
/// The emulated machine: memory, the register file, the instruction-execution loop,
/// privilege levels, the trap table, I/O devices, and the interrupt queue. It drives
/// the OS by dispatching its routines through the interrupt vector table (entering
/// Privileged mode) rather than calling OS methods directly, and exposes a stream of
/// observability events (instruction executed, context switch, privilege change, …)
/// for the visualizer. The layout-related public constants describe the process-table
/// entry format and IVT slots shared with the ISA OS code.
/// </summary>
public partial class Hardware
{
    // ---- public constants ------------------------------------------------
    // The per-process kernel stack region. It holds the syscall trap frame (the saved
    // user register file + trap info = KernelHeaderSize bytes) at its base, with a small
    // handler stack above. Sized to fit the frame; the user stack top is still
    // TotalSize - KernelStackSize, so the ISA spawn/exec ESP formula stays unchanged.
    public const int KernelStackSize = KernelHeaderSize + 64;
    public const int KernelSaveAreaOffset = 0;
    public const int KernelTrapInfoOffset = 96;
    public const int KernelTrapInfoSize = 16;
    public const int KernelHeaderSize = KernelTrapInfoOffset + KernelTrapInfoSize;

    // ---- process-table entry layout --------------------------------------
    // An entry in the OS process table (held in OS memory). The first 96 bytes
    // mirror the register file (24 registers × 4 bytes; the EIP slot holds the
    // saved instruction pointer); the remaining fields hold the saved privilege
    // level, schedule state, and the sizing data the hardware needs to rebuild
    // the process's memory layout.
    public const int ProcessEntryRegisterFile      = 0;
    public const int ProcessEntryLevel             = 96;
    public const int ProcessEntryState             = 100;
    public const int ProcessEntryWaitReason        = 104;
    public const int ProcessEntryProgramAddress    = 108;
    public const int ProcessEntryProgramSize       = 112;
    public const int ProcessEntryRequiredMemory    = 116;
    public const int ProcessEntryRequiredStackSize = 120;
    // Total bytes the process occupies (program + kernel section + memory + stacks);
    // the OS allocator first-fits this size and Halt returns it to the free list.
    public const int ProcessEntryTotalSize         = 124;
    public const int ProcessEntryPriority           = 128;  // MLFQ queue level (0 = highest)
    public const int ProcessEntryTicksUsed          = 132;  // hardware ticks used in current level
    // Process identity (the spawning effort): Pid is a monotonic id decoupled from the
    // slot index; ParentPid/WaitTarget/ExitStatus support fork/wait/zombie reaping.
    public const int ProcessEntryPid                = 136;
    public const int ProcessEntryParentPid          = 140;
    public const int ProcessEntryWaitTarget         = 144;  // PID being waited on, else -1
    public const int ProcessEntryExitStatus         = 148;
    // Disk slot holding this process's program image; the load routine DREADs it into
    // RAM at the allocated ProgramAddress. Sits at 152 so 136-148 stay free for the
    // spawning effort's PID group.
    public const int ProcessEntryDiskSlot           = 152;
    // Filesystem first block of this process's program image (Phase 4: boot-from-FS). When
    // >= 0 the process is FS-backed and IvtSpawn chain-loads the image via fs_load_image
    // instead of DREADing a disk slot; -1 marks a slot-backed process (DiskSlot >= 0). Fork
    // copies it; EXEC(slot) resets it to -1, exec-by-path sets it to the new file's block.
    public const int ProcessEntryFirstBlock         = 156;
    // Per-process file-descriptor table: FdCount handles (0 = stdin, 1 = stdout, ...),
    // each a 4-byte device id. IN/OUT resolve the fd's device rather than assuming
    // device == process index. Bytes 136-159 stay free for later efforts (disk slot,
    // PID group); the fd table sits at 160 so those stay untouched. Widened from 4 to 8
    // handles for the filesystem effort so a process can hold several open files at once
    // (fds 2-7) alongside stdin/stdout; the table is the last entry field, so widening it
    // only grows ProcessEntrySize (no other field moves) and shifts OsLayout downstream.
    public const int ProcessEntryFdTable            = 160;
    public const int FdCount                        = 8;
    public const int StdIn                          = 0;
    public const int StdOut                         = 1;
    // Job control (Shell §2.5). Both reserved up front (JC-A) so the entry grows exactly once.
    // Stopped: 0 = runnable, 1 = job-control-stopped (an orthogonal flag beside ProcessState, so a
    // Blocked process can be stopped without losing its wait state). Used from JC-C. SigHandler:
    // reserved virtual address of a future catchable-signal handler; unused until JC-E (sigaction).
    public const int ProcessEntryStopped            = 192;
    public const int ProcessEntrySigHandler         = 196;
    public const int ProcessEntrySize               = 200;  // 160 + FdCount*4 + Stopped + SigHandler

    // ---- device ids ------------------------------------------------------
    // Character device ids 0..MaxProcesses-1 are the per-process I/O devices (the
    // device == process-index shim, until the focus effort rebinds them). Block
    // devices live above that range; the disk effort registers one at DiskDeviceId.
    public const int DiskDeviceId = 256;
    // Default disk geometry when no Bin is supplied to the constructor: 64 slots of
    // 1 KiB each — generous enough that the many load sites and multi-load tests
    // never exhaust slots.
    public const int DefaultDiskSlots    = 64;
    // 2 KiB each: raised from 1024 in Shell §2.5 (JC-D) once the shell — with its job-control
    // builtins — grew past 1 KiB. Program images are staged through a disk slot before FS install,
    // so a slot must hold the whole image. Swap slots (256-byte pages) sit in the same Bin and are
    // unaffected by the larger size.
    public const int DefaultDiskSlotSize = 2048;
    // The filesystem's raw block store, carried in the same Bin as the image/swap slots
    // but block-addressed (see Bin.ReadFileBlock). Block size matches the paging PageSize
    // (256) by convention so the future RAM cache can hold one block per page frame; kept
    // as an independent constant here to avoid a Hardware→OsLayout dependency. The count is
    // configurable — start large enough for early filesystem work; the cache is sized as a
    // fraction of it. 256 blocks × 256 bytes = 64 KiB of file space.
    public const int DefaultFileBlockSize  = 256;
    public const int DefaultFileBlockCount = 256;

    // ---- interrupt vector table -----------------------------------------
    // One 4-byte slot per OS routine at the front of the OS region. Hardware reads
    // the slot for an event and jumps to that routine in Privileged mode, instead
    // of calling an OS method directly.
    public const int IvtContextSwitch      = 0;
    public const int IvtHalt               = 1;
    public const int IvtInvalidInstruction = 2;
    public const int IvtWakeInput          = 3;
    public const int IvtWakeOutput         = 4;
    public const int IvtBlockInput         = 5;
    public const int IvtBlockOutput        = 6;
    public const int IvtSchedule           = 7;
    // IvtAllocate reserves a memory block for a staged process entry (pure, disk-free). It is
    // kept as the buddy allocator's isolation-test entry point; production loads go through
    // IvtSpawn (which allocs + DREADs the image inline). Program bytes are moved by IvtSpawn/
    // IvtExec (DREAD from a disk slot) or fs_exec_core (from the filesystem), never a
    // standalone slot — the old IvtDiskLoad routine was removed (had no dispatch sites).
    public const int IvtAllocate           = 8;
    // Process-spawning routines (fork/exec/wait runtime creation, spawn boot creation).
    public const int IvtFork               = 9;
    public const int IvtExec               = 10;
    public const int IvtWait               = 11;
    public const int IvtSpawn              = 12;
    // The shared syscall (IN/OUT) handler's entry address. Unlike the dispatched slots,
    // EnterKernel jumps here directly without masking interrupts, so the handler stays
    // preemptible; only its address is stored here (wired up by BuildOsImage).
    public const int IvtSyscall            = 13;
    // Demand-paging fault handler (Phase 2): dispatched by the MMU when a user data
    // access touches a non-resident page; makes the page resident, then resumes.
    public const int IvtPageFault          = 14;
    public const int IvtWakeKey            = 15;
    // Filesystem buffer-cache control interface (Increment 2). Dispatched with an op
    // selector in EAX and a block number in EBX; routes to the cache manager subroutines
    // (get/dirty/write-through/pin/unpin/discard/flush) and parks the result in the cache
    // header's CacheResult word. Kernel-internal; also the direct test entry point.
    public const int IvtCacheOp            = 16;
    // Filesystem control interface (Increment 3+). Dispatched with an op selector in EAX and
    // arguments in EBX/ECX; routes to the block allocator / free-chaining (and, later, the
    // directory tree). Result parked in OsLayout.FsResult. One slot grows across increments.
    public const int IvtFsOp               = 17;
    // User filesystem syscall (Increment 5). FSYS dispatches this atomic routine (like FORK
    // dispatches IvtFork); it reads the syscall number in EAX and args in EBX/ECX/EDX from
    // the trapped user registers, performs the op, and resumes the caller with the result in
    // EAX. File ops don't block, so it runs atomically rather than through the preemptible
    // IvtSyscall path (whose shared privileged stack couldn't survive preemption mid-op).
    public const int IvtFsSyscall          = 18;
    // Ensures one virtual page of the CURRENT process is resident (faulting it in if needed)
    // and, if EnsureUserPageIsWrite is set, not copy-on-write (resolving it if needed). Entered
    // with the page number in EAX; the isWrite flag is read from OsLayout scratch (set by the
    // C# caller before dispatch, since DispatchOsRoutine only carries one EAX argument).
    // Result (0 = ok, -1 = unmapped/bad page) is parked in OsLayout.EnsureUserPageResult.
    // Dispatched only via RunOsRoutineSynchronously from Hardware.UserToPhysical (Phase 3
    // rectification) — never by ISA code, which calls the underlying ensure_user_page
    // subroutine directly via CALL/RET instead.
    public const int IvtEnsureUserPage     = 19;
    // Non-blocking reap (Shell §2.5 job control, JC-A). REAP dispatches this atomic routine (like
    // FORK dispatches IvtFork); it reaps a dead child — targeted (EAX = pid) or any (EAX = 0) — and
    // resumes the caller with the reaped pid in EAX (0 if none dead) and its exit status in EDX. It
    // never blocks, so it runs atomically rather than through the preemptible IvtSyscall path.
    public const int IvtReap               = 20;
    // Signal delivery to an arbitrary process (Shell §2.5 job control, JC-B). KILL dispatches this
    // atomic routine (like FORK); the target pid is in EAX and the signal number in OsLayout.KillSig
    // (DispatchOsRoutine carries only one EAX arg). SigTerm/SigKill tear the target down via the same
    // teardown as exit_body (freeing memory, waking a wait()ing parent, zombie/orphan handling);
    // SigStop/SigCont set/clear the target's job-control stop flag (wired in JC-C). Resumes the caller
    // with 0 (delivered) or -1 (no such pid) in EAX; if the caller killed itself, reschedules instead.
    public const int IvtKill               = 21;
    public const int IvtSlotCount          = 22;
    public const int IvtSize               = IvtSlotCount * 4;

    // KILL signal numbers (Shell §2.5). SigTerm/SigKill both run the kernel teardown (no catchable
    // handlers this pass — that is the reserved SIGACTION/JC-E work). SigStop/SigCont are the
    // job-control stop/continue signals, applied in JC-C.
    public const int SigTerm = 1;
    public const int SigKill = 2;
    public const int SigStop = 3;
    public const int SigCont = 4;

    // IvtCacheOp op selectors (passed in EAX; block number in EBX). The dispatcher parks
    // each op's result (cache_get returns the block's cached data address; the rest return 0)
    // in the cache header's CacheResult word, since RunOsRoutineSynchronously restores
    // registers and a test cannot read a routine's register result directly.
    public const int CacheOpGet          = 0; // ensure block resident → data address
    public const int CacheOpDirty        = 1; // mark resident block dirty (write-back later)
    public const int CacheOpWriteThrough = 2; // flush block to disk now, leave clean
    public const int CacheOpPin          = 3; // pin (never evict while pinned)
    public const int CacheOpUnpin        = 4; // release one pin
    public const int CacheOpDiscard      = 5; // drop block: clear valid+dirty (no write-back)
    public const int CacheOpFlush        = 6; // write back all dirty unpinned slots

    // IvtFsOp op selectors (EAX). Block arg in EBX; ChainSetNext's next arg in ECX.
    // Results (alloc's block number, ChainNext's pointer) are parked in OsLayout.FsResult.
    public const int FsOpFormat       = 0; // write initial superblock + empty bitmap (blocks 0,1 used)
    public const int FsOpAllocBlock   = 1; // claim a free block → block number, or -1 if full
    public const int FsOpFreeBlock    = 2; // release block EBX (clear bitmap bit, discard from cache)
    public const int FsOpChainNext    = 3; // read block EBX's next-block link → pointer
    public const int FsOpChainSetNext = 4; // set block EBX's next-block link to ECX
    // Directory ops (Inc 4). Name args are addresses of a word-per-char, null-padded buffer.
    public const int FsOpRootDir      = 5; // → root directory block (from superblock)
    public const int FsOpHash         = 6; // hash the name at EBX → hash value
    public const int FsOpLookup       = 7; // find name ECX in directory EBX → entry addr, or -1
    public const int FsOpInsert       = 8; // add (name ECX, type EDX, firstBlock ESI) to dir EBX → entry addr, or -1 (dup/full)
    public const int FsOpRemove       = 9; // remove name ECX from directory EBX → 0, or -1 if absent
    // Nested directories (Inc 4b).
    public const int FsOpMkdir        = 10; // create subdirectory (name ECX) in dir EBX → new dir block, or -1
    public const int FsOpPathResolve  = 11; // resolve path at EBX (e.g. "/a/b/c") → entry addr, or -1
    // File-syscall cores (Inc 5), reached directly for testing (the FSYS wrapper below calls
    // the same routines). Paths here are ABSOLUTE addresses; the wrapper translates the user
    // pointer first. EDX carries the owning process index so an fd is allocated in its table.
    public const int FsOpOpen         = 12; // open/create absolute path EBX (flags ECX) for proc EDX → fd, or -1
    public const int FsOpClose        = 13; // close fd EBX for proc ECX → 0, or -1
    public const int FsOpRead         = 14; // read fd EBX into abs buf ECX, count EDX, proc ESI → chars read, or -1
    public const int FsOpWrite        = 15; // write count EDX chars from abs buf ECX to fd EBX, proc ESI → chars written, or -1
    // Directory maintenance by absolute path (Phase 1 rectification).
    public const int FsOpUnlink       = 16; // delete the file at abs path EBX (frees its whole block chain) → 0, or -1
    public const int FsOpMkdirPath    = 17; // create a directory at abs path EBX → new dir block, or -1
    public const int FsOpReadDir      = 18; // copy dir EBX's ECX-th in-use entry (64 bytes) into abs buf EDX → entry type, or -1 past end

    // FSYS syscall numbers (EAX) and flags.
    public const int FsysOpen   = 0; // EBX=path ptr, ECX=flags → fd, or -1
    public const int FsysRead   = 1; // EBX=fd, ECX=buf ptr, EDX=len → bytes read (Inc 5b)
    public const int FsysWrite  = 2; // EBX=fd, ECX=buf ptr, EDX=len → bytes written (Inc 5b)
    public const int FsysClose  = 3; // EBX=fd → 0, or -1
    public const int FsysExec   = 4; // EBX=path ptr → replace the running image with the FS file (Inc 6); no return on success, -1 on failure
    public const int FsysUnlink = 5; // EBX=path ptr → 0, or -1 (Phase 1)
    public const int FsysMkdir  = 6; // EBX=path ptr → 0, or -1 (Phase 1)
    public const int FsysReaddir= 7; // EBX=dir path ptr, ECX=index, EDX=out ptr → entry type, or -1 past end (Phase 1)
    public const int FsysCreateFlag = 1; // OPEN: create the file if it does not exist

    // ---- raw keycode constants (for INK / INPOLL) -------------------------
    // Printable ASCII (32–126) is delivered as-is. Special keys use values above
    // the ASCII range so programs can distinguish them from printable characters.
    public const int KeyUp    = 256;
    public const int KeyDown  = 257;
    public const int KeyLeft  = 258;
    public const int KeyRight = 259;

    // ---- public events ---------------------------------------------------
    public event EventHandler<InstructionExecutedArgs>? InstructionExecuted;
    public event EventHandler<MemoryWrittenArgs>? MemoryWritten;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;
    public event EventHandler<ProgramOutputArgs>? ProgramOutput;
    // Fired when an OS routine resumes a different process than was last running.
    public event EventHandler<ContextSwitchArgs>? ContextSwitched;
    // Fired on real privilege-level transitions during execution (syscall trap,
    // OS-routine dispatch, return to a process); see PrivilegeChangedArgs.
    public event EventHandler<PrivilegeChangedArgs>? PrivilegeChanged;
    // Fired when the running process blocks on I/O and the OS switches away.
    public event EventHandler<ProcessBlockedArgs>? ProcessBlocked;
    // Fired when a device interrupt is delivered and a waiter is woken.
    public event EventHandler<ProcessWokenArgs>? ProcessWoken;
    // Fired when an OS routine is dispatched through the IVT, naming which one.
    public event EventHandler<OsRoutineArgs>? OsRoutineEntered;
    // Fired when a process is torn down (HLT or invalid-instruction fault), so a
    // host can release per-process resources (e.g. close its terminal window).
    public event EventHandler<ProcessTerminatedArgs>? ProcessTerminated;
    // Fired after a user-mode conditional branch is scored by the branch predictor.
    public event EventHandler<BranchPredictedArgs>? BranchPredicted;

    // ---- private constants -----------------------------------------------
    private const int SchedulerInstructionCount = 30;

    // Observational cycles added when the branch predictor mispredicts a user branch
    // (models the pipeline-flush cost). The cycle counter is observational only; it
    // does not drive the MLFQ quantum (which stays instruction-count based).
    private const int MispredictPenalty = 3;

    // ---- private fields --------------------------------------------------
    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;

    private int instructionCount;
    private int instructionPointer;
    private PrivilegeLevel level;

    // Hardware interrupt-enable flag (the real CPU's IF). When false, the run loop
    // neither preempts at the quantum nor dispatches pending interrupts, so the code
    // runs atomically. Cleared on OS-routine (IVT) entry and restored on OSRET/IRET;
    // a syscall trap into the kernel leaves it set, so the syscall handler stays
    // preemptible. Default true (user/kernel code is interruptible). Because atomic
    // OS routines never nest or get preempted, every resumable saved context is
    // interruptible, so this flag needs no per-process storage.
    private bool interruptsEnabled = true;
    private bool trapTaken;

    // Suppresses observability events (PrivilegeChanged, OsRoutineEntered) while an
    // OS routine is run synchronously (the C#-initiated allocation path), whose
    // transitions are bookkeeping, not part of the visible execution timeline.
    private bool suppressOsEvents;

    // Branch predictor (2-bit BHT) + observational cycle counter. The predictor scores
    // only user-program branches (see RecordBranch); cycles count executed non-atomic
    // instructions plus misprediction penalties. currentInstructionAddress is the
    // address of the instruction being executed, so a branch handler can index the BHT.
    private BranchPredictor predictor = new BranchPredictor();
    private long cycles;
    private int currentInstructionAddress;

    // Per-process-slot record of the (programAddress, userExtent) the page table was last
    // seeded for, so SeedPageTableIfNew reseeds only when a slot is reused for a new
    // process (not on every resume, which would evict all resident pages).
    private int[] pageSeedBase = NewFilled(OsLayout.MaxProcesses, -1);
    private int[] pageSeedExtent = NewFilled(OsLayout.MaxProcesses, -1);

    // Global monotonically increasing access counter the MMU stamps into a frame's
    // LastUse on every resident access (the reference clock for LRU eviction). The ISA
    // page-fault handler evicts the resident frame with the smallest stamp.
    private int pageClock;

    private static int[] NewFilled(int count, int value)
    {
        int[] array = new int[count];
        for (int i = 0; i < count; i++)
        {
            array[i] = value;
        }
        return array;
    }

    // The interrupted process's register file (incl. saved IP in the EIP slot),
    // snapshotted when an OS routine is dispatched so the routine can clobber the
    // live registers as scratch; SAVEREGS persists this frame to a process entry.
    private byte[] trapFrame = Array.Empty<byte>();

    // Staged by LOADREGS and committed by OSRET, so an OS routine can keep using the
    // live registers right up to the atomic return into the next process.
    private byte[]? pendingContext;

    // The privilege level the interrupted process was running at, captured when an
    // OS routine is dispatched (which raises the CPU to Privileged). SAVEREGS folds
    // it into the saved entry so a process resumes at its true level.
    private PrivilegeLevel interruptedLevel;

    // Whether a process is currently scheduled. Set by OSRET: true when it commits a
    // process context, false when an OS routine returns to idle (no context staged).
    // Lets Run detect the idle state without reaching into the OS data structures.
    private bool processRunning;

    // Program base of the process last resumed by OSRET; used to fire ContextSwitched
    // only when the resumed process actually changes.
    private int lastContextBase = -1;

    // The foreground (focused) process: the one bound to the live keyboard and screen.
    // Keyboard input (RaiseInputInterrupt with no device) goes to this process's stdin
    // device; the host switches focus with Tab. -1 means no process is focused, in
    // which case keyboard input falls back to device 0 (the bare-harness default).
    private int activeProcess = -1;

    private int currentProcessMemoryStart;
    private int currentProcessMemorySize;
    private int currentProcessStackStart;
    private int currentProcessStackSize;
    private int currentProcessKernelStackStart;
    private int currentProcessKernelStackSize;
    private int currentProcessInstructionStart;
    private int currentProcessInstructionSize;

    private Dictionary<byte, List<Trap>> trapTable = new Dictionary<byte, List<Trap>>();

    // The OS's reserved memory region at the front of the address space: an IVT
    // followed by OS code and data. Zero-sized when the OS keeps no in-memory image.
    private int osMemoryBase;
    private int osMemorySize;

    // First-class I/O devices, keyed by device id. Each character device owns an
    // input buffer, a wait queue (process indices blocked on its input), and an
    // output-busy flag. Device ids are independent of processes; the device ==
    // process-index mapping survives only as a default via fd tables / CurrentDeviceId.
    private readonly Dictionary<int, Device> devices = new Dictionary<int, Device>();
    private readonly ConcurrentQueue<Interrupt> pendingInterrupts = new ConcurrentQueue<Interrupt>();

    // ---- constructor -----------------------------------------------------
    // Convenience constructor: gives the machine a default-sized disk. The disk holds the
    // image slots [0, DefaultDiskSlots) plus the paging swap region above them, so a
    // page's deterministic swap slot (OsLayout.SwapSlot) is always in range.
    public Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os)
        : this(memorySize, registerNames, os, new Bin(DefaultDiskSlots + OsLayout.SwapSlotCount, DefaultDiskSlotSize, DefaultFileBlockCount, DefaultFileBlockSize))
    {
    }

    public Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os, Bin disk)
    {
        memory = new byte[memorySize];
        registers = new byte[registerNames.Length * 4];
        registerIndex = new Dictionary<RegisterName, int>();
        for (int i = 0; i < registerNames.Length; i++)
        {
            registerIndex[registerNames[i]] = i * 4;
        }
        this.os = os;
        instructionCount = 0;
        // Register the disk as a block device before the OS attaches, so the load
        // path (which runs during AttachHardware-driven seeding) can reach it.
        RegisterDevice(new Device(DiskDeviceId, disk));
        os.AttachHardware(this);
    }

    // ---- accessor methods ------------------------------------------------
    public int GetMemorySize() { return memory.Length; }
    public int GetRegisterFileSize() { return registers.Length; }
    public int GetRegisterOffset(RegisterName name) { return registerIndex[name]; }
    public int GetInstructionPointer() { return instructionPointer; }
    public void SetInstructionPointer(int address) { instructionPointer = address; }
    public PrivilegeLevel GetPrivilegeLevel() { return level; }
    public void SetPrivilegeLevel(PrivilegeLevel value) { level = value; }
    /// <summary>Whether maskable interrupts are enabled (the CPU's IF flag). False while an atomic OS routine runs.</summary>
    public bool InterruptsEnabled() { return interruptsEnabled; }
    /// <summary>Low-level test seam: forces the interrupt-enable flag, to simulate being inside (false) or outside (true) an atomic OS routine.</summary>
    public void SetInterruptsEnabled(bool value) { interruptsEnabled = value; }

    // The foreground process, which owns the live keyboard and screen. The host sets
    // this (e.g. Tab to cycle focus); -1 means none.
    public void SetActiveProcess(int index) { activeProcess = index; }
    public int GetActiveProcess() { return activeProcess; }

    // Changes the privilege level, firing PrivilegeChanged on a real transition.
    // Used by the execution-time transition points (dispatch, syscall, return) so
    // they are observable; the test accessor and synchronous-run restore set the
    // field directly to stay silent.
    private void SetLevel(PrivilegeLevel newLevel)
    {
        if (newLevel == level)
        {
            return;
        }
        PrivilegeLevel from = level;
        level = newLevel;
        if (!suppressOsEvents)
        {
            PrivilegeChanged?.Invoke(this, new PrivilegeChangedArgs { From = from, To = newLevel });
        }
    }

    // Reserves the OS region at the front of memory (base 0). Processes are then
    // allocated above it, so user/kernel code can never address into the OS image.
    public void ReserveOsMemory(int size)
    {
        osMemoryBase = 0;
        osMemorySize = size;
    }

    public int GetOsMemoryBase() { return osMemoryBase; }
    public int GetOsMemorySize() { return osMemorySize; }

    // ===== IVT Dispatch (DispatchOsRoutine, EnterOsRoutine) ==================
    // Enters an OS routine via its IVT slot: snapshots the interrupted process's
    // context, raises to Privileged, and jumps to the routine. The routine returns
    // to a process with OSRET. Replaces direct os.X(hw) method calls.
    public void DispatchOsRoutine(int slot)
    {
        EnterOsRoutine(slot);
    }

    // Same as DispatchOsRoutine, but passes a parameter to the routine in EAX (after
    // the interrupted registers are safely snapshotted). Used for routines that take
    // an argument: a wait reason (Wake/Block) or a faulting opcode / pending entry.
    public void DispatchOsRoutine(int slot, int eaxArgument)
    {
        EnterOsRoutine(slot);
        WriteRegisterAt(0, eaxArgument); // EAX is register index 0
    }

    // Human-readable name for an IVT slot, for the OsRoutineEntered event.
    private static string NameForRoutineSlot(int slot)
    {
        switch (slot)
        {
            case IvtContextSwitch:      return "ContextSwitch";
            case IvtHalt:               return "Halt";
            case IvtInvalidInstruction: return "InvalidInstruction";
            case IvtWakeInput:          return "Wake (input)";
            case IvtWakeOutput:         return "Wake (output)";
            case IvtBlockInput:         return "Block (input)";
            case IvtBlockOutput:        return "Block (output)";
            case IvtSchedule:           return "Schedule";
            case IvtAllocate:           return "Allocate";
            case IvtFork:               return "Fork";
            case IvtExec:               return "Exec";
            case IvtWait:               return "Wait";
            case IvtSpawn:              return "Spawn";
            case IvtPageFault:          return "PageFault";
            case IvtCacheOp:            return "CacheOp";
            case IvtFsOp:               return "FsOp";
            case IvtFsSyscall:          return "FsSyscall";
            case IvtEnsureUserPage:     return "EnsureUserPage";
            case IvtReap:               return "Reap";
            case IvtKill:               return "Kill";
            default:                    return $"slot {slot}";
        }
    }

    private void EnterOsRoutine(int slot)
    {
        CaptureInterruptedContext();
        int routineAddress = ReadWord(osMemoryBase + slot * 4);
        if (!suppressOsEvents)
        {
            OsRoutineEntered?.Invoke(this, new OsRoutineArgs { Slot = slot, Name = NameForRoutineSlot(slot) });
        }
        // Mask interrupts BEFORE SetLevel so a PrivilegeChanged observer can distinguish an
        // atomic OS-routine dispatch (interrupts masked) from a syscall trap (still enabled).
        interruptsEnabled = false; // OS routines run atomically: no preemption, no interrupt dispatch
        SetLevel(PrivilegeLevel.Kernel);
        instructionPointer = routineAddress;
        trapTaken = true;
    }

    // ===== Traps (LoadTraps, EvaluateTraps) ==================================
    public void LoadTraps(List<Trap> traps)
    {
        trapTable = new Dictionary<byte, List<Trap>>();
        foreach (Trap trap in traps)
        {
            if (!trapTable.ContainsKey(trap.Opcode))
            {
                trapTable[trap.Opcode] = new List<Trap>();
            }
            trapTable[trap.Opcode].Add(trap);
        }
    }

    // Evaluates OS-defined traps for the given opcode. If a matching trap's
    // condition fires, calls TrapInvalidInstruction and returns true.
    public bool EvaluateTraps(byte opcode, byte b1, byte b2, byte b3)
    {
        if (!trapTable.TryGetValue(opcode, out List<Trap>? traps))
        {
            return false;
        }
        foreach (Trap trap in traps)
        {
            if (trap.Condition != null && trap.Condition(this, b1, b2, b3))
            {
                TrapInvalidInstruction(opcode, b1, b2, b3);
                return true;
            }
        }
        return false;
    }

    // ===== Program Base (GetProgramBase, GetProgramBaseFor) ==================
    // Program-relative addressing follows the privilege level: user code runs relative
    // to its program image, while kernel code (the shared syscall handler and the OS
    // routines) addresses memory absolutely (base 0) so it can reach the OS region and
    // any process's memory.
    public int GetProgramBase()
    {
        return GetProgramBaseFor(level);
    }

    // The program base for a specific privilege level (independent of the live level),
    // so OsReturn can resolve a relative saved EIP for the level it is returning to
    // before that level becomes current.
    private int GetProgramBaseFor(PrivilegeLevel forLevel)
    {
        if (forLevel == PrivilegeLevel.User)
        {
            return currentProcessInstructionStart;
        }
        // Kernel addresses the OS region absolutely (base 0): the syscall handler is
        // shared OS code and the OS routines live in the OS region.
        return 0;
    }

    // ===== Memory + Register Access (ReadBytes, WriteBytes, ReadRegisterAt...) =
    public byte[] ReadBytes(int address)
    {
        return new byte[] { memory[address], memory[address + 1], memory[address + 2], memory[address + 3] };
    }

    public void WriteBytes(int address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            memory[address + i] = data[i];
        }
        MemoryWritten?.Invoke(this, new MemoryWrittenArgs { Address = address, Data = (byte[])data.Clone() });
    }

    public byte[] ReadRegisters() { return registers; }

    public void WriteRegisters(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            registers[i] = data[i];
        }
    }

    public int ReadRegisterAt(byte index)
    {
        int offset = index * 4;
        return registers[offset] | (registers[offset + 1] << 8) | (registers[offset + 2] << 16) | (registers[offset + 3] << 24);
    }

    public void WriteRegisterAt(byte index, int value)
    {
        int offset = index * 4;
        registers[offset]     = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8)  & 0xFF);
        registers[offset + 2] = (byte)((value >> 16) & 0xFF);
        registers[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public int ReadRegister(RegisterName name)
    {
        int offset = registerIndex[name];
        return registers[offset] | (registers[offset + 1] << 8) | (registers[offset + 2] << 16) | (registers[offset + 3] << 24);
    }

    public void WriteRegister(RegisterName name, int value)
    {
        int offset = registerIndex[name];
        registers[offset]     = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8)  & 0xFF);
        registers[offset + 2] = (byte)((value >> 16) & 0xFF);
        registers[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    // Reads a full register-file-sized block from memory (ReadBytes only returns
    // 4 bytes, so a separate method is needed to restore a saved register state).
    public byte[] ReadRegisterState(int address)
    {
        byte[] state = new byte[registers.Length];
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = memory[address + i];
        }
        return state;
    }

    // (Process memory-range queries were removed with the bounds traps: the MMU is now the
    // sole memory-protection mechanism — see TryTranslateData / RaiseProtectionFault.)

    // ---- helper functions ------------------------------------------------
    public void Output(int value)
    {
        Output(value, CurrentDeviceId());
    }

    public void Output(int value, int device)
    {
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = value, Device = device, SourceProcess = CurrentDeviceId() });
    }

    // The device id of the running process: its process-table index, since each
    // process owns one terminal device. Falls back to device 0 with no OS image
    // (bare-hardware harness) or when idle.
    private int CurrentDeviceId()
    {
        if (!OsManaged)
        {
            return 0;
        }
        int index = ReadWord(osMemoryBase + OsLayout.CurrentIndexOffset);
        if (index < 0)
        {
            return 0;
        }
        return index;
    }

    // Returns the device with this id, creating a character device on first use
    // (mirroring the previous on-demand input-queue creation).
    private Device DeviceFor(int id)
    {
        if (!devices.TryGetValue(id, out Device? device))
        {
            device = new Device(id, DeviceType.Character);
            devices[id] = device;
        }
        return device;
    }

    // Registers a device explicitly (used by the disk effort to add a block device).
    public void RegisterDevice(Device device)
    {
        devices[device.Id] = device;
    }

    // Public accessor for a device (creating a character device on first use).
    public Device GetDevice(int id)
    {
        return DeviceFor(id);
    }

    // ===== Disk (DiskRead, DiskWrite, DiskLength, Disk property) =============
    // The disk's backing store: the Bin behind the block device at DiskDeviceId.
    public Bin Disk
    {
        get
        {
            Device device = DeviceFor(DiskDeviceId);
            if (device.Block == null)
            {
                throw new InvalidOperationException("No disk is registered at DiskDeviceId.");
            }
            return device.Block;
        }
    }

    // Block-device transfer: copies disk slot's contents into RAM at destAddr
    // (absolute) and returns the byte count. Backs the DREAD instruction.
    public int DiskRead(int destAddr, int slot)
    {
        byte[] data = Disk.Load(slot);
        WriteBytes(destAddr, data);
        return data.Length;
    }

    // Block-device transfer: copies len bytes of RAM from srcAddr (absolute) into a
    // disk slot. Backs the DWRITE instruction.
    public void DiskWrite(int slot, int srcAddr, int len)
    {
        byte[] buffer = new byte[len];
        for (int i = 0; i < len; i++)
        {
            buffer[i] = memory[srcAddr + i];
        }
        Disk.Store(slot, buffer);
    }

    // The byte length stored in a disk slot. Backs the DLEN instruction, which EXEC
    // uses to size the new image's allocation before reading it.
    public int DiskLength(int slot)
    {
        return Disk.GetLength(slot);
    }

    // Block-device transfer: copies a whole file block (FileBlockSize bytes) into RAM at
    // destAddr (absolute). Backs the FBREAD instruction. Unlike DiskRead the size is fixed,
    // so no length is returned; a never-written block reads back as zeros.
    public void FileBlockRead(int destAddr, int block)
    {
        byte[] data = Disk.ReadFileBlock(block);
        WriteBytes(destAddr, data);
    }

    // Block-device transfer: copies a whole file block (FileBlockSize bytes) of RAM from
    // srcAddr (absolute) into the file block. Backs the FBWRITE instruction.
    public void FileBlockWrite(int block, int srcAddr)
    {
        int size = Disk.FileBlockSize;
        byte[] buffer = new byte[size];
        for (int i = 0; i < size; i++)
        {
            buffer[i] = memory[srcAddr + i];
        }
        Disk.WriteFileBlock(block, buffer);
    }

    // ===== Device Internals (FdDevice, FocusedInputDevice, waiters) ==========
    // Resolves the running process's file descriptor to a device id. Falls back to
    // CurrentDeviceId() (the process index) with no OS image or no running process,
    // so the bare-hardware harness and the idle state behave exactly as before.
    private int FdDevice(int fd)
    {
        if (!OsManaged)
        {
            return CurrentDeviceId();
        }
        int index = ReadWord(osMemoryBase + OsLayout.CurrentIndexOffset);
        if (index < 0)
        {
            return CurrentDeviceId();
        }
        int entry = osMemoryBase + OsLayout.ProcessEntryAddress(index);
        return ReadWord(entry + ProcessEntryFdTable + fd * 4);
    }

    // The stdin device of the focused (foreground) process, where live keyboard input
    // is delivered. Falls back to device 0 when nothing is focused or there is no OS
    // image, matching the previous single-device behaviour.
    private int FocusedInputDevice()
    {
        if (!OsManaged || activeProcess < 0)
        {
            return 0;
        }
        int entry = osMemoryBase + OsLayout.ProcessEntryAddress(activeProcess);
        return ReadWord(entry + ProcessEntryFdTable + StdIn * 4);
    }

    // Records a process index as waiting on a device's input (no duplicates).
    private static void AddInputWaiter(Device device, int processIndex)
    {
        if (!device.Waiters.Contains(processIndex))
        {
            device.Waiters.Add(processIndex);
        }
    }

    // Removes and returns the first process waiting on this device's input. The caller
    // must ensure there is a waiter (device.Waiters is non-empty).
    private static int NextInputWaiter(Device device)
    {
        int processIndex = device.Waiters[0];
        device.Waiters.RemoveAt(0);
        return processIndex;
    }

    private static void AddStringInputWaiter(Device device, int processIndex)
    {
        if (!device.StringWaiters.Contains(processIndex))
        {
            device.StringWaiters.Add(processIndex);
        }
    }

    private static int NextStringInputWaiter(Device device)
    {
        int processIndex = device.StringWaiters[0];
        device.StringWaiters.RemoveAt(0);
        return processIndex;
    }

    private static void AddKeyWaiter(Device device, int processIndex)
    {
        if (!device.KeyWaiters.Contains(processIndex))
        {
            device.KeyWaiters.Add(processIndex);
        }
    }

    private static int NextKeyWaiter(Device device)
    {
        int processIndex = device.KeyWaiters[0];
        device.KeyWaiters.RemoveAt(0);
        return processIndex;
    }

    // ===== Word Memory Helpers (ReadWord, WriteWord, WriteWordRaw) ===========
    private int ReadWord(int address)
    {
        byte[] bytes = ReadBytes(address);
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }

    private void WriteWord(int address, int value)
    {
        WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8)  & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }

    // Writes a word directly into physical memory without firing MemoryWritten. Used to
    // seed page tables (OS bookkeeping, not part of the visible program memory timeline).
    private void WriteWordRaw(int address, int value)
    {
        memory[address]     = (byte)(value & 0xFF);
        memory[address + 1] = (byte)((value >> 8)  & 0xFF);
        memory[address + 2] = (byte)((value >> 16) & 0xFF);
        memory[address + 3] = (byte)((value >> 24) & 0xFF);
    }

    // ---- MMU: virtual-to-physical translation (paging) -------------------

    // Translates a user-mode data/stack virtual address through the running process's
    // page table and reports whether it succeeded. Kernel/OS code (and non-OS-managed
    // hardware) addresses memory absolutely, so it always succeeds via the program base.
    // A user access to a **non-resident** page raises a demand page fault (dispatching
    // IvtPageFault) and returns false — the caller must abort the instruction without
    // committing; the faulting instruction re-runs after the page is made resident.
    // `isWrite` is recorded for the dirty/LRU bookkeeping added in a later increment.
    public bool TryTranslateData(int virtualAddress, bool isWrite, out int physical)
    {
        if (level != PrivilegeLevel.User || !OsManaged)
        {
            physical = GetProgramBase() + virtualAddress;
            return true;
        }

        int index = ReadWord(osMemoryBase + OsLayout.CurrentIndexOffset);
        int page = virtualAddress / OsLayout.PageSize;
        int offset = virtualAddress % OsLayout.PageSize;
        // A page the table cannot describe (beyond the per-process page count) is outside the
        // address space: a protection fault that terminates the process. The MMU is the sole
        // memory-protection mechanism — there is no linear fallback and no bounds trap.
        if (index < 0 || index >= OsLayout.MaxProcesses || page < 0 || page >= OsLayout.MaxPagesPerProcess)
        {
            RaiseProtectionFault(page);
            physical = 0;
            return false;
        }

        int pte = ReadWord(osMemoryBase + OsLayout.PageTableAddress(index) + page * OsLayout.PageTableEntryBytes);
        if (pte <= OsLayout.NonResidentPage)
        {
            // Non-resident: a RAM-home page (-2) or a swap-backed data page (<= -3). The
            // ISA handler reads the PTE to decide how to fault it in (RAM copy vs DREAD).
            RaisePageFault(page);
            physical = 0;
            return false;
        }
        if (pte < 0)
        {
            // Unmapped page (-1): a virtual page beyond the process's mapped extent → an
            // out-of-bounds access. Terminate the process (protection fault).
            RaiseProtectionFault(page);
            physical = 0;
            return false;
        }
        // Resident: the PTE holds the page's frame base in the physical frame pool.
        int frameIndex = (pte - OsLayout.FramePoolBase) / OsLayout.PageSize;
        // A WRITE to a copy-on-write (read-only) frame traps: re-raise the fault so the ISA
        // handler copies the page privately for this process; the instruction then re-runs
        // and the write commits to the now-private frame. Reads of a COW frame pass through.
        if (isWrite && FrameIsCow(frameIndex))
        {
            RaisePageFault(page);
            physical = 0;
            return false;
        }
        // Bump the frame's LRU stamp (and set its dirty bit on a write) — the reference/dirty
        // bits a hardware MMU maintains; the ISA page-fault handler reads them when it must
        // evict a victim to free a frame.
        StampFrame(frameIndex, isWrite);
        physical = pte + offset;
        return true;
    }

    // Non-faulting query of a user virtual address against the running process's page
    // table (resident pages only); used by the visualizer/tests. Returns the linear
    // address for non-resident/unmapped pages without raising a fault.
    public int TranslateDataAddress(int virtualAddress)
    {
        if (level != PrivilegeLevel.User || !OsManaged)
        {
            return GetProgramBase() + virtualAddress;
        }
        int index = ReadWord(osMemoryBase + OsLayout.CurrentIndexOffset);
        int page = virtualAddress / OsLayout.PageSize;
        int offset = virtualAddress % OsLayout.PageSize;
        if (index < 0 || index >= OsLayout.MaxProcesses || page < 0 || page >= OsLayout.MaxPagesPerProcess)
        {
            return currentProcessInstructionStart + virtualAddress;
        }
        int pte = ReadWord(osMemoryBase + OsLayout.PageTableAddress(index) + page * OsLayout.PageTableEntryBytes);
        if (pte < 0)
        {
            return currentProcessInstructionStart + virtualAddress;
        }
        return pte + offset;
    }

    // Raises a demand page fault for the running process: rewinds the IP to the faulting
    // instruction so it re-runs, then dispatches the ISA page-fault handler with the
    // faulting page number in EAX. The handler makes the page resident and resumes.
    private void RaisePageFault(int faultingPage)
    {
        instructionPointer = currentInstructionAddress;
        DispatchOsRoutine(IvtPageFault, faultingPage);
    }

    // Terminates the running user process for touching a virtual page outside its address
    // space (an out-of-bounds or unmapped access). Routed through the same OS teardown as an
    // invalid instruction (exit status -1) so the machine frees the process and schedules the
    // next. Unlike a page fault the IP is not rewound — the faulting instruction does not
    // re-run. Only reached from the User + OS-managed translation path.
    private void RaiseProtectionFault(int faultingPage)
    {
        instructionCount = 0;
        byte opcode = memory[currentInstructionAddress];
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs
        {
            Opcode = opcode,
            B1 = memory[currentInstructionAddress + 1],
            B2 = memory[currentInstructionAddress + 2],
            B3 = memory[currentInstructionAddress + 3],
            Reason = "Memory access outside process bounds"
        });
        ProcessTerminated?.Invoke(this, new ProcessTerminatedArgs { Device = CurrentDeviceId() });
        DispatchOsRoutine(IvtInvalidInstruction, opcode);
    }

    // Translates a user virtual address through the CURRENTLY RUNNING process's page table on
    // behalf of KERNEL-MODE code acting on that process's behalf (OUTS/INS string I/O) —
    // distinct from TryTranslateData, which only translates while the CPU's OWN privilege level
    // is User. Faults the page in (and resolves a copy-on-write share, if isWrite) via the
    // IvtEnsureUserPage routine, run synchronously so this is safe to call mid-instruction: the
    // routine only touches OS memory (page tables/frames) and scratch registers, never the
    // process's own saved-context state, so RunOsRoutineSynchronously's full save/restore
    // leaves the caller's (also mid-syscall) CPU state completely undisturbed. Returns -1 for a
    // pointer outside the process's mapped extent — the caller must abort cleanly (no crash,
    // no corruption of unrelated memory), not for an emulator-level bounds violation.
    // (Phase 3 rectification — fixes the prior linear ProgramAddress+ptr math, which was wrong
    // whenever the target page was swap-backed DATA memory or had been evicted/refilled.)
    public int UserToPhysical(int virtualAddress, bool isWrite)
    {
        if (!OsManaged)
        {
            return GetProgramBase() + virtualAddress;
        }
        int page = virtualAddress / OsLayout.PageSize;
        int offset = virtualAddress % OsLayout.PageSize;
        int procIndex = ReadWord(osMemoryBase + OsLayout.CurrentIndexOffset);
        if (procIndex < 0 || procIndex >= OsLayout.MaxProcesses || page < 0 || page >= OsLayout.MaxPagesPerProcess)
        {
            return -1;
        }
        WriteWord(osMemoryBase + OsLayout.EnsureUserPageIsWrite, isWrite ? 1 : 0);
        RunOsRoutineSynchronously(IvtEnsureUserPage, page);
        if (ReadWord(osMemoryBase + OsLayout.EnsureUserPageResult) != 0)
        {
            return -1;
        }
        int pte = ReadWord(osMemoryBase + OsLayout.PageTableAddress(procIndex) + page * OsLayout.PageTableEntryBytes);
        return pte + offset; // guaranteed resident by IvtEnsureUserPage
    }

    // (Re)seeds the page table for the process in slot `index` when that slot is first
    // used for a process with this (base, extent) — every user-accessible page starts
    // **non-resident** (so its first data touch faults in via IvtPageFault); pages beyond
    // the process are unmapped. A page in the DATA region is seeded swap-backed (its PTE
    // encodes its Bin-disk swap slot); code and stack pages are seeded RAM-home (the -2
    // sentinel). Guarded so it runs once per process creation, not on every resume (which
    // would clobber resident pages back to non-resident — a fault storm). Run from
    // SetLayoutFromEntry, the universal pre-resume gate, so loaded/forked/exec'd processes
    // all get seeded. Uses raw writes to avoid event spam.
    private void SeedPageTableIfNew(int index, int programAddress, int programSize, int requiredMemory, int userExtent)
    {
        if (!OsManaged || index < 0 || index >= OsLayout.MaxProcesses)
        {
            return;
        }
        if (pageSeedBase[index] == programAddress && pageSeedExtent[index] == userExtent)
        {
            return; // already seeded for this process; preserve its resident pages
        }
        pageSeedBase[index] = programAddress;
        pageSeedExtent[index] = userExtent;

        // A forked child (cowPartner >= 0) shares the parent's data-page snapshot copy-on-
        // write: its data PTEs reference the PARTNER's swap slots with the COW encoding. A
        // freshly loaded/exec'd process (no partner) gets private swap data pages.
        int cowPartner = CowPartner(index);
        int pageCount = (userExtent + OsLayout.PageSize - 1) / OsLayout.PageSize;
        int tableBase = osMemoryBase + OsLayout.PageTableAddress(index);
        for (int p = 0; p < OsLayout.MaxPagesPerProcess; p++)
        {
            int pte;
            if (p >= pageCount)
            {
                pte = OsLayout.UnmappedPage;
            }
            else if (OsLayout.IsDataPage(p, programSize, requiredMemory))
            {
                if (cowPartner >= 0)
                {
                    pte = OsLayout.CowPte(OsLayout.SwapSlot(cowPartner, p)); // shared COW data page
                }
                else
                {
                    pte = OsLayout.SwapPte(OsLayout.SwapSlot(index, p)); // private swap-backed data page
                }
            }
            else
            {
                pte = OsLayout.NonResidentPage; // RAM-home code/stack page
            }
            WriteWordRaw(tableBase + p * OsLayout.PageTableEntryBytes, pte);
        }
    }

    // The PTE for a process's virtual page: a resident page's physical base (>= 0), or a
    // NonResidentPage/UnmappedPage sentinel (exposed for tests/visualizer).
    public int PageTableEntry(int processIndex, int page)
    {
        if (!OsManaged || processIndex < 0 || page < 0 || page >= OsLayout.MaxPagesPerProcess)
        {
            return OsLayout.UnmappedPage;
        }
        return ReadWord(osMemoryBase + OsLayout.PageTableAddress(processIndex) + page * OsLayout.PageTableEntryBytes);
    }

    // True when a process's virtual page is currently resident (backed by physical memory).
    public bool IsPageResident(int processIndex, int page)
    {
        return PageTableEntry(processIndex, page) >= 0;
    }

    // Stamps a frame's LRU counter on every resident access and sets its dirty bit on a
    // write. Raw writes: this is OS/MMU bookkeeping (the reference and dirty bits), not
    // part of the visible program-memory timeline, so it fires no MemoryWritten events.
    private void StampFrame(int frameIndex, bool isWrite)
    {
        if (frameIndex < 0 || frameIndex >= OsLayout.FrameCount)
        {
            return; // a resident PTE outside the pool (e.g. a hand-seeded test value): nothing to stamp
        }
        pageClock++;
        int entry = osMemoryBase + OsLayout.FrameTableEntry(frameIndex);
        WriteWordRaw(entry + OsLayout.FrameLastUseField, pageClock);
        if (isWrite)
        {
            WriteWordRaw(entry + OsLayout.FrameDirtyField, 1);
        }
    }

    // ---- frame table ("core map") accessors for tests and the visualizer ----
    public bool FrameOccupied(int frame) { return ReadFrameField(frame, OsLayout.FrameOccupiedField) != 0; }
    public int FrameOwnerProcess(int frame) { return ReadFrameField(frame, OsLayout.FrameOwnerProcField); }
    public int FrameOwnerPage(int frame) { return ReadFrameField(frame, OsLayout.FrameOwnerPageField); }
    public bool FrameDirty(int frame) { return ReadFrameField(frame, OsLayout.FrameDirtyField) != 0; }
    public int FrameLastUse(int frame) { return ReadFrameField(frame, OsLayout.FrameLastUseField); }
    // The swap slot a frame is backed by, or -1 when it holds a RAM-home (code/stack) page.
    public int FrameSwap(int frame) { return ReadFrameField(frame, OsLayout.FrameSwapField); }
    // True when a frame is a copy-on-write read-only share (a write to it traps to copy).
    public bool FrameCow(int frame) { return ReadFrameField(frame, OsLayout.FrameCowField) != 0; }
    private bool FrameIsCow(int frame) { return ReadFrameField(frame, OsLayout.FrameCowField) != 0; }

    // Process `processIndex`'s copy-on-write partner (the other end of a fork share), or -1.
    public int CowPartner(int processIndex)
    {
        return ReadWord(osMemoryBase + OsLayout.CowPartnerAddress(processIndex));
    }

    // Number of frames in the pool currently holding a page.
    public int ResidentFrameCount()
    {
        int count = 0;
        for (int f = 0; f < OsLayout.FrameCount; f++)
        {
            if (FrameOccupied(f))
            {
                count++;
            }
        }
        return count;
    }

    private int ReadFrameField(int frame, int field)
    {
        return ReadWord(osMemoryBase + OsLayout.FrameTableEntry(frame) + field);
    }

    // ===== Buffer Word Helpers (ReadWordFrom, WriteWordInto) =================
    // Word read/write against a plain byte[] (e.g. a register-file snapshot),
    // independent of the memory array.
    private static int ReadWordFrom(byte[] buffer, int offset)
    {
        return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
    }

    private static void WriteWordInto(byte[] buffer, int offset, int value)
    {
        buffer[offset]     = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8)  & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    // ===== Run Loop Internals (TryDispatch, BlockCurrent, InitStack) =========
    // Dispatches one pending device interrupt per call: applies its hardware effect
    // (buffer the input / free the output device) and enters the matching wake
    // routine. Returns false when no interrupt is pending.
    private bool TryDispatchPendingInterrupt()
    {
        if (!pendingInterrupts.TryDequeue(out Interrupt interrupt))
        {
            return false;
        }
        if (interrupt.Kind == InterruptKind.ForegroundSignal)
        {
            // Terminal job-control signal (Ctrl-C/Ctrl-Z): run IvtKill on the focused pid (carried in
            // Device) with the signal (Value) and KillNoDeliver set — there is no killer process.
            WriteWord(osMemoryBase + OsLayout.KillSig, interrupt.Value);
            WriteWord(osMemoryBase + OsLayout.KillNoDeliver, 1);
            DispatchOsRoutine(IvtKill, interrupt.Device);
            return true;
        }
        Device device = DeviceFor(interrupt.Device);
        if (interrupt.Kind == InterruptKind.InputReady)
        {
            device.Input.Enqueue(interrupt.Value);
            ProcessWoken?.Invoke(this, new ProcessWokenArgs { Reason = WaitReason.Input, Value = interrupt.Value, Device = interrupt.Device });
            // Wake the first process actually blocked on this device's input. With no
            // registered waiter the value just stays buffered until a process reads it —
            // there is no device-as-process fallback. Wake routines take the target
            // process index in EAX.
            if (device.Waiters.Count > 0)
            {
                DispatchOsRoutine(IvtWakeInput, NextInputWaiter(device));
            }
        }
        else if (interrupt.Kind == InterruptKind.StringInputReady)
        {
            device.StringInput.Enqueue(interrupt.StringValue!);
            ProcessWoken?.Invoke(this, new ProcessWokenArgs { Reason = WaitReason.StringInput, Value = 0, Device = interrupt.Device });
            if (device.StringWaiters.Count > 0)
            {
                DispatchOsRoutine(IvtWakeInput, NextStringInputWaiter(device));
            }
        }
        else if (interrupt.Kind == InterruptKind.KeyInputReady)
        {
            device.KeyInput.Enqueue(interrupt.Value);
            ProcessWoken?.Invoke(this, new ProcessWokenArgs { Reason = WaitReason.KeyInput, Value = interrupt.Value, Device = interrupt.Device });
            if (device.KeyWaiters.Count > 0)
            {
                DispatchOsRoutine(IvtWakeKey, NextKeyWaiter(device));
            }
        }
        else
        {
            device.OutputBusy = false;
            ProcessWoken?.Invoke(this, new ProcessWokenArgs { Reason = WaitReason.Output, Value = 0, Device = interrupt.Device });
            DispatchOsRoutine(IvtWakeOutput, interrupt.Device);
        }
        return true;
    }

    // Rewinds IP to the I/O instruction so it re-runs on resume, then enters the
    // matching block routine, which saves the process and switches away. Without an
    // OS image, it just yields (the bare-hardware harness has no scheduler).
    private void BlockCurrent(WaitReason reason)
    {
        instructionPointer -= 4;
        if (OsManaged)
        {
            ProcessBlocked?.Invoke(this, new ProcessBlockedArgs { Reason = reason });
            int slot;
            if (reason == WaitReason.Input || reason == WaitReason.StringInput || reason == WaitReason.KeyInput)
            {
                slot = IvtBlockInput;
            }
            else
            {
                slot = IvtBlockOutput;
            }
            DispatchOsRoutine(slot, (int)reason);
            return;
        }
        trapTaken = true;
    }

    // Seeds ESP to the top of the user stack into the process's saved register
    // state; the first context switch loads it into the live ESP register. ESP is
    // stored as an offset from the program base (position-independent model).
    private void InitializeStackPointer(Process process)
    {
        if (!registerIndex.ContainsKey(RegisterName.ESP))
        {
            return;
        }
        int userStackTop = (currentProcessStackStart + currentProcessStackSize) - currentProcessInstructionStart;
        int offset = registerIndex[RegisterName.ESP];
        WriteBytes(process.RegisterStateAddress + offset, new byte[]
        {
            (byte)(userStackTop & 0xFF),
            (byte)((userStackTop >> 8)  & 0xFF),
            (byte)((userStackTop >> 16) & 0xFF),
            (byte)((userStackTop >> 24) & 0xFF)
        });
    }

    // ---- integral functions ----------------------------------------------

    // True when the OS runs its routines as ISA code in the reserved OS region.
    // Without one (a bare hardware harness), Run just executes instructions.
    private bool OsManaged { get { return osMemorySize > 0; } }

    /// <summary>
    /// Advances the machine by one tick: runs an in-progress OS routine, services a
    /// pending device interrupt, invokes the scheduler when idle, or steps the running
    /// process. With no OS image it simply steps the next instruction.
    /// </summary>
    public void Run()
    {
        if (!OsManaged)
        {
            StepInstruction(); // no OS image: plain instruction stepping
            return;
        }

        if (!interruptsEnabled)
        {
            StepInstruction(); // run the in-progress atomic OS routine, one instruction per tick
            return;
        }
        if (TryDispatchPendingInterrupt())
        {
            return; // a wake routine was entered; it runs over the next ticks
        }
        if (!processRunning)
        {
            DispatchOsRoutine(IvtSchedule); // idle: ask the scheduler for a Ready process
            return;
        }
        StepInstruction();
    }

    // Fetches and executes the instruction at the IP, advancing it. Counts the
    // instruction and preempts at the quantum, except for privileged OS-routine
    // instructions (which are not counted, fire no event, and are never preempted).
    private void StepInstruction()
    {
        int ip = instructionPointer;
        instructionPointer += 4;
        byte[] bytes = ReadBytes(ip);
        bool executed = Instruction.Execute(ip, this);
        if (!executed || trapTaken)
        {
            // Trapped (invalid opcode, syscall, termination, or an OS routine entry):
            // not counted and does not advance the quantum counter.
            trapTaken = false;
            return;
        }
        if (OsManaged && !interruptsEnabled)
        {
            return; // an atomic OS-routine instruction: not counted, no event, never preempted
        }
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        cycles++; // baseline of one observational cycle per executed (non-atomic) instruction
        if (OsManaged && instructionCount >= SchedulerInstructionCount)
        {
            instructionCount = 0;
            DispatchOsRoutine(IvtContextSwitch);
        }
    }

    // ---- branch prediction (observational) -------------------------------

    // Set by Instruction.Execute before each handler runs, so a branch handler can
    // tell the predictor which instruction address it is scoring.
    public void SetCurrentInstructionAddress(int address)
    {
        currentInstructionAddress = address;
    }

    // Scores a conditional branch (JZ/JNZ/JS/JNS) against the predictor. Only
    // user-mode program branches are measured: OS routines (interrupts masked) and the
    // shared syscall handler (Kernel mode) are skipped so the stats reflect the
    // workload, not the scheduler's loops. A misprediction adds an observational cycle
    // penalty. This never changes control flow — the caller still jumps on `taken`.
    public void RecordBranch(bool taken)
    {
        if (level != PrivilegeLevel.User)
        {
            return;
        }
        bool predicted = predictor.Predict(currentInstructionAddress);
        bool hit = predictor.Record(currentInstructionAddress, taken);
        if (!hit)
        {
            cycles += MispredictPenalty;
        }
        BranchPredicted?.Invoke(this, new BranchPredictedArgs
        {
            Address = currentInstructionAddress,
            Predicted = predicted,
            Actual = taken,
            Hit = hit
        });
    }

    public BranchPredictor GetBranchPredictor()
    {
        return predictor;
    }

    public long GetCycles()
    {
        return cycles;
    }

    // ===== Process Loading (LoadProcess, LoadProcessLayout, SetProcessLayout) =
    public void LoadProcess(Process process, byte[] program)
    {
        WriteBytes(process.ProgramAddress, program);
        process.ProgramSize = program.Length;
        SetProcessLayout(process.ProgramAddress, program.Length, process.RequiredMemory, process.RequiredStackSize);
        InitializeStackPointer(process);
    }

    // Restores the running process's memory layout so program-relative addressing
    // and range freeing operate on the correct process.
    public void LoadProcessLayout(Process process)
    {
        SetProcessLayout(process.ProgramAddress, process.ProgramSize, process.RequiredMemory, process.RequiredStackSize);
    }

    // Layout: [program][memory][user stack][kernel stack]. The syscall handler is now
    // shared OS code (no per-process copy), so there is no kernel section; the kernel
    // stack holds the syscall trap frame at its base. The register-state block and mode
    // slot live at the front of the memory region (RegisterStateAddress == currentProcessMemoryStart).
    private void SetProcessLayout(int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        currentProcessInstructionStart  = programAddress;
        currentProcessInstructionSize   = programSize;
        currentProcessMemoryStart = programAddress + programSize;
        currentProcessMemorySize  = requiredMemory;
        currentProcessStackStart  = currentProcessMemoryStart + requiredMemory;
        currentProcessStackSize   = requiredStackSize;
        currentProcessKernelStackStart = currentProcessStackStart + requiredStackSize;
        currentProcessKernelStackSize  = KernelStackSize;
    }

    // ===== Kernel Entry / Exit (EnterKernel, Iret, CaptureInterruptedContext) =
    // An I/O instruction executed in user mode traps into the kernel: pushes the trap
    // frame (saved user register file + trap info) onto this process's kernel stack,
    // points the frame-pointer register at it, and jumps to the shared syscall handler.
    // The frame lives at the base of the kernel-stack region (per-process, so it survives
    // a context switch or a block mid-syscall). Interrupts stay enabled: the handler is
    // preemptible. Kernel addresses absolutely (base 0), so EBP/ESP hold real addresses.
    public void EnterKernel(byte opcode, int operandByteOffset)
    {
        EnterKernel(opcode, operandByteOffset, 0);
    }

    // Two-operand variant: stores a second operand byte-offset at trap-frame +12 so
    // the syscall handler can reach both operands (used by OUTS and INS).
    public void EnterKernel(byte opcode, int operandByteOffset, int secondOperandByteOffset)
    {
        int frameBase = currentProcessKernelStackStart;
        WriteBytes(frameBase + KernelSaveAreaOffset, (byte[])registers.Clone());
        WriteWord(frameBase + KernelTrapInfoOffset,      opcode);
        WriteWord(frameBase + KernelTrapInfoOffset + 4,  operandByteOffset);
        // Return IP stored relative to the user program base (position-independent).
        WriteWord(frameBase + KernelTrapInfoOffset + 8,  instructionPointer - currentProcessInstructionStart);
        WriteWord(frameBase + KernelTrapInfoOffset + 12, secondOperandByteOffset);
        SetLevel(PrivilegeLevel.Kernel);
        WriteRegister(RegisterName.EBP, frameBase);                                        // frame pointer (absolute)
        WriteRegister(RegisterName.ESP, currentProcessKernelStackStart + currentProcessKernelStackSize); // kernel stack top
        instructionPointer = ReadWord(osMemoryBase + IvtSyscall * 4);                      // shared handler in the OS region
        trapTaken = true;
    }

    // Returns from a kernel-mode syscall handler, restoring the saved register file
    // (including any IN result written into the save area) from this process's kernel
    // stack frame and jumping back to user code.
    public void Iret()
    {
        int frameBase = currentProcessKernelStackStart;
        int returnOffset = ReadWord(frameBase + KernelTrapInfoOffset + 8); // user-relative
        WriteRegisters(ReadRegisterState(frameBase + KernelSaveAreaOffset));
        SetLevel(PrivilegeLevel.User);
        interruptsEnabled = true; // back in user code: interruptible
        instructionPointer = currentProcessInstructionStart + returnOffset;
    }

    // Snapshots the live register file (folding the live IP into the EIP slot) and
    // the current privilege level as the interrupted process's context, so a
    // dispatched OS routine may use the live registers as scratch while SAVEREGS
    // persists this frame to its entry.
    public void CaptureInterruptedContext()
    {
        trapFrame = (byte[])registers.Clone();
        if (registerIndex.TryGetValue(RegisterName.EIP, out int eipOffset))
        {
            // Fold the live IP into the EIP slot relative to the program base of the
            // interrupted level, so a saved/forked context is position-independent.
            WriteWordInto(trapFrame, eipOffset, instructionPointer - GetProgramBase());
        }
        interruptedLevel = level;
    }

    // SAVEREGS: persists the captured trap frame to a process-table entry, including
    // the interrupted process's privilege level in the entry's level slot so it
    // resumes at the right level. Falls back to the live registers when no frame was
    // captured (direct/test use).
    public void SaveRegistersTo(int address)
    {
        byte[] source;
        if (trapFrame.Length == registers.Length)
        {
            source = (byte[])trapFrame.Clone();
        }
        else
        {
            source = (byte[])registers.Clone();
        }
        WriteBytes(address, source);
        WriteWord(address + ProcessEntryLevel, (int)interruptedLevel);
    }

    // ===== Context Commit (LoadRegistersFrom, SetLayoutFromEntry, OsReturn) ==
    // LOADREGS: stages a process-table entry's saved register file for OSRET to
    // commit. Does not touch the live registers, so the routine keeps its scratch.
    public void LoadRegistersFrom(int address)
    {
        pendingContext = ReadRegisterState(address);
    }

    // SETLAYOUT: rebuilds the hardware memory layout from a process-table entry's
    // sizing fields, so bounds checks and program-relative addressing target the
    // process the OS is about to resume.
    public void SetLayoutFromEntry(int entryAddress)
    {
        int programAddress    = ReadWord(entryAddress + ProcessEntryProgramAddress);
        int programSize       = ReadWord(entryAddress + ProcessEntryProgramSize);
        int requiredMemory    = ReadWord(entryAddress + ProcessEntryRequiredMemory);
        int requiredStackSize = ReadWord(entryAddress + ProcessEntryRequiredStackSize);
        SetProcessLayout(programAddress, programSize, requiredMemory, requiredStackSize);
        // Seed this process's page table the first time the slot is used for it, so the
        // MMU can fault its user pages in on demand. The user-accessible extent is the
        // program + data + user stack (the kernel stack beyond it is addressed absolutely).
        int index = (entryAddress - osMemoryBase - OsLayout.ProcessTableOffset) / ProcessEntrySize;
        int userExtent = programSize + requiredMemory + requiredStackSize;
        SeedPageTableIfNew(index, programAddress, programSize, requiredMemory, userExtent);
    }

    // OSRET: the OS's return-to-process primitive. Reads the target level from the
    // live registers (the routine's computed value), then atomically commits the
    // staged register file, sets the IP from its EIP slot, and drops to that level.
    public void OsReturn(int privilegeLevel)
    {
        processRunning = pendingContext != null;
        if (pendingContext != null)
        {
            WriteRegisters(pendingContext);
            if (registerIndex.TryGetValue(RegisterName.EIP, out int eipOffset))
            {
                // The saved EIP is relative to the resumed process's program base for
                // the level it is returning to; resolve it against that base.
                int savedEipOffset = ReadWordFrom(pendingContext, eipOffset);
                instructionPointer = GetProgramBaseFor((PrivilegeLevel)privilegeLevel) + savedEipOffset;
            }
            pendingContext = null;

            // SETLAYOUT (run just before this) updated the layout to the resumed
            // process; fire ContextSwitched when that process actually changed.
            if (currentProcessInstructionStart != lastContextBase)
            {
                ContextSwitched?.Invoke(this, new ContextSwitchArgs
                {
                    FromProgramBase = lastContextBase,
                    ToProgramBase = currentProcessInstructionStart
                });
                lastContextBase = currentProcessInstructionStart;
            }
        }
        SetLevel((PrivilegeLevel)privilegeLevel);
        interruptsEnabled = true; // returning to a preemptible process: re-enable interrupts
        trapTaken = true;
    }

    public bool IsProcessRunning() { return processRunning; }

    // ===== RunOsRoutineSynchronously =========================================
    // Runs an OS routine to completion synchronously, used for the C#-initiated load
    // path (allocation). The live CPU state is preserved around the routine, which
    // only touches scratch registers and OS memory, so a running process is undisturbed.
    public void RunOsRoutineSynchronously(int slot, int eaxArgument)
    {
        byte[] savedRegisters = (byte[])registers.Clone();
        int savedIp = instructionPointer;
        PrivilegeLevel savedLevel = level;
        bool savedInterrupts = interruptsEnabled;
        bool savedTrap = trapTaken;
        bool savedRunning = processRunning;
        // This dispatch re-captures the interrupted-context bookkeeping (via
        // EnterOsRoutine/OSRET). Snapshot it too so a synchronous run that lands while
        // the main loop is mid-OS-routine stays fully transparent — otherwise the
        // interrupted routine's later SAVEREGS would persist this run's captured level.
        PrivilegeLevel savedInterruptedLevel = interruptedLevel;
        byte[] savedTrapFrame = trapFrame;
        byte[]? savedPending = pendingContext;
        int savedLastContextBase = lastContextBase;

        // The transitions inside a synchronous allocation run are bookkeeping, not
        // part of the visible timeline; keep them off the observability feeds.
        suppressOsEvents = true;
        DispatchOsRoutine(slot, eaxArgument);
        int guard = 0;
        while (!interruptsEnabled && guard++ < 1_000_000)
        {
            int ip = instructionPointer;
            instructionPointer += 4;
            Instruction.Execute(ip, this);
        }
        suppressOsEvents = false;

        WriteRegisters(savedRegisters);
        instructionPointer = savedIp;
        level = savedLevel;
        interruptsEnabled = savedInterrupts;
        trapTaken = savedTrap;
        processRunning = savedRunning;
        interruptedLevel = savedInterruptedLevel;
        trapFrame = savedTrapFrame;
        pendingContext = savedPending;
        lastContextBase = savedLastContextBase;
    }

    // ===== Process Lifecycle (Halt, Exit, Wait, SetFocus, Fork, Exec) ========
    // HLT is a request to terminate with status 0: the OS Halt routine frees the
    // process's memory, reaps it or hands its status to a waiting parent, and schedules
    // the next. Without an OS image, just stop (raise to Privileged).
    public void Halt()
    {
        Terminate(0);
    }

    // EXIT: terminate with an explicit status (delivered to a waiting parent). Same OS
    // routine as HLT, with the status passed in.
    public void Exit(int status)
    {
        Terminate(status);
    }

    private void Terminate(int status)
    {
        instructionCount = 0;
        if (OsManaged)
        {
            ProcessTerminated?.Invoke(this, new ProcessTerminatedArgs { Device = CurrentDeviceId() });
            DispatchOsRoutine(IvtHalt, status);
            return;
        }
        SetLevel(PrivilegeLevel.Kernel);
        interruptsEnabled = false; // atomic stop (no OS image to dispatch to)
        trapTaken = true;
    }

    // WAIT: block until the child with the given PID terminates and collect its exit
    // status. Dispatches the privileged wait routine (which returns immediately if the
    // child is already a zombie). Without an OS image it is a no-op trap.
    public void Wait(int childPid)
    {
        if (OsManaged)
        {
            DispatchOsRoutine(IvtWait, childPid);
            return;
        }
        trapTaken = true;
    }

    // REAP: non-blocking reap of a dead child (Shell §2.5 job control). targetPid = 0 reaps any
    // dead child of the caller; targetPid > 0 reaps that specific child. Dispatches the atomic
    // IvtReap routine, which resumes the caller with the reaped PID in EAX (0 if none dead) and
    // its exit status in EDX. Never blocks. Without an OS image it is a no-op trap.
    public void Reap(int targetPid)
    {
        if (OsManaged)
        {
            DispatchOsRoutine(IvtReap, targetPid);
            return;
        }
        trapTaken = true;
    }

    // KILL: send signal `sig` to the process with PID `targetPid` (Shell §2.5 job control). The
    // signal number is stashed in OsLayout.KillSig (DispatchOsRoutine carries only the pid, in EAX);
    // IvtKill reads both, applies the signal (TERM/KILL tear the target down; STOP/CONT set/clear its
    // stop flag), and resumes the caller with 0 (delivered) or -1 (no such pid) in EAX.
    public void Kill(int targetPid, int sig)
    {
        if (OsManaged)
        {
            WriteWord(osMemoryBase + OsLayout.KillSig, sig);
            WriteWord(osMemoryBase + OsLayout.KillNoDeliver, 0); // process-initiated: deliver the result
            DispatchOsRoutine(IvtKill, targetPid);
            return;
        }
        trapTaken = true;
    }

    // Terminal job-control signal (Shell §2.5 JC-D): a keypress (Ctrl-C → SigTerm, Ctrl-Z → SigStop)
    // sends `sig` to the FOCUSED (foreground) process, like a real tty. Enqueued as a pending
    // interrupt so it is dispatched at a safe point (not mid-OS-routine); the dispatcher reads the
    // focused pid and runs IvtKill with KillNoDeliver set (no killer process called KILL). A no-op if
    // there is no focused process. Uses the InterruptKind carrying (sig, focusedPid).
    public void RaiseForegroundSignal(int sig)
    {
        if (!OsManaged || activeProcess < 0)
        {
            return;
        }
        int entry = osMemoryBase + OsLayout.ProcessEntryAddress(activeProcess);
        int state = ReadWord(entry + ProcessEntryState);
        if (state == (int)ProcessState.Terminated)
        {
            return;
        }
        int pid = ReadWord(entry + ProcessEntryPid);
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.ForegroundSignal, sig, pid));
    }

    // SETFOCUS: make the process with the given PID the foreground process. Maps PID to
    // its process-table slot (reading the table) and points the focus there, so the live
    // keyboard and screen follow it. A no-op if the PID is not a live process.
    public void SetFocus(int pid)
    {
        if (!OsManaged)
        {
            return;
        }
        int count = ReadWord(osMemoryBase + OsLayout.ProcessCountOffset);
        for (int i = 0; i < count; i++)
        {
            int entry = osMemoryBase + OsLayout.ProcessEntryAddress(i);
            int state = ReadWord(entry + ProcessEntryState);
            if (state == (int)ProcessState.Terminated)
            {
                continue;
            }
            if (ReadWord(entry + ProcessEntryPid) == pid)
            {
                SetActiveProcess(i);
                return;
            }
        }
    }

    // FORK: duplicate the running process. Dispatches the privileged fork routine, which
    // creates the child by copying this process's memory and register file, assigns it a
    // PID, and resumes the scheduler. Without an OS image it is a no-op trap.
    public void Fork()
    {
        if (OsManaged)
        {
            DispatchOsRoutine(IvtFork);
            return;
        }
        trapTaken = true;
    }

    // FSYS: a user filesystem syscall. Dispatches the atomic IvtFsSyscall routine, which
    // reads the syscall number/args from the trapped user registers and resumes the caller
    // with the result in EAX. Mirrors Fork's dispatch (no eaxArg, so the live EAX/EBX/ECX/EDX
    // the user set survive into the routine).
    public void FsSyscall()
    {
        if (OsManaged)
        {
            DispatchOsRoutine(IvtFsSyscall);
            return;
        }
        trapTaken = true;
    }

    // EXEC: replace the running process's image with the program in the given disk slot.
    // Dispatches the privileged exec routine (the slot arrives in the routine's EAX),
    // which reallocates, loads the new image, and resets the process to run it.
    public void Exec(int programSlot)
    {
        if (OsManaged)
        {
            DispatchOsRoutine(IvtExec, programSlot);
            return;
        }
        trapTaken = true;
    }

    // ===== TrapInvalidInstruction ============================================
    // An invalid opcode is a fault that terminates the process. Hardware fires the
    // observability event (with the trap's reason, if any), then the OS
    // InvalidInstruction routine tears the process down and schedules the next.
    public void TrapInvalidInstruction(byte opcode, byte b1, byte b2, byte b3)
    {
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs { Opcode = opcode, B1 = b1, B2 = b2, B3 = b3, Reason = ReasonForOpcode(opcode) });
        instructionCount = 0;
        if (OsManaged)
        {
            ProcessTerminated?.Invoke(this, new ProcessTerminatedArgs { Device = CurrentDeviceId() });
            DispatchOsRoutine(IvtInvalidInstruction, opcode);
            return;
        }
        SetLevel(PrivilegeLevel.Kernel);
        interruptsEnabled = false; // atomic stop (no OS image to dispatch to)
        trapTaken = true;
    }

    // The reason string an OS trap attached to this opcode, for the fault event.
    private string? ReasonForOpcode(byte opcode)
    {
        if (trapTable.TryGetValue(opcode, out List<Trap>? list) && list.Count > 0)
        {
            return list[0].Reason;
        }
        return null;
    }

    // ===== Kernel I/O (KernelInput, KernelOutput) ============================
    // Kernel-mode input: deliver a value from the running process's stdin device
    // (fd 0), or block the process on that device until its input interrupt wakes it
    // (the IN instruction re-runs on resume). The process is recorded in the device's
    // wait queue so the interrupt dispatch knows whom to wake.
    public void KernelInput(byte register)
    {
        int deviceId = FdDevice(StdIn);
        Device device = DeviceFor(deviceId);
        if (device.Input.Count == 0)
        {
            AddInputWaiter(device, CurrentDeviceId());
            BlockCurrent(WaitReason.Input);
            return;
        }
        WriteRegisterAt(register, device.Input.Dequeue());
    }

    // Kernel-mode output: deliver to the running process's stdout device (fd 1) if it
    // is free (marking it busy until an output-complete interrupt), otherwise block.
    public void KernelOutput(int value)
    {
        int deviceId = FdDevice(StdOut);
        Device device = DeviceFor(deviceId);
        if (device.OutputBusy)
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        Output(value, deviceId);
        device.OutputBusy = true;
    }

    // Kernel-mode string output: read `len` words from user virtual address `ptr`,
    // take the low byte of each as a character, fire ProgramOutput with the string. Each word
    // is translated through the process's own page table (UserToPhysical), faulting pages in
    // as needed — not a flat ProgramAddress+ptr read, which would be wrong for a swap-backed
    // DATA-region pointer (Phase 3 rectification).
    public void KernelOutputString(int ptr, int len)
    {
        int deviceId = FdDevice(StdOut);
        Device device = DeviceFor(deviceId);
        if (device.OutputBusy)
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            int physical = UserToPhysical(ptr + i * 4, isWrite: false);
            if (physical < 0)
            {
                break; // pointer outside the process's mapped extent: stop cleanly
            }
            int word = ReadWord(physical);
            if (word == 0)
            {
                break;
            }
            sb.Append((char)(word & 0xFF));
        }
        string s = sb.ToString();
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = 0, StringValue = s, Device = deviceId, SourceProcess = CurrentDeviceId() });
        device.OutputBusy = true;
    }

    // Kernel-mode string input: dequeue a line from stdin's string buffer or block
    // the process until one arrives (INS re-runs on resume). The string is written
    // word-by-word to user virtual address `ptr` (up to maxLen words), null-terminated, via
    // UserToPhysical (Phase 3 rectification — see KernelOutputString).
    public void KernelInputString(int ptr, int maxLen)
    {
        int deviceId = FdDevice(StdIn);
        Device device = DeviceFor(deviceId);
        if (device.StringInput.Count == 0)
        {
            AddStringInputWaiter(device, CurrentDeviceId());
            BlockCurrent(WaitReason.StringInput);
            return;
        }
        string s = device.StringInput.Dequeue();
        int writeLen = Math.Min(s.Length, maxLen - 1);
        for (int i = 0; i < writeLen; i++)
        {
            int physical = UserToPhysical(ptr + i * 4, isWrite: true);
            if (physical < 0)
            {
                return; // pointer outside the process's mapped extent: stop cleanly
            }
            WriteWord(physical, s[i]);
        }
        int terminatorPhysical = UserToPhysical(ptr + writeLen * 4, isWrite: true);
        if (terminatorPhysical < 0)
        {
            return;
        }
        WriteWord(terminatorPhysical, 0);
    }

    // ===== Interrupt Raising (RaiseInputInterrupt, RaiseOutputComplete) ======
    // Raises an input interrupt from the live keyboard: delivered to the focused
    // (foreground) process's stdin device, or device 0 when nothing is focused.
    public void RaiseInputInterrupt(int value)
    {
        RaiseInputInterrupt(value, FocusedInputDevice());
    }

    // Raises an input interrupt for a specific device (== owning process index).
    public void RaiseInputInterrupt(int value, int deviceId)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.InputReady, value, deviceId));
    }

    // Signals output completion for device 0 (the default single device).
    public void RaiseOutputComplete()
    {
        RaiseOutputComplete(0);
    }

    // Signals output completion for a specific device (== owning process index).
    public void RaiseOutputComplete(int deviceId)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.OutputComplete, 0, deviceId));
    }

    // Raises a string input interrupt for the focused process's stdin device.
    public void RaiseStringInputInterrupt(string value)
    {
        RaiseStringInputInterrupt(value, FocusedInputDevice());
    }

    // Raises a string input interrupt for a specific device.
    public void RaiseStringInputInterrupt(string value, int deviceId)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.StringInputReady, value, deviceId));
    }

    // Kernel-mode raw key input: dequeue a keycode from stdin's key buffer, or block
    // the process until a key arrives (INK re-runs on resume).
    public void KernelInputKey(byte register)
    {
        int deviceId = FdDevice(StdIn);
        Device device = DeviceFor(deviceId);
        if (device.KeyInput.Count == 0)
        {
            AddKeyWaiter(device, CurrentDeviceId());
            BlockCurrent(WaitReason.KeyInput);
            return;
        }
        WriteRegisterAt(register, device.KeyInput.Dequeue());
    }

    // Kernel-mode non-blocking key poll: dequeue a keycode if available, else write -1.
    public void KernelInputKeyPoll(byte register)
    {
        int deviceId = FdDevice(StdIn);
        Device device = DeviceFor(deviceId);
        if (device.KeyInput.Count == 0)
        {
            WriteRegisterAt(register, -1);
            return;
        }
        WriteRegisterAt(register, device.KeyInput.Dequeue());
    }

    // Raises a raw key interrupt for the focused process's stdin device.
    public void RaiseKeyInterrupt(int keyCode)
    {
        RaiseKeyInterrupt(keyCode, FocusedInputDevice());
    }

    // Raises a raw key interrupt for a specific device.
    public void RaiseKeyInterrupt(int keyCode, int deviceId)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.KeyInputReady, keyCode, deviceId));
    }
}
