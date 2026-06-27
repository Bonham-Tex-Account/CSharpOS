namespace CSharpOS;

/// <summary>
/// Raised when an instruction faults — an unknown opcode or an OS trap firing — with
/// the offending bytes, the owning process name, and the trap's reason (if any).
/// </summary>
public class InvalidInstructionArgs : EventArgs
{
    public byte Opcode { get; init; }
    public byte B1 { get; init; }
    public byte B2 { get; init; }
    public byte B3 { get; init; }
    public string? ProcessName { get; init; }
    public string? Reason { get; init; }
}
