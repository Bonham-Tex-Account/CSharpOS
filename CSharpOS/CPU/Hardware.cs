using System.Collections.Concurrent;

namespace CSharpOS;

public partial class Hardware
{
    // ---- public constants ------------------------------------------------
    public const int KernelStackSize = 64;
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
    public const int ProcessEntrySize               = 160;

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
    public const int IvtLoadProcess        = 8;
    public const int IvtSlotCount          = 9;
    public const int IvtSize               = IvtSlotCount * 4;

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

    // ---- private constants -----------------------------------------------
    private const int SchedulerInstructionCount = 30;

    // ---- private fields --------------------------------------------------
    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;

    private int instructionCount;
    private int instructionPointer;
    private PrivilegeLevel level;
    private bool trapTaken;

    // Suppresses observability events (PrivilegeChanged, OsRoutineEntered) while an
    // OS routine is run synchronously (the C#-initiated allocation path), whose
    // transitions are bookkeeping, not part of the visible execution timeline.
    private bool suppressOsEvents;

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
    private int currentProcessKernelSectionStart;
    private int currentProcessKernelSectionSize;

    private Dictionary<byte, List<Trap>> trapTable = new Dictionary<byte, List<Trap>>();

    // The OS's reserved memory region at the front of the address space: an IVT
    // followed by OS code and data. Zero-sized when the OS keeps no in-memory image.
    private int osMemoryBase;
    private int osMemorySize;

    // Per-device I/O state, keyed by device id (== the owning process's table
    // index). Each process's terminal is its own device, so its input never leaks
    // to another process and its output device is busy-tracked independently.
    private readonly Dictionary<int, Queue<int>> inputByDevice = new Dictionary<int, Queue<int>>();
    private readonly HashSet<int> outputBusyDevices = new HashSet<int>();
    private readonly ConcurrentQueue<Interrupt> pendingInterrupts = new ConcurrentQueue<Interrupt>();

    // ---- constructor -----------------------------------------------------
    public Hardware(int memorySize, RegisterName[] registerNames, IOperatingSystem os)
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
            case IvtLoadProcess:        return "LoadProcess";
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
        SetLevel(PrivilegeLevel.Privileged);
        instructionPointer = routineAddress;
        trapTaken = true;
    }

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

    // Program-relative addressing follows the privilege level: user code runs
    // relative to its program image, kernel code relative to its kernel section,
    // and privileged OS code addresses memory absolutely (base 0) so it can reach
    // the OS region and any process's memory.
    public int GetProgramBase()
    {
        if (level == PrivilegeLevel.Privileged)
        {
            return 0;
        }
        if (level == PrivilegeLevel.User)
        {
            return currentProcessInstructionStart;
        }
        return currentProcessKernelSectionStart;
    }

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
            new MemoryRange { Start = currentProcessInstructionStart, Size = currentProcessInstructionSize },
            new MemoryRange { Start = currentProcessKernelSectionStart, Size = currentProcessKernelSectionSize }
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
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = value, Device = device });
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

    private Queue<int> InputQueueFor(int device)
    {
        if (!inputByDevice.TryGetValue(device, out Queue<int>? queue))
        {
            queue = new Queue<int>();
            inputByDevice[device] = queue;
        }
        return queue;
    }

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

    // Dispatches one pending device interrupt per call: applies its hardware effect
    // (buffer the input / free the output device) and enters the matching wake
    // routine. Returns false when no interrupt is pending.
    private bool TryDispatchPendingInterrupt()
    {
        if (!pendingInterrupts.TryDequeue(out Interrupt interrupt))
        {
            return false;
        }
        if (interrupt.Kind == InterruptKind.InputReady)
        {
            InputQueueFor(interrupt.Device).Enqueue(interrupt.Value);
            ProcessWoken?.Invoke(this, new ProcessWokenArgs { Reason = WaitReason.Input, Value = interrupt.Value, Device = interrupt.Device });
            // Wake routines take the target device (== process index) in EAX and
            // wake that specific process if it is blocked on the matching reason.
            DispatchOsRoutine(IvtWakeInput, interrupt.Device);
        }
        else
        {
            outputBusyDevices.Remove(interrupt.Device);
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
            if (reason == WaitReason.Input)
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
    // state; the first context switch loads it into the live ESP register.
    private void InitializeStackPointer(Process process)
    {
        if (!registerIndex.ContainsKey(RegisterName.ESP))
        {
            return;
        }
        int userStackTop = currentProcessStackStart + currentProcessStackSize;
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

    public void Run()
    {
        if (!OsManaged)
        {
            StepInstruction(); // no OS image: plain instruction stepping
            return;
        }

        if (level == PrivilegeLevel.Privileged)
        {
            StepInstruction(); // run the in-progress OS routine, one instruction per tick
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
        if (OsManaged && level == PrivilegeLevel.Privileged)
        {
            return; // an OS-routine instruction
        }
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        if (OsManaged && instructionCount >= SchedulerInstructionCount)
        {
            instructionCount = 0;
            DispatchOsRoutine(IvtContextSwitch);
        }
    }

    public void LoadProcess(Process process, byte[] program)
    {
        WriteBytes(process.ProgramAddress, program);
        process.ProgramSize = program.Length;
        SetProcessLayout(process.ProgramAddress, program.Length, process.RequiredMemory, process.RequiredStackSize);
        if (os.KernelImage.Length > 0)
        {
            WriteBytes(currentProcessKernelSectionStart + KernelHeaderSize, os.KernelImage);
        }
        InitializeStackPointer(process);
    }

    // Restores the running process's memory layout so program-relative addressing
    // and range freeing operate on the correct process.
    public void LoadProcessLayout(Process process)
    {
        SetProcessLayout(process.ProgramAddress, process.ProgramSize, process.RequiredMemory, process.RequiredStackSize);
    }

    // Layout: [program][kernel section][memory][user stack][kernel stack].
    // The register-state block and mode slot live at the front of the memory
    // region (RegisterStateAddress == currentProcessMemoryStart).
    private void SetProcessLayout(int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        processLayoutLoaded = true;
        currentProcessInstructionStart  = programAddress;
        currentProcessInstructionSize   = programSize;
        currentProcessKernelSectionStart = programAddress + programSize;
        currentProcessKernelSectionSize  = KernelHeaderSize + os.KernelImage.Length;
        currentProcessMemoryStart = currentProcessKernelSectionStart + currentProcessKernelSectionSize;
        currentProcessMemorySize  = requiredMemory;
        currentProcessStackStart  = currentProcessMemoryStart + requiredMemory;
        currentProcessStackSize   = requiredStackSize;
        currentProcessKernelStackStart = currentProcessStackStart + requiredStackSize;
        currentProcessKernelStackSize  = KernelStackSize;
    }

    // An I/O instruction executed in user mode traps into the kernel: saves the
    // user register file, records trap-info, and jumps to the kernel entry point.
    public void EnterKernel(byte opcode, int operandByteOffset)
    {
        int kernelBase = currentProcessKernelSectionStart;
        WriteBytes(kernelBase + KernelSaveAreaOffset, (byte[])registers.Clone());
        WriteWord(kernelBase + KernelTrapInfoOffset,     opcode);
        WriteWord(kernelBase + KernelTrapInfoOffset + 4, operandByteOffset);
        WriteWord(kernelBase + KernelTrapInfoOffset + 8, instructionPointer);
        SetLevel(PrivilegeLevel.Kernel);
        WriteRegister(RegisterName.ESP, currentProcessKernelStackStart + currentProcessKernelStackSize);
        instructionPointer = kernelBase + KernelHeaderSize;
        trapTaken = true;
    }

    // Returns from a kernel-mode syscall handler, restoring the saved register
    // file (including any IN result written into it) and jumping back to user code.
    public void Iret()
    {
        int kernelBase = currentProcessKernelSectionStart;
        int returnIp = ReadWord(kernelBase + KernelTrapInfoOffset + 8);
        WriteRegisters(ReadRegisterState(kernelBase + KernelSaveAreaOffset));
        SetLevel(PrivilegeLevel.User);
        instructionPointer = returnIp;
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
            WriteWordInto(trapFrame, eipOffset, instructionPointer);
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
                instructionPointer = ReadWordFrom(pendingContext, eipOffset);
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
        trapTaken = true;
    }

    public bool IsProcessRunning() { return processRunning; }

    // Runs an OS routine to completion synchronously, used for the C#-initiated load
    // path (allocation). The live CPU state is preserved around the routine, which
    // only touches scratch registers and OS memory, so a running process is undisturbed.
    public void RunOsRoutineSynchronously(int slot, int eaxArgument)
    {
        byte[] savedRegisters = (byte[])registers.Clone();
        int savedIp = instructionPointer;
        PrivilegeLevel savedLevel = level;
        bool savedTrap = trapTaken;
        bool savedRunning = processRunning;

        // The transitions inside a synchronous allocation run are bookkeeping, not
        // part of the visible timeline; keep them off the observability feeds.
        suppressOsEvents = true;
        DispatchOsRoutine(slot, eaxArgument);
        int guard = 0;
        while (level == PrivilegeLevel.Privileged && guard++ < 1_000_000)
        {
            int ip = instructionPointer;
            instructionPointer += 4;
            Instruction.Execute(ip, this);
        }
        suppressOsEvents = false;

        WriteRegisters(savedRegisters);
        instructionPointer = savedIp;
        level = savedLevel;
        trapTaken = savedTrap;
        processRunning = savedRunning;
    }

    // HLT is a request to terminate: the OS Halt routine frees the process and
    // schedules the next. Without an OS image, just stop (raise to Privileged).
    public void Halt()
    {
        instructionCount = 0;
        if (OsManaged)
        {
            ProcessTerminated?.Invoke(this, new ProcessTerminatedArgs { Device = CurrentDeviceId() });
            DispatchOsRoutine(IvtHalt);
            return;
        }
        SetLevel(PrivilegeLevel.Privileged);
        trapTaken = true;
    }

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
        SetLevel(PrivilegeLevel.Privileged);
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

    // Kernel-mode input: deliver a value from the running process's own input
    // device, or block the process until that device's input interrupt wakes it
    // (the IN instruction re-runs on resume).
    public void KernelInput(byte register)
    {
        Queue<int> queue = InputQueueFor(CurrentDeviceId());
        if (queue.Count == 0)
        {
            BlockCurrent(WaitReason.Input);
            return;
        }
        WriteRegisterAt(register, queue.Dequeue());
    }

    // Kernel-mode output: deliver if the running process's output device is free
    // (marking it busy until an output-complete interrupt), otherwise block.
    public void KernelOutput(int value)
    {
        int device = CurrentDeviceId();
        if (outputBusyDevices.Contains(device))
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        Output(value, device);
        outputBusyDevices.Add(device);
    }

    // Raises an input interrupt for device 0 (the default single device).
    public void RaiseInputInterrupt(int value)
    {
        RaiseInputInterrupt(value, 0);
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
}
