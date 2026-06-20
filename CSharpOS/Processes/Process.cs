namespace CSharpOS;

public class Process
{
    // ---- public fields ---------------------------------------------------
    public int RegisterStateAddress;
    public int ModeStateAddress;
    public int InstructionPointer;
    public string ProgramFilePath;
    public int ProgramAddress;
    public int ProgramSize;
    public int RequiredMemory;
    public int RequiredStackSize;
    public ProcessState State = ProcessState.Ready;
    public WaitReason WaitReason = WaitReason.None;

    // ---- constructor -----------------------------------------------------
    public Process(string programFilePath, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = programFilePath;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }
}
