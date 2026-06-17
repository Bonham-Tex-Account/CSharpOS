namespace CSharpOS;

public class Process
{
    public int RegisterStateAddress;
    public int InstructionPointer;
    public string ProgramFilePath;
    public int ProgramAddress;
    public int RequiredMemory;
    public int RequiredStackSize;

    public Process(string programFilePath, int requiredMemory, int requiredStackSize)
    {
        ProgramFilePath = programFilePath;
        RequiredMemory = requiredMemory;
        RequiredStackSize = requiredStackSize;
    }
}
