namespace CSharpOS;

public struct Trap
{
    public byte Opcode;
    public string Reason;
    public Func<Hardware, byte, byte, byte, bool> Condition;
}
