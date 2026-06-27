namespace CSharpOS;

/// <summary>
/// An instruction trap: a fault condition attached to an opcode. Before executing
/// that opcode the hardware evaluates <see cref="Condition"/>; if it returns true the
/// instruction faults (e.g. a privileged instruction used in user mode, or an
/// out-of-bounds memory access).
/// </summary>
public struct Trap
{
    /// <summary>The opcode this trap guards.</summary>
    public byte Opcode;
    /// <summary>Human-readable explanation surfaced when the trap fires.</summary>
    public string Reason;
    /// <summary>
    /// Returns true when the instruction should fault. Receives the hardware and the
    /// instruction's three operand bytes.
    /// </summary>
    public Func<Hardware, byte, byte, byte, bool> Condition;
}
