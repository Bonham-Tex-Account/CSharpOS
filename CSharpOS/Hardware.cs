namespace CSharpOS;

public class Hardware
{
    private byte[] memory;
    private byte[] registers;
    private Dictionary<RegisterName, int> registerIndex;
    private OperatingSystem os;

    public int instructionCount;
    public int instructionPointer;

    private int currentProcessMemoryStart;
    private int currentProcessMemorySize;
    private int currentProcessStackStart;
    private int currentProcessStackSize;
    private int currentProcessInstructionStart;
    private int currentProcessInstructionSize;

    public Hardware(int memorySize, RegisterName[] registerNames, OperatingSystem os)
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
        os.Hardware = this;
    }

    public byte[] ReadBytes(int address)
    {
        return new byte[] { memory[address], memory[address + 1], memory[address + 2], memory[address + 3] };
    }

    public byte[] ReadRegisters()
    {
        return registers;
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
        currentProcessInstructionStart = process.ProgramAddress;
        currentProcessInstructionSize = program.Length;
        currentProcessMemoryStart = process.ProgramAddress + program.Length;
        currentProcessMemorySize = process.RequiredMemory;
        currentProcessStackStart = currentProcessMemoryStart + process.RequiredMemory;
        currentProcessStackSize = process.RequiredStackSize;
    }

    public void TrapInvalidInstruction(byte opcode, byte b1, byte b2, byte b3)
    {
        os.HandleInvalidInstruction(this, opcode, b1, b2, b3);
    }

    public void Run()
    {
        // TBD
    }
}
