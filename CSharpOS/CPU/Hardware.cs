using System.Collections.Concurrent;

namespace CSharpOS;

public partial class Hardware
{
    // ---- public constants ------------------------------------------------
    public const int KernelStackSize = 64;
    public const int KernelSaveAreaOffset = 0;
    public const int KernelTrapInfoOffset = 64;
    public const int KernelHeaderSize = 80;

    // ---- public events ---------------------------------------------------
    public event EventHandler<InstructionExecutedArgs>? InstructionExecuted;
    public event EventHandler<MemoryWrittenArgs>? MemoryWritten;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;
    public event EventHandler<ProgramOutputArgs>? ProgramOutput;

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
    public int GetInstructionPointer() { return instructionPointer; }
    public void SetInstructionPointer(int address) { instructionPointer = address; }
    public PrivilegeLevel GetPrivilegeLevel() { return level; }
    public void SetPrivilegeLevel(PrivilegeLevel value) { level = value; }

    // Program-relative addressing follows the privilege level: kernel code runs
    // relative to the kernel section, user code relative to its program image.
    public int GetProgramBase()
    {
        return level == PrivilegeLevel.User ? currentProcessInstructionStart : currentProcessKernelSectionStart;
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

    private void DrainInterrupts()
    {
        while (pendingInterrupts.TryDequeue(out Interrupt interrupt))
        {
            if (interrupt.Kind == InterruptKind.InputReady)
            {
                inputBuffer.Enqueue(interrupt.Value);
                os.Wake(WaitReason.Input);
            }
            else
            {
                outputBusy = false;
                os.Wake(WaitReason.Output);
            }
        }
    }

    // Rewinds IP to the I/O instruction so it re-runs on resume, then yields.
    private void BlockCurrent(WaitReason reason)
    {
        instructionPointer -= 4;
        trapTaken = true;
        os.BlockCurrentProcess(this, reason);
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
    public void Run()
    {
        DrainInterrupts();
        if (!os.HasRunningProcess)
        {
            os.Schedule(this);
        }
        if (!os.HasRunningProcess)
        {
            return; // idle: every process is blocked, waiting for an interrupt
        }

        int ip = instructionPointer;
        instructionPointer += 4;
        byte[] bytes = ReadBytes(ip);
        bool executed = Instruction.Execute(ip, this);
        if (!executed || trapTaken)
        {
            // Trapped (invalid opcode, syscall, or termination): not counted as
            // executed and does not advance the quantum counter.
            trapTaken = false;
            return;
        }
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        // User and kernel code are preemptible; privileged OS primitives are atomic.
        if (level != PrivilegeLevel.Privileged && instructionCount >= SchedulerInstructionCount)
        {
            instructionCount = 0;
            os.ContextSwitch(this);
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

    // HLT is a request to terminate: an atomic OS-level (Privileged) operation.
    public void Halt()
    {
        level = PrivilegeLevel.Privileged;
        instructionCount = 0;
        trapTaken = true;
        os.HandleHalt(this);
    }

    // An invalid opcode is a fault that terminates the process: atomic, like HLT.
    // (No re-trap into the kernel — this is the teardown path, not a syscall.)
    public void TrapInvalidInstruction(byte opcode, byte b1, byte b2, byte b3)
    {
        level = PrivilegeLevel.Privileged;
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs { Opcode = opcode, B1 = b1, B2 = b2, B3 = b3 });
        instructionCount = 0;
        trapTaken = true;
        os.HandleInvalidInstruction(this, opcode, b1, b2, b3);
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
