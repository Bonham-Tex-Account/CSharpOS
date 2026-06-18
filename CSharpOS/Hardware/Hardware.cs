namespace CSharpOS;

public class Hardware
{
    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private IOperatingSystem os;
    public int GetMemorySize() { return memory.Length; }

    private int instructionCount;
    private int instructionPointer;

    private int currentProcessMemoryStart;
    private int currentProcessMemorySize;
    private int currentProcessStackStart;
    private int currentProcessStackSize;
    private int currentProcessInstructionStart;
    private int currentProcessInstructionSize;

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

    public void LoadProcess(Process process, byte[] program)
    {
        WriteBytes(process.ProgramAddress, program);
        SetProcessLayout(process.ProgramAddress, program.Length, process.RequiredMemory, process.RequiredStackSize);
    }

    // Restores the running process's memory layout so that program-relative
    // addressing and range freeing operate on the correct process. Program size
    // is derived from the gap between the program start and its saved register state.
    public void LoadProcessLayout(Process process)
    {
        int programSize = process.RegisterStateAddress - process.ProgramAddress;
        SetProcessLayout(process.ProgramAddress, programSize, process.RequiredMemory, process.RequiredStackSize);
    }

    private void SetProcessLayout(int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        currentProcessInstructionStart = programAddress;
        currentProcessInstructionSize = programSize;
        currentProcessMemoryStart = programAddress + programSize;
        currentProcessMemorySize = requiredMemory;
        currentProcessStackStart = currentProcessMemoryStart + requiredMemory;
        currentProcessStackSize = requiredStackSize;
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
        Instruction.Execute(ip, this);
        InstructionExecuted?.Invoke(this, new InstructionExecutedArgs { Address = ip, Opcode = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3] });
        instructionCount++;
        if (instructionCount >= 5)
        {
            instructionCount = 0;
            os.ContextSwitch(this);
        }
    }
}
