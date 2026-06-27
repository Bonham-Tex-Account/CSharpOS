namespace CSharpOS;

/// <summary>
/// Raised after a (non-privileged) instruction executes, carrying its address and the
/// four encoded bytes so a visualizer can render the executed instruction stream.
/// </summary>
public class InstructionExecutedArgs : EventArgs
{
    public int Address { get; init; }
    public byte Opcode { get; init; }
    public byte B1 { get; init; }
    public byte B2 { get; init; }
    public byte B3 { get; init; }
}
