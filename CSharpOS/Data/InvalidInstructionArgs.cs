namespace CSharpOS;

public class InvalidInstructionArgs : EventArgs
{
    public byte Opcode { get; init; }
    public byte B1 { get; init; }
    public byte B2 { get; init; }
    public byte B3 { get; init; }
    public string? ProcessName { get; init; }
    public string? Reason { get; init; }
}
