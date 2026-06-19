namespace CSharpOS;

public class Hardware
{
    const int SchedulerInstructionCount = 10;

    // Fixed per-process kernel stack (scratch space, like a real kernel's
    // per-thread stack). The kernel section is sized separately to the OS's
    // kernel image (os.KernelImage), so it scales with the syscall library.
    public const int KernelStackSize = 64;

    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;
    public int GetMemorySize() { return memory.Length; }

    private int instructionCount;
    private int instructionPointer;

    // User vs kernel privilege. Boots in user mode; only a trap/SYSCALL flips it
    // to kernel and IRET flips it back (those instructions arrive in a later pass).
    // Persisted per process by the OS across context switches.
    private bool kernelMode;

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

    public event EventHandler<InstructionExecutedArgs>? InstructionExecuted;
    public event EventHandler<MemoryWrittenArgs>? MemoryWritten;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;
    public event EventHandler<ProgramOutputArgs>? ProgramOutput;

    public Func<int>? InputProvider;

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

    public int GetInstructionPointer() { return instructionPointer; }
    public void SetInstructionPointer(int address) { instructionPointer = address; }
    public int GetProgramBase() { return currentProcessInstructionStart; }

    public bool IsKernelMode() { return kernelMode; }
    public void EnterKernelMode() { kernelMode = true; }
    public void EnterUserMode() { kernelMode = false; }

    public void Output(int value)
    {
        ProgramOutput?.Invoke(this, new ProgramOutputArgs { Value = value });
    }

    public int ReadInput()
    {
        return InputProvider != null ? InputProvider() : 0;
    }

    public void Halt()
    {
        instructionCount = 0;
        os.HandleHalt(this);
    }

    public byte[] ReadBytes(int address)
    {
        return new byte[] { memory[address], memory[address + 1], memory[address + 2], memory[address + 3] };
    }

    public byte[] ReadRegisters()
    {
        return registers;
    }

    // Reads a full register-file-sized block from memory, used to restore a
    // saved register state. ReadBytes only returns a 4-byte instruction word.
    public byte[] ReadRegisterState(int address)
    {
        byte[] state = new byte[registers.Length];
        for (int i = 0; i < state.Length; i++)
        {
            state[i] = memory[address + i];
        }
        return state;
    }

    public void WriteRegisters(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            registers[i] = data[i];
        }
    }

    public void WriteBytes(int address, byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            memory[address + i] = data[i];
        }
        MemoryWritten?.Invoke(this, new MemoryWrittenArgs { Address = address, Data = (byte[])data.Clone() });
    }

    public List<MemoryRange> GetCurrentProcessRanges()
    {
        List<MemoryRange> ranges = new List<MemoryRange>
        {
            new MemoryRange { Start = currentProcessMemoryStart, Size = currentProcessMemorySize },
            new MemoryRange { Start = currentProcessStackStart, Size = currentProcessStackSize },
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

    public void LoadProcess(Process process, byte[] program)
    {
        WriteBytes(process.ProgramAddress, program);
        process.ProgramSize = program.Length;
        process.ModeStateAddress = process.RegisterStateAddress + registers.Length;
        SetProcessLayout(process.ProgramAddress, program.Length, process.RequiredMemory, process.RequiredStackSize);
        if (os.KernelImage.Length > 0)
        {
            WriteBytes(currentProcessKernelSectionStart, os.KernelImage);
        }
        InitializeStackPointer(process);
    }

    // Restores the running process's memory layout so that program-relative
    // addressing and range freeing operate on the correct process.
    public void LoadProcessLayout(Process process)
    {
        SetProcessLayout(process.ProgramAddress, process.ProgramSize, process.RequiredMemory, process.RequiredStackSize);
    }

    // Layout: [program][kernel section][memory][user stack][kernel stack].
    // The register-state block and the per-process mode slot live at the front
    // of the memory region (RegisterStateAddress == currentProcessMemoryStart).
    private void SetProcessLayout(int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        currentProcessInstructionStart = programAddress;
        currentProcessInstructionSize = programSize;
        currentProcessKernelSectionStart = programAddress + programSize;
        currentProcessKernelSectionSize = os.KernelImage.Length;
        currentProcessMemoryStart = currentProcessKernelSectionStart + currentProcessKernelSectionSize;
        currentProcessMemorySize = requiredMemory;
        currentProcessStackStart = currentProcessMemoryStart + requiredMemory;
        currentProcessStackSize = requiredStackSize;
        currentProcessKernelStackStart = currentProcessStackStart + requiredStackSize;
        currentProcessKernelStackSize = KernelStackSize;
    }

    // Seeds ESP to the top of the user stack (it grows down, so CALL's ESP-4
    // writes the first word inside the region) by writing it into the process's
    // saved register state, which the first context switch loads into ESP.
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
            (byte)((userStackTop >> 8) & 0xFF),
            (byte)((userStackTop >> 16) & 0xFF),
            (byte)((userStackTop >> 24) & 0xFF)
        });
    }

    public int ReadRegisterAt(byte index)
    {
        int offset = index * 4;
        return registers[offset] | (registers[offset + 1] << 8) | (registers[offset + 2] << 16) | (registers[offset + 3] << 24);
    }

    public void WriteRegisterAt(byte index, int value)
    {
        int offset = index * 4;
        registers[offset] = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8) & 0xFF);
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
        registers[offset] = (byte)(value & 0xFF);
        registers[offset + 1] = (byte)((value >> 8) & 0xFF);
        registers[offset + 2] = (byte)((value >> 16) & 0xFF);
        registers[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public void TrapInvalidInstruction(byte opcode, byte b1, byte b2, byte b3)
    {
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs { Opcode = opcode, B1 = b1, B2 = b2, B3 = b3 });
        instructionCount = 0;
        os.HandleInvalidInstruction(this, opcode, b1, b2, b3);
    }

    public void Run()
    {
        int ip = instructionPointer;
        instructionPointer += 4;
        byte[] bytes = ReadBytes(ip);
        bool executed = Instruction.Execute(ip, this);
        if (!executed)
        {
            // The instruction trapped as invalid; it did not execute, so it is
            // not reported as executed and does not advance the quantum counter.
            return;
        }
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        if (instructionCount >= SchedulerInstructionCount)
        {
            instructionCount = 0;
            os.ContextSwitch(this);
        }
    }
}
