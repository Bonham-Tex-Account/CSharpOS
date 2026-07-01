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
    // spawning effort's PID group, and 156 stays spare.
    public const int ProcessEntryDiskSlot           = 152;
    // Per-process file-descriptor table: FdCount handles (0 = stdin, 1 = stdout, ...),
    // each a 4-byte device id. IN/OUT resolve the fd's device rather than assuming
    // device == process index. Bytes 136-159 stay free for later efforts (disk slot,
    // PID group); the fd table sits at 160 so those stay untouched.
    public const int ProcessEntryFdTable            = 160;
    public const int FdCount                        = 4;
    public const int StdIn                          = 0;
    public const int StdOut                         = 1;
    public const int ProcessEntrySize               = 176;  // 160 + FdCount * 4

    // ---- device ids ------------------------------------------------------
    // Character device ids 0..MaxProcesses-1 are the per-process I/O devices (the
    // device == process-index shim, until the focus effort rebinds them). Block
    // devices live above that range; the disk effort registers one at DiskDeviceId.
    public const int DiskDeviceId = 256;
    // Default disk geometry when no Bin is supplied to the constructor: 64 slots of
    // 1 KiB each — generous enough that the many load sites and multi-load tests
    // never exhaust slots.
    public const int DefaultDiskSlots    = 64;
    public const int DefaultDiskSlotSize = 1024;

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
    // Process loading is two routines: IvtAllocate reserves a memory block (pure,
    // disk-free), and IvtDiskLoad copies the program image from its disk slot into
    // the allocated RAM. LoadProcess dispatches them in sequence.
    public const int IvtAllocate           = 8;
    public const int IvtDiskLoad           = 9;
    // Process-spawning routines (fork/exec/wait runtime creation, spawn boot creation).
    public const int IvtFork               = 10;
    public const int IvtExec               = 11;
    public const int IvtWait               = 12;
    public const int IvtSpawn              = 13;
    // The shared syscall (IN/OUT) handler's entry address. Unlike the dispatched slots,
    // EnterKernel jumps here directly without masking interrupts, so the handler stays
    // preemptible; only its address is stored here (wired up by BuildOsImage).
    public const int IvtSyscall            = 14;
    // Demand-paging fault handler (Phase 2): dispatched by the MMU when a user data
    // access touches a non-resident page; makes the page resident, then resumes.
    public const int IvtPageFault          = 15;
    public const int IvtWakeKey            = 16;
    public const int IvtSlotCount          = 17;
    public const int IvtSize               = IvtSlotCount * 4;

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

    // Set once the first process layout is loaded; guards the user-mode bounds
    // check in IsAddressInProcessRanges so plain unit tests that never call
    // LoadProcessLayout are not rejected.
    private bool processLayoutLoaded;

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
        : this(memorySize, registerNames, os, new Bin(DefaultDiskSlots + OsLayout.SwapSlotCount, DefaultDiskSlotSize))
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
            case IvtDiskLoad:           return "DiskLoad";
            case IvtFork:               return "Fork";
            case IvtExec:               return "Exec";
            case IvtWait:               return "Wait";
            case IvtSpawn:              return "Spawn";
            case IvtPageFault:          return "PageFault";
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

    // ===== Process Ranges (IsAddressInProcessRanges, GetCurrentProcessRanges) =
    public bool IsAddressInProcessRanges(int address)
    {
        if (!processLayoutLoaded)
        {
            return true;
        }
        foreach (MemoryRange range in GetCurrentProcessRanges())
        {
            if (address >= range.Start && address < range.Start + range.Size)
            {
                return true;
            }
        }
        return false;
    }

    public List<MemoryRange> GetCurrentProcessRanges()
    {
        List<MemoryRange> ranges = new List<MemoryRange>
        {
            new MemoryRange { Start = currentProcessMemoryStart,      Size = currentProcessMemorySize },
            new MemoryRange { Start = currentProcessStackStart,       Size = currentProcessStackSize },
            new MemoryRange { Start = currentProcessKernelStackStart, Size = currentProcessKernelStackSize },
            new MemoryRange { Start = currentProcessInstructionStart, Size = currentProcessInstructionSize }
        };

        ranges.Sort((MemoryRange a, MemoryRange b) => a.Start.CompareTo(b.Start));

        List<MemoryRange> merged = new List<MemoryRange>();
        MemoryRange current = ranges[0];

        for (int i = 1; i < ranges.Count; i++)
        {
            MemoryRange next = ranges[i];
            if (current.Start + current.Size >= next.Start)
            {
                current = new MemoryRange
                {
                    Start = current.Start,
                    Size = Math.Max(current.Start + current.Size, next.Start + next.Size) - current.Start
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

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
        // Anything the page table cannot describe (no running process or an out-of-range
        // page) falls back to the linear mapping; the bounds traps still guard genuinely
        // out-of-bounds accesses.
        if (index < 0 || index >= OsLayout.MaxProcesses || page < 0 || page >= OsLayout.MaxPagesPerProcess)
        {
            physical = currentProcessInstructionStart + virtualAddress;
            return true;
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
            // Unmapped page (-1, outside the process): linear fallback.
            physical = currentProcessInstructionStart + virtualAddress;
            return true;
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
        processLayoutLoaded = true;
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
    // take the low byte of each as a character, fire ProgramOutput with the string.
    public void KernelOutputString(int ptr, int len)
    {
        int deviceId = FdDevice(StdOut);
        Device device = DeviceFor(deviceId);
        if (device.OutputBusy)
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        int physBase = currentProcessInstructionStart;
        System.Text.StringBuilder sb = new System.Text.StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            int word = ReadWord(physBase + ptr + i * 4);
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
    // word-by-word to user virtual address `ptr` (up to maxLen words), null-terminated.
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
        int physBase = currentProcessInstructionStart;
        int writeLen = Math.Min(s.Length, maxLen - 1);
        for (int i = 0; i < writeLen; i++)
        {
            WriteWord(physBase + ptr + i * 4, s[i]);
        }
        WriteWord(physBase + ptr + writeLen * 4, 0);
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
