using System.Collections.Concurrent;

namespace CSharpOS;

public abstract class OperatingSystem : IOperatingSystem
{
    private List<Process> activeProcesses;
    private ConcurrentQueue<Process> pendingProcesses;
    private Process? currentProcess;
    private int currentProcessIndex;
    private List<Trap> traps;
    private List<MemoryRange> availableMemoryRanges;
    private TextWriter log;
    public bool HasProcesses { get { return activeProcesses.Count > 0 || !pendingProcesses.IsEmpty; } }

    public event EventHandler<ContextSwitchArgs>? ContextSwitched;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;

    // Syscall functions shipped by this OS, copied into each process's kernel
    // section. Empty until the SYSCALL pass; subclasses override to add syscalls.
    public virtual byte[] KernelImage => Array.Empty<byte>();

    protected OperatingSystem(List<Trap> traps, TextWriter log)
    {
        this.traps = traps;
        this.log = log;
        activeProcesses = new List<Process>();
        pendingProcesses = new ConcurrentQueue<Process>();
        availableMemoryRanges = new List<MemoryRange>();
        currentProcess = null;
    }

    public void AttachHardware(Hardware hw)
    {
        availableMemoryRanges = new List<MemoryRange> { new MemoryRange { Start = 0, Size = hw.GetMemorySize() } };
    }

    public void LoadProcess(Process process)
    {
        pendingProcesses.Enqueue(process);
    }

    private void DrainPendingProcesses(Hardware hw)
    {
        while (pendingProcesses.TryDequeue(out Process? process))
        {
            byte[] program = File.ReadAllBytes(process.ProgramFilePath);
            // The kernel section is the hardware-reserved header plus the kernel image.
            int kernelSectionSize = Hardware.KernelHeaderSize + KernelImage.Length;
            int totalSize = program.Length + kernelSectionSize + process.RequiredMemory + process.RequiredStackSize + Hardware.KernelStackSize;

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
                continue;
            }

            process.ProgramAddress = allocated.Start;
            // The register state lives at the start of the memory region, which sits
            // after the program and the reserved kernel section.
            process.RegisterStateAddress = allocated.Start + program.Length + kernelSectionSize;
            process.InstructionPointer = allocated.Start;
            SplitRange(allocated, totalSize);
            hw.LoadProcess(process, program);
            activeProcesses.Add(process);
        }
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
        InvalidInstruction?.Invoke(this, new InvalidInstructionArgs { Opcode = opcode, B1 = b1, B2 = b2, B3 = b3, ProcessName = currentProcess?.ProgramFilePath, Reason = reason });

        TerminateCurrentProcess(hw);
    }

    public void HandleHalt(Hardware hw)
    {
        log.WriteLine($"[HALT] Process: {currentProcess?.ProgramFilePath ?? "unknown"}");
        TerminateCurrentProcess(hw);
    }

    private void TerminateCurrentProcess(Hardware hw)
    {
        string? terminated = currentProcess?.ProgramFilePath;

        FreeCurrentProcessMemory(hw);
        activeProcesses.Remove(currentProcess!);
        currentProcess = null;

        DrainPendingProcesses(hw);

        if (activeProcesses.Count > 0)
        {
            currentProcessIndex = currentProcessIndex % activeProcesses.Count;
            currentProcess = activeProcesses[currentProcessIndex];
            hw.LoadProcessLayout(currentProcess);
            byte[] savedRegisters = hw.ReadRegisterState(currentProcess.RegisterStateAddress);
            hw.WriteRegisters(savedRegisters);
            hw.SetInstructionPointer(currentProcess.InstructionPointer);
            RestoreMode(hw, currentProcess);

            ContextSwitched?.Invoke(this, new ContextSwitchArgs { FromProcess = terminated, ToProcess = currentProcess.ProgramFilePath });
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

    public void ContextSwitch(Hardware hw)
    {
        DrainPendingProcesses(hw);

        if (activeProcesses.Count == 0)
        {
            return;
        }

        string? fromProcess = currentProcess?.ProgramFilePath;

        if (currentProcess != null)
        {
            hw.WriteBytes(currentProcess.RegisterStateAddress, hw.ReadRegisters());
            SaveMode(hw, currentProcess);
            currentProcess.InstructionPointer = hw.GetInstructionPointer();
        }

        currentProcessIndex = (currentProcessIndex + 1) % activeProcesses.Count;
        currentProcess = activeProcesses[currentProcessIndex];
        hw.LoadProcessLayout(currentProcess);

        byte[] savedRegisters = hw.ReadRegisterState(currentProcess.RegisterStateAddress);
        hw.WriteRegisters(savedRegisters);
        hw.SetInstructionPointer(currentProcess.InstructionPointer);
        RestoreMode(hw, currentProcess);

        ContextSwitched?.Invoke(this, new ContextSwitchArgs { FromProcess = fromProcess, ToProcess = currentProcess.ProgramFilePath });
    }

    // The privilege level is part of each process's saved state, stored in a
    // reserved slot in its protected area so it survives context switches (a
    // process preempted mid-syscall resumes at the same level).
    private void SaveMode(Hardware hw, Process process)
    {
        hw.WriteBytes(process.ModeStateAddress, new byte[] { (byte)hw.GetPrivilegeLevel(), 0, 0, 0 });
    }

    private void RestoreMode(Hardware hw, Process process)
    {
        byte[] mode = hw.ReadBytes(process.ModeStateAddress);
        hw.SetPrivilegeLevel((PrivilegeLevel)mode[0]);
    }
}
