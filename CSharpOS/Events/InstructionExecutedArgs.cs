namespace CSharpOS;

public class InstructionExecutedArgs : EventArgs
{
    public int Address { get; init; }
    public byte Opcode { get; init; }
    public byte B1 { get; init; }
    public byte B2 { get; init; }
    public byte B3 { get; init; }
}
