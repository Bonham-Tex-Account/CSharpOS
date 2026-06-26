namespace CSharpOS;

public class Process
{
    // ---- public fields ---------------------------------------------------
    public int RegisterStateAddress;
    public int InstructionPointer;
    public string ProgramFilePath;
    public int ProgramAddress;
    public int ProgramSize;
    public int RequiredMemory;
    public int RequiredStackSize;
    public ProcessState State = ProcessState.Ready;
    public WaitReason WaitReason = WaitReason.None;
    // Disk slot holding the program image. -1 means "not yet on disk": a file-path
    // process auto-stages its bytes to a slot on first load (the file path is sugar
    // over the single load-from-disk pipeline).
    public int ProgramSlot = -1;

    // ---- constructor -----------------------------------------------------
    public Process(string programFilePath, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = programFilePath;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }

    // Slot-based process: the program image already lives on the disk at programSlot.
    public Process(int programSlot, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = string.Empty;
        ProgramSlot = programSlot;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }
}
