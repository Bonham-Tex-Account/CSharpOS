namespace CSharpOS;

public static class Instruction
{
    public const byte MOV_REG_REG = 0x01;
    public const byte MOV_REG_IMM = 0x02;
    public const byte ADD         = 0x10;
    public const byte SUB         = 0x11;
    public const byte MUL         = 0x12;
    public const byte DIV         = 0x13;
    public const byte JMP         = 0x20;
    public const byte JZ          = 0x21;
    public const byte JNZ         = 0x22;
    public const byte CALL        = 0x23;
    public const byte RET         = 0x24;

    private static Dictionary<byte, Action<Hardware, byte, byte, byte>> opcodeTable = new();

    static Instruction()
    {
        opcodeTable[MOV_REG_REG] = InstructionFunctions.MovRegReg;
        opcodeTable[MOV_REG_IMM] = InstructionFunctions.MovRegImm;
        opcodeTable[ADD]         = InstructionFunctions.Add;
        opcodeTable[SUB]         = InstructionFunctions.Sub;
        opcodeTable[MUL]         = InstructionFunctions.Mul;
        opcodeTable[DIV]         = InstructionFunctions.Div;
        opcodeTable[JMP]         = InstructionFunctions.Jmp;
        opcodeTable[JZ]          = InstructionFunctions.Jz;
        opcodeTable[JNZ]         = InstructionFunctions.Jnz;
        opcodeTable[CALL]        = InstructionFunctions.Call;
        opcodeTable[RET]         = InstructionFunctions.Ret;
    }

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
