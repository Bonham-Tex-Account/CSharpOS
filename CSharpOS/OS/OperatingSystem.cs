using System.Collections.Concurrent;

namespace CSharpOS;

public abstract class OperatingSystem : IOperatingSystem
{
    // ---- public events and properties ------------------------------------
    public event EventHandler<ContextSwitchArgs>? ContextSwitched;
    public event EventHandler<InvalidInstructionArgs>? InvalidInstruction;

    public bool HasProcesses { get { return activeProcesses.Count > 0 || !pendingProcesses.IsEmpty; } }
    public bool HasRunningProcess { get { return currentProcess != null; } }

    // Syscall functions shipped by this OS, copied into each process's kernel
    // section. Empty until overridden; subclasses supply the syscall library.
    public virtual byte[] KernelImage => Array.Empty<byte>();

    // ---- private fields --------------------------------------------------
    private List<Process> activeProcesses;
    private ConcurrentQueue<Process> pendingProcesses;
    private Process? currentProcess;
    private int currentProcessIndex;
    private List<Trap> traps;
    private List<MemoryRange> availableMemoryRanges;
    private TextWriter log;

    // ---- constructor -----------------------------------------------------
    protected OperatingSystem(List<Trap> traps, TextWriter log)
    {
        this.traps = traps;
        this.log = log;
        activeProcesses = new List<Process>();
        pendingProcesses = new ConcurrentQueue<Process>();
        availableMemoryRanges = new List<MemoryRange>();
        currentProcess = null;
    }

    // ---- accessor methods ------------------------------------------------
    public void AttachHardware(Hardware hw)
    {
        availableMemoryRanges = new List<MemoryRange> { new MemoryRange { Start = 0, Size = hw.GetMemorySize() } };
        hw.LoadTraps(traps);
    }

    public void LoadProcess(Process process)
    {
        pendingProcesses.Enqueue(process);
    }

    // ---- helper functions ------------------------------------------------
    private void DrainPendingProcesses(Hardware hw)
    {
        while (pendingProcesses.TryDequeue(out Process? process))
        {
            byte[] program = File.ReadAllBytes(process.ProgramFilePath);
            // The memory region must hold the full register-file save block plus the
            // mode-state slot; bump RequiredMemory up if the caller undersized it.
            int minMemory = hw.GetRegisterFileSize() + 4;
            if (process.RequiredMemory < minMemory)
            {
                process.RequiredMemory = minMemory;
            }
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

    private void TerminateCurrentProcess(Hardware hw)
    {
        string? terminated = currentProcess?.ProgramFilePath;

        FreeCurrentProcessMemory(hw);
        activeProcesses.Remove(currentProcess!);
        currentProcess = null;

        DrainPendingProcesses(hw);

        if (activeProcesses.Count > 0)
        {
            // The removed process's slot now holds the next process, so start the
            // scan there.
            SwitchToNextReady(hw, terminated, currentProcessIndex % activeProcesses.Count);
        }
    }

    // Scans up to all processes from startIndex for a Ready one, loads it as
    // current, and fires ContextSwitched. Sets currentProcess = null (idle) when
    // no Ready process exists.
    private void SwitchToNextReady(Hardware hw, string? fromProcess, int startIndex)
    {
        int count = activeProcesses.Count;
        for (int i = 0; i < count; i++)
        {
            int index = (startIndex + i) % count;
            if (activeProcesses[index].State == ProcessState.Ready)
            {
                currentProcessIndex = index;
                currentProcess = activeProcesses[index];
                LoadCurrent(hw);
                ContextSwitched?.Invoke(this, new ContextSwitchArgs { FromProcess = fromProcess, ToProcess = currentProcess.ProgramFilePath });
                return;
            }
        }
        currentProcess = null;
    }

    private void SaveCurrent(Hardware hw)
    {
        hw.WriteBytes(currentProcess!.RegisterStateAddress, hw.ReadRegisters());
        SaveMode(hw, currentProcess);
        currentProcess.InstructionPointer = hw.GetInstructionPointer();
    }

    private void LoadCurrent(Hardware hw)
    {
        hw.LoadProcessLayout(currentProcess!);
        byte[] savedRegisters = hw.ReadRegisterState(currentProcess!.RegisterStateAddress);
        hw.WriteRegisters(savedRegisters);
        hw.SetInstructionPointer(currentProcess.InstructionPointer);
        RestoreMode(hw, currentProcess);
    }

    // The privilege level is part of each process's saved state, stored in a
    // reserved slot so it survives context switches (a process preempted
    // mid-syscall resumes at the same level).
    private void SaveMode(Hardware hw, Process process)
    {
        hw.WriteBytes(process.ModeStateAddress, new byte[] { (byte)hw.GetPrivilegeLevel(), 0, 0, 0 });
    }

    private void RestoreMode(Hardware hw, Process process)
    {
        byte[] mode = hw.ReadBytes(process.ModeStateAddress);
        hw.SetPrivilegeLevel((PrivilegeLevel)mode[0]);
    }

    // ---- integral functions ----------------------------------------------
    public void ContextSwitch(Hardware hw)
    {
        DrainPendingProcesses(hw);

        if (activeProcesses.Count == 0)
        {
            currentProcess = null;
            return;
        }

        string? fromProcess = currentProcess?.ProgramFilePath;
        if (currentProcess != null)
        {
            SaveCurrent(hw); // the preempted process stays Ready
        }

        SwitchToNextReady(hw, fromProcess, (currentProcessIndex + 1) % activeProcesses.Count);
    }

    // Marks the running process Blocked on a device and immediately yields the
    // CPU to the next Ready process, or idles if none are Ready.
    public void BlockCurrentProcess(Hardware hw, WaitReason reason)
    {
        if (currentProcess == null)
        {
            return;
        }
        string? fromProcess = currentProcess.ProgramFilePath;
        currentProcess.State = ProcessState.Blocked;
        currentProcess.WaitReason = reason;
        SaveCurrent(hw); // saves the rewound IP so the I/O instruction re-runs on resume
        SwitchToNextReady(hw, fromProcess, (currentProcessIndex + 1) % activeProcesses.Count);
    }

    // A device interrupt fired: make one process waiting on that device Ready.
    // Does not preempt the running process.
    public void Wake(WaitReason reason)
    {
        foreach (Process process in activeProcesses)
        {
            if (process.State == ProcessState.Blocked && process.WaitReason == reason)
            {
                process.State = ProcessState.Ready;
                process.WaitReason = WaitReason.None;
                return;
            }
        }
    }

    // Called by the hardware when the CPU is idle: pick a Ready process, if any.
    public void Schedule(Hardware hw)
    {
        if (currentProcess != null)
        {
            return;
        }
        DrainPendingProcesses(hw);
        if (activeProcesses.Count == 0)
        {
            return;
        }
        SwitchToNextReady(hw, null, (currentProcessIndex + 1) % activeProcesses.Count);
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
}
