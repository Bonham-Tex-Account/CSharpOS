namespace CSharpOS;

/// <summary>
/// Turns an encoded 4-byte instruction word back into a human-readable mnemonic.
/// The inverse of <see cref="Assembler"/>'s emit methods, kept in core so the
/// visualizer (and any tooling) can render the instruction stream without
/// re-implementing opcode knowledge. Pure and side-effect free.
/// </summary>
public static class Disassembler
{
    /// <summary>
    /// Decodes a single instruction word into a mnemonic string such as
    /// "MOV EAX, 5", "JMP 108", or "STORE [EBX], EAX".
    /// </summary>
    public static string Decode(byte opcode, byte b1, byte b2, byte b3)
    {
        int address = (b1 << 8) | b2;
        int imm16 = (b2 << 8) | b3;

        switch (opcode)
        {
            case Instruction.MOV_REG_REG:   return $"MOV {Reg(b1)}, {Reg(b2)}";
            case Instruction.MOV_REG_IMM:   return $"MOV {Reg(b1)}, {b2}";
            case Instruction.MOV_REG_IMM16: return $"MOV {Reg(b1)}, {imm16}";
            case Instruction.LOAD:          return $"LOAD {Reg(b1)}, [{Reg(b2)}]";
            case Instruction.STORE:         return $"STORE [{Reg(b1)}], {Reg(b2)}";
            case Instruction.ADD:           return $"ADD {Reg(b1)}, {Reg(b2)}";
            case Instruction.SUB:           return $"SUB {Reg(b1)}, {Reg(b2)}";
            case Instruction.MUL:           return $"MUL {Reg(b1)}, {Reg(b2)}";
            case Instruction.DIV:           return $"DIV {Reg(b1)}, {Reg(b2)}";
            case Instruction.CMP:           return $"CMP {Reg(b1)}, {Reg(b2)}";
            case Instruction.INC:           return $"INC {Reg(b1)}";
            case Instruction.DEC:           return $"DEC {Reg(b1)}";
            case Instruction.AND:           return $"AND {Reg(b1)}, {Reg(b2)}";
            case Instruction.OR:            return $"OR {Reg(b1)}, {Reg(b2)}";
            case Instruction.XOR:           return $"XOR {Reg(b1)}, {Reg(b2)}";
            case Instruction.NOT:           return $"NOT {Reg(b1)}";
            case Instruction.SHL:           return $"SHL {Reg(b1)}, {Reg(b2)}";
            case Instruction.SHR:           return $"SHR {Reg(b1)}, {Reg(b2)}";
            case Instruction.JMP:           return $"JMP {address}";
            case Instruction.JZ:            return $"JZ {address}";
            case Instruction.JNZ:           return $"JNZ {address}";
            case Instruction.JS:            return $"JS {address}";
            case Instruction.JNS:           return $"JNS {address}";
            case Instruction.CALL:          return $"CALL {address}";
            case Instruction.RET:           return "RET";
            case Instruction.OUT:           return $"OUT {Reg(b1)}";
            case Instruction.IN:            return $"IN {Reg(b1)}";
            case Instruction.HLT:           return "HLT";
            case Instruction.IRET:          return "IRET";
            case Instruction.SAVEREGS:      return $"SAVEREGS [{Reg(b1)}]";
            case Instruction.LOADREGS:      return $"LOADREGS [{Reg(b1)}]";
            case Instruction.SETLAYOUT:     return $"SETLAYOUT [{Reg(b1)}]";
            case Instruction.OSRET:         return $"OSRET {Reg(b1)}";
            default:                        return $"??? {opcode:X2}";
        }
    }

    private static RegisterName Reg(byte index)
    {
        return (RegisterName)index;
    }
}
