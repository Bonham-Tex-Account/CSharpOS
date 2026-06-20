using System.Collections.Concurrent;

namespace CSharpOS;

public partial class Hardware
{
    // ---- public constants ------------------------------------------------
    public const int KernelStackSize = 64;
    public const int KernelSaveAreaOffset = 0;
    public const int KernelTrapInfoOffset = 64;
    public const int KernelTrapInfoSize = 16;
    public const int KernelHeaderSize = KernelTrapInfoOffset + KernelTrapInfoSize;

    // ---- process-table entry layout --------------------------------------
    // An entry in the OS process table (held in OS memory). The first 64 bytes
    // mirror the register file (the EIP slot holds the saved instruction pointer);
    // the remaining fields hold the saved privilege level, schedule state, and the
    // sizing data the hardware needs to rebuild the process's memory layout.
    public const int ProcessEntryRegisterFile     = 0;
    public const int ProcessEntryLevel            = 64;
    public const int ProcessEntryState            = 68;
    public const int ProcessEntryWaitReason       = 72;
    public const int ProcessEntryProgramAddress   = 76;
    public const int ProcessEntryProgramSize      = 80;
    public const int ProcessEntryRequiredMemory   = 84;
    public const int ProcessEntryRequiredStackSize = 88;
    // Total bytes the process occupies (program + kernel section + memory + stacks);
    // the OS allocator first-fits this size and Halt returns it to the free list.
    public const int ProcessEntryTotalSize        = 92;
    public const int ProcessEntrySize             = 128;

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

    // ---- private constants -----------------------------------------------
    private const int SchedulerInstructionCount = 10;

    // ---- private fields --------------------------------------------------
    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;

    private int instructionCount;
    private int instructionPointer;
    private PrivilegeLevel level;
    private bool trapTaken;

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

    private readonly Queue<int> inputBuffer = new Queue<int>();
    private bool outputBusy;
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

    private void EnterOsRoutine(int slot)
    {
        CaptureInterruptedContext();
        int routineAddress = ReadWord(osMemoryBase + slot * 4);
        level = PrivilegeLevel.Privileged;
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
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = value });
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
            inputBuffer.Enqueue(interrupt.Value);
            DispatchOsRoutine(IvtWakeInput, (int)WaitReason.Input);
        }
        else
        {
            outputBusy = false;
            DispatchOsRoutine(IvtWakeOutput, (int)WaitReason.Output);
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
        process.ModeStateAddress = process.RegisterStateAddress + registers.Length;
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
        level = PrivilegeLevel.Kernel;
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
        level = PrivilegeLevel.User;
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
        level = (PrivilegeLevel)privilegeLevel;
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

        DispatchOsRoutine(slot, eaxArgument);
        int guard = 0;
        while (level == PrivilegeLevel.Privileged && guard++ < 1_000_000)
        {
            int ip = instructionPointer;
            instructionPointer += 4;
            Instruction.Execute(ip, this);
        }

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
            DispatchOsRoutine(IvtHalt);
            return;
        }
        level = PrivilegeLevel.Privileged;
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
            DispatchOsRoutine(IvtInvalidInstruction, opcode);
            return;
        }
        level = PrivilegeLevel.Privileged;
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

    // Kernel-mode input: deliver a buffered value, or block the process until an
    // input interrupt wakes it (the IN instruction re-runs on resume).
    public void KernelInput(byte register)
    {
        if (inputBuffer.Count == 0)
        {
            BlockCurrent(WaitReason.Input);
            return;
        }
        WriteRegisterAt(register, inputBuffer.Dequeue());
    }

    // Kernel-mode output: deliver if the device is free (marking it busy until an
    // output-complete interrupt), otherwise block until it frees.
    public void KernelOutput(int value)
    {
        if (outputBusy)
        {
            BlockCurrent(WaitReason.Output);
            return;
        }
        Output(value);
        outputBusy = true;
    }

    public void RaiseInputInterrupt(int value)
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.InputReady, value));
    }

    public void RaiseOutputComplete()
    {
        pendingInterrupts.Enqueue(new Interrupt(InterruptKind.OutputComplete, 0));
    }
}
