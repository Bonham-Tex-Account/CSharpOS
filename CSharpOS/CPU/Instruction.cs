namespace CSharpOS;

public static class Instruction
{
    // ---- public constants (opcodes) --------------------------------------
    public const byte MOV_REG_REG = 0x01;
    public const byte MOV_REG_IMM = 0x02;
    // 16-bit immediate (b2 = high byte, b3 = low byte) for addresses/offsets that
    // exceed the 8-bit range of MOV_REG_IMM. Used heavily by OS ISA code, whose
    // memory offsets (process table, data section) run well past 255.
    public const byte MOV_REG_IMM16 = 0x03;
    public const byte LOAD        = 0x05;
    public const byte STORE       = 0x06;
    public const byte ADD         = 0x10;
    public const byte SUB         = 0x11;
    public const byte MUL         = 0x12;
    public const byte DIV         = 0x13;
    public const byte CMP         = 0x14;
    public const byte INC         = 0x15;
    public const byte DEC         = 0x16;
    public const byte JMP         = 0x20;
    public const byte JZ          = 0x21;
    public const byte JNZ         = 0x22;
    public const byte CALL        = 0x23;
    public const byte RET         = 0x24;
    public const byte JS          = 0x25;
    public const byte JNS         = 0x26;
    public const byte OUT         = 0x30;
    public const byte IN          = 0x31;
    public const byte HLT         = 0x32;
    public const byte IRET        = 0x33;

    // ---- privileged OS-support opcodes ------------------------------------
    // Used by OS ISA code running in Privileged mode to save/restore a process's
    // full register file, refresh the hardware memory layout from a process-table
    // entry, and return to a process at a chosen privilege level.
    public const byte SAVEREGS    = 0x40;
    public const byte LOADREGS    = 0x41;
    public const byte SETLAYOUT   = 0x42;
    public const byte OSRET       = 0x43;

    // ---- private fields --------------------------------------------------
    private static Dictionary<byte, Action<Hardware, byte, byte, byte>> opcodeTable = new();

    // ---- constructor (static initializer) --------------------------------
    static Instruction()
    {
        opcodeTable[MOV_REG_REG] = InstructionFunctions.MovRegReg;
        opcodeTable[MOV_REG_IMM] = InstructionFunctions.MovRegImm;
        opcodeTable[MOV_REG_IMM16] = InstructionFunctions.MovRegImm16;
        opcodeTable[LOAD]        = InstructionFunctions.Load;
        opcodeTable[STORE]       = InstructionFunctions.Store;
        opcodeTable[ADD]         = InstructionFunctions.Add;
        opcodeTable[SUB]         = InstructionFunctions.Sub;
        opcodeTable[MUL]         = InstructionFunctions.Mul;
        opcodeTable[DIV]         = InstructionFunctions.Div;
        opcodeTable[CMP]         = InstructionFunctions.Cmp;
        opcodeTable[INC]         = InstructionFunctions.Inc;
        opcodeTable[DEC]         = InstructionFunctions.Dec;
        opcodeTable[JMP]         = InstructionFunctions.Jmp;
        opcodeTable[JZ]          = InstructionFunctions.Jz;
        opcodeTable[JNZ]         = InstructionFunctions.Jnz;
        opcodeTable[CALL]        = InstructionFunctions.Call;
        opcodeTable[RET]         = InstructionFunctions.Ret;
        opcodeTable[JS]          = InstructionFunctions.Js;
        opcodeTable[JNS]         = InstructionFunctions.Jns;
        opcodeTable[OUT]         = InstructionFunctions.Out;
        opcodeTable[IN]          = InstructionFunctions.In;
        opcodeTable[HLT]         = InstructionFunctions.Hlt;
        opcodeTable[IRET]        = InstructionFunctions.Iret;
        opcodeTable[SAVEREGS]    = InstructionFunctions.SaveRegs;
        opcodeTable[LOADREGS]    = InstructionFunctions.LoadRegs;
        opcodeTable[SETLAYOUT]   = InstructionFunctions.SetLayout;
        opcodeTable[OSRET]       = InstructionFunctions.OsRet;
    }

    // ---- integral functions ----------------------------------------------

    // Returns true if a handler ran, false if the opcode was invalid and trapped.
    public static bool Execute(int address, Hardware hw)
    {
        byte[] bytes = hw.ReadBytes(address);
        byte opcode = bytes[0];
        byte b1 = bytes[1];
        byte b2 = bytes[2];
        byte b3 = bytes[3];

        if (hw.EvaluateTraps(opcode, b1, b2, b3))
        {
            return false;
        }

        if (opcodeTable.TryGetValue(opcode, out Action<Hardware, byte, byte, byte>? handler))
        {
            handler(hw, b1, b2, b3);
            return true;
        }
        else
        {
            hw.TrapInvalidInstruction(opcode, b1, b2, b3);
            return false;
        }
    }
}
