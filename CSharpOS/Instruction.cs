namespace CSharpOS;

public static class Instruction
{
    private static Dictionary<byte, Action<Hardware, byte, byte, byte>> opcodeTable = new();

    public static void Execute(int address, Hardware hw)
    {
        byte[] bytes = hw.ReadBytes(address);
        byte opcode = bytes[0];
        byte b1 = bytes[1];
        byte b2 = bytes[2];
        byte b3 = bytes[3];

        if (opcodeTable.TryGetValue(opcode, out Action<Hardware, byte, byte, byte>? handler))
        {
            handler(hw, b1, b2, b3);
        }
        else
        {
            hw.TrapInvalidInstruction(opcode, b1, b2, b3);
        }
    }
}
