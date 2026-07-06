using CSharpOS;

namespace OSTests;

/// <summary>
/// Verifies the Disassembler renders every opcode the ISA defines, including the
/// 16-bit immediate form, the bitwise ops, and the privileged OS-support opcodes
/// that the old visualizer Decode rendered as "??? XX". One case per opcode plus
/// the unknown-opcode fallback.
/// </summary>
public class DisassemblerTests
{
    private static byte Idx(RegisterName name)
    {
        return (byte)name;
    }

    [Fact]
    public void Decodes_MovRegReg()
    {
        Assert.Equal("MOV EAX, EBX",
            Disassembler.Decode(Instruction.MOV_REG_REG, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
    }

    [Fact]
    public void Decodes_MovRegImm_AsDecimalByte()
    {
        Assert.Equal("MOV EAX, 200",
            Disassembler.Decode(Instruction.MOV_REG_IMM, Idx(RegisterName.EAX), 200, 0));
    }

    [Fact]
    public void Decodes_MovRegImm16_FromHighLowBytes()
    {
        // b2 = high byte, b3 = low byte => (0x01 << 8) | 0x2C = 300.
        Assert.Equal("MOV ECX, 300",
            Disassembler.Decode(Instruction.MOV_REG_IMM16, Idx(RegisterName.ECX), 0x01, 0x2C));
    }

    [Fact]
    public void Decodes_LoadAndStore_WithPointerSyntax()
    {
        Assert.Equal("LOAD EAX, [EBX]",
            Disassembler.Decode(Instruction.LOAD, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("STORE [EBX], EAX",
            Disassembler.Decode(Instruction.STORE, Idx(RegisterName.EBX), Idx(RegisterName.EAX), 0));
    }

    [Fact]
    public void Decodes_Arithmetic()
    {
        Assert.Equal("ADD EAX, EBX", Disassembler.Decode(Instruction.ADD, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("SUB EAX, EBX", Disassembler.Decode(Instruction.SUB, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("MUL EAX, EBX", Disassembler.Decode(Instruction.MUL, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("DIV EAX, EBX", Disassembler.Decode(Instruction.DIV, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("CMP EAX, EBX", Disassembler.Decode(Instruction.CMP, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("INC EAX", Disassembler.Decode(Instruction.INC, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("DEC EAX", Disassembler.Decode(Instruction.DEC, Idx(RegisterName.EAX), 0, 0));
    }

    [Fact]
    public void Decodes_Bitwise()
    {
        Assert.Equal("AND EAX, EBX", Disassembler.Decode(Instruction.AND, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("OR EAX, EBX", Disassembler.Decode(Instruction.OR, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("XOR EAX, EBX", Disassembler.Decode(Instruction.XOR, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
        Assert.Equal("NOT EAX", Disassembler.Decode(Instruction.NOT, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("SHL EAX, ECX", Disassembler.Decode(Instruction.SHL, Idx(RegisterName.EAX), Idx(RegisterName.ECX), 0));
        Assert.Equal("SHR EAX, ECX", Disassembler.Decode(Instruction.SHR, Idx(RegisterName.EAX), Idx(RegisterName.ECX), 0));
    }

    [Fact]
    public void Decodes_Jumps_WithAbsoluteAddressFromB1B2()
    {
        // address = (b1 << 8) | b2 = (0x01 << 8) | 0x23 = 291.
        Assert.Equal("JMP 291", Disassembler.Decode(Instruction.JMP, 0x01, 0x23, 0));
        Assert.Equal("JZ 64", Disassembler.Decode(Instruction.JZ, 0x00, 0x40, 0));
        Assert.Equal("JNZ 80", Disassembler.Decode(Instruction.JNZ, 0x00, 0x50, 0));
        Assert.Equal("JS 64", Disassembler.Decode(Instruction.JS, 0x00, 0x40, 0));
        Assert.Equal("JNS 80", Disassembler.Decode(Instruction.JNS, 0x00, 0x50, 0));
        Assert.Equal("CALL 100", Disassembler.Decode(Instruction.CALL, 0x00, 0x64, 0));
    }

    [Fact]
    public void Decodes_NoOperandAndIoOps()
    {
        Assert.Equal("RET", Disassembler.Decode(Instruction.RET, 0, 0, 0));
        Assert.Equal("HLT", Disassembler.Decode(Instruction.HLT, 0, 0, 0));
        Assert.Equal("IRET", Disassembler.Decode(Instruction.IRET, 0, 0, 0));
        Assert.Equal("OUT EAX", Disassembler.Decode(Instruction.OUT, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("IN EAX", Disassembler.Decode(Instruction.IN, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("OUTS [EAX], ECX", Disassembler.Decode(Instruction.OUTS, Idx(RegisterName.EAX), Idx(RegisterName.ECX), 0));
        Assert.Equal("INS [EAX], ECX", Disassembler.Decode(Instruction.INS, Idx(RegisterName.EAX), Idx(RegisterName.ECX), 0));
        Assert.Equal("INK EAX", Disassembler.Decode(Instruction.INK, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("INPOLL EAX", Disassembler.Decode(Instruction.INPOLL, Idx(RegisterName.EAX), 0, 0));
    }

    [Fact]
    public void Decodes_OsSupportOps()
    {
        Assert.Equal("SAVEREGS [ESP]", Disassembler.Decode(Instruction.SAVEREGS, Idx(RegisterName.ESP), 0, 0));
        Assert.Equal("LOADREGS [ESP]", Disassembler.Decode(Instruction.LOADREGS, Idx(RegisterName.ESP), 0, 0));
        Assert.Equal("SETLAYOUT [EAX]", Disassembler.Decode(Instruction.SETLAYOUT, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("OSRET EBX", Disassembler.Decode(Instruction.OSRET, Idx(RegisterName.EBX), 0, 0));
    }

    [Fact]
    public void Decodes_Reap_WithSingleRegisterOperand()
    {
        // REAP r — non-blocking reap of a dead child (Shell §2.5 job control).
        Assert.Equal("REAP EAX", Disassembler.Decode(Instruction.REAP, Idx(RegisterName.EAX), 0, 0));
        Assert.Equal("REAP EDI", Disassembler.Decode(Instruction.REAP, Idx(RegisterName.EDI), 0, 0));
    }

    [Fact]
    public void Decodes_Kill_WithPidAndSignalRegisters()
    {
        // KILL r, s — send signal reg[s] to the process with PID reg[r] (Shell §2.5 job control).
        Assert.Equal("KILL ESI, EDX", Disassembler.Decode(Instruction.KILL, Idx(RegisterName.ESI), Idx(RegisterName.EDX), 0));
    }

    [Fact]
    public void Decodes_Sigaction_WithSignalAndHandlerRegisters()
    {
        // SIGACTION s, h — install reg[h] as the catchable-signal handler; reg[s] selects the signal.
        Assert.Equal("SIGACTION EAX, EBX", Disassembler.Decode(Instruction.SIGACTION, Idx(RegisterName.EAX), Idx(RegisterName.EBX), 0));
    }

    [Fact]
    public void Decodes_SigReturn_AsNoOperand()
    {
        // SIGRETURN — return from a signal handler (Shell §2.5 job control, JC-E).
        Assert.Equal("SIGRETURN", Disassembler.Decode(Instruction.SIGRETURN, 0, 0, 0));
    }

    [Fact]
    public void Decodes_UnknownOpcode_AsHexFallback()
    {
        Assert.Equal("??? FF", Disassembler.Decode(0xFF, 0, 0, 0));
        Assert.Equal("??? 7F", Disassembler.Decode(0x7F, 1, 2, 3));
    }
}
