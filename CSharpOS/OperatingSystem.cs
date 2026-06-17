namespace CSharpOS;

public class OperatingSystem
{
    private List<Process> processes;
    private Process? currentProcess;
    private int currentProcessIndex;
    private List<Trap> traps;
    private List<MemoryRange> availableMemoryRanges;
    private TextWriter log;
    public Hardware? Hardware { get; set; }

    public OperatingSystem(List<Trap> traps, TextWriter log)
    {
        this.traps = traps;
        this.log = log;
        processes = new List<Process>();
        availableMemoryRanges = new List<MemoryRange>();
        currentProcess = null;
    }

    public bool LoadProcess(Process process)
    {
        byte[] program = File.ReadAllBytes(process.ProgramFilePath);
        int totalSize = process.RequiredMemory + process.RequiredStackSize + program.Length;

        MemoryRange allocated = new MemoryRange { Start = -1, Size = 0 };
        foreach (MemoryRange range in availableMemoryRanges)
        {
            if (range.Size >= totalSize)
            {
                allocated = range;
                break;
            }
        }

        if (allocated.Start == -1)
        {
            log.WriteLine($"[LOAD FAILED] Not enough memory for process: {process.ProgramFilePath}");
            return false;
        }

        process.ProgramAddress = allocated.Start;
        process.RegisterStateAddress = allocated.Start + program.Length;

        SplitRange(allocated, totalSize);
        Hardware!.LoadProcess(process, program);
        processes.Add(process);
        return true;
    }

    private void SplitRange(MemoryRange range, int used)
    {
        availableMemoryRanges.Remove(range);
        if (range.Size > used)
        {
            availableMemoryRanges.Add(new MemoryRange { Start = range.Start + used, Size = range.Size - used });
        }
    }

    public void HandleInvalidInstruction(Hardware hw, byte opcode, byte b1, byte b2, byte b3)
    {
        string reason = "Unknown invalid instruction";
        foreach (Trap trap in traps)
        {
            if (trap.Opcode == opcode)
            {
                reason = trap.Reason;
                break;
            }
        }

        log.WriteLine($"[INVALID INSTRUCTION] Process: {currentProcess?.ProgramFilePath ?? "unknown"} | Reason: {reason} | Instruction: {opcode:X2} {b1:X2} {b2:X2} {b3:X2}");

        FreeCurrentProcessMemory(hw);
        processes.Remove(currentProcess!);
        currentProcess = null;

        if (processes.Count > 0)
        {
            currentProcessIndex = currentProcessIndex % processes.Count;
            currentProcess = processes[currentProcessIndex];
            byte[] savedRegisters = hw.ReadBytes(currentProcess.RegisterStateAddress);
            hw.WriteRegisters(savedRegisters);
            hw.instructionPointer = currentProcess.InstructionPointer;
            hw.instructionCount = 0;
        }
    }

    private void FreeCurrentProcessMemory(Hardware hw)
    {
        List<MemoryRange> freed = hw.GetCurrentProcessRanges();
        foreach (MemoryRange range in freed)
        {
            AddAndMergeRange(range);
        }
    }

    private void AddAndMergeRange(MemoryRange newRange)
    {
        availableMemoryRanges.Add(newRange);
        availableMemoryRanges.Sort((MemoryRange a, MemoryRange b) => a.Start.CompareTo(b.Start));

        List<MemoryRange> merged = new List<MemoryRange>();
        MemoryRange current = availableMemoryRanges[0];

        for (int i = 1; i < availableMemoryRanges.Count; i++)
        {
            MemoryRange next = availableMemoryRanges[i];
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
        availableMemoryRanges = merged;
    }

    public void ContextSwitch()
    {
        if (currentProcess != null)
        {
            // save current process register state and instruction pointer into its reserved memory
            Hardware!.WriteBytes(currentProcess.RegisterStateAddress, Hardware.ReadRegisters());
            currentProcess.InstructionPointer = Hardware.instructionPointer;
        }

        // advance to next process round-robin
        currentProcessIndex = (currentProcessIndex + 1) % processes.Count;
        currentProcess = processes[currentProcessIndex];

        // load next process registers and point hardware to its instructions
        byte[] savedRegisters = Hardware!.ReadBytes(currentProcess.RegisterStateAddress);
        Hardware.WriteRegisters(savedRegisters);
        Hardware.instructionPointer = currentProcess.InstructionPointer;

        Hardware.instructionCount = 0;
    }
}
