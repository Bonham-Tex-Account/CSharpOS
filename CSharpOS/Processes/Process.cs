namespace CSharpOS;

/// <summary>
/// A loadable program plus the C#-side bookkeeping the host keeps about it. The OS
/// owns the authoritative state in its process table; this descriptor carries the
/// load request (sizing, image source) and is updated with the assigned address and
/// PID once loaded.
/// </summary>
public class Process
{
    // ---- public fields ---------------------------------------------------
    /// <summary>Absolute address of this process's saved register-file block.</summary>
    public int RegisterStateAddress;
    /// <summary>Current instruction pointer.</summary>
    public int InstructionPointer;
    /// <summary>Path of the program image file, or empty for a slot-based process.</summary>
    public string ProgramFilePath;
    /// <summary>
    /// Optional friendly name for displays (visualizer process panels). When set it is used
    /// as the process's name instead of the disk-slot / file-path label — e.g. a slot-based
    /// program loaded at boot can still show a meaningful name like "shell".
    /// </summary>
    public string? DisplayName;
    /// <summary>Base address of the loaded program image in RAM.</summary>
    public int ProgramAddress;
    /// <summary>Length of the program image in bytes.</summary>
    public int ProgramSize;
    /// <summary>Working memory the process requested, in bytes.</summary>
    public int RequiredMemory;
    /// <summary>User stack size the process requested, in bytes.</summary>
    public int RequiredStackSize;
    /// <summary>Scheduling state (mirrors the OS process-table entry).</summary>
    public ProcessState State = ProcessState.Ready;
    /// <summary>What the process is blocked on, if any.</summary>
    public WaitReason WaitReason = WaitReason.None;
    /// <summary>
    /// Disk slot holding the program image. -1 means "not yet on disk": a file-path
    /// process auto-stages its bytes to a slot on first load (the file path is sugar
    /// over the single load-from-disk pipeline).
    /// </summary>
    public int ProgramSlot = -1;

    /// <summary>
    /// Monotonic process id assigned by the OS at load time (0 until loaded). The slot
    /// index is internal; PID is the stable identity used by wait/exec/focus.
    /// </summary>
    public int Pid;

    // ---- constructor -----------------------------------------------------
    /// <summary>Creates a process whose image is read from a file path (staged to disk on first load).</summary>
    public Process(string programFilePath, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = programFilePath;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }

    /// <summary>Creates a process whose program image already lives on the disk at <paramref name="programSlot"/>.</summary>
    public Process(int programSlot, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = string.Empty;
        ProgramSlot = programSlot;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }
}
