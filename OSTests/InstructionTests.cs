using CSharpOS;

namespace OSTests;

public class InstructionTests
{
    private static Hardware Build()
    {
        FakeOS os = new FakeOS();
        return Test.NewHardware(1024, os);
    }

    private static int ZeroFlag(Hardware hw)
    {
        return hw.ReadRegister(RegisterName.EFLAGS) & 1;
    }

    [Fact]
    public void MovRegReg_CopiesSourceIntoDestination()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(1, 77);
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_REG, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(77, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void MovRegImm_LoadsImmediateIntoDestination()
    {
        Hardware hw = Build();
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_IMM, 2, 200, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(200, hw.ReadRegisterAt(2));
    }

    [Fact]
    public void Add_SumsRegistersIntoDestination()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 5);
        hw.WriteRegisterAt(1, 7);
        hw.WriteBytes(0, Test.Word(Instruction.ADD, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(12, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Add_NonZeroResult_ClearsZeroFlag()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 1);
        hw.WriteRegisterAt(0, 5);
        hw.WriteRegisterAt(1, 7);
        hw.WriteBytes(0, Test.Word(Instruction.ADD, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, ZeroFlag(hw));
    }

    [Fact]
    public void Sub_SubtractsSourceFromDestination()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 10);
        hw.WriteRegisterAt(1, 3);
        hw.WriteBytes(0, Test.Word(Instruction.SUB, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(7, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Sub_ResultingInZero_SetsZeroFlag()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 5);
        hw.WriteRegisterAt(1, 5);
        hw.WriteBytes(0, Test.Word(Instruction.SUB, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, hw.ReadRegisterAt(0));
        Assert.Equal(1, ZeroFlag(hw));
    }

    [Fact]
    public void Mul_MultipliesRegistersIntoDestination()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 3);
        hw.WriteRegisterAt(1, 4);
        hw.WriteBytes(0, Test.Word(Instruction.MUL, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(12, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Div_DividesDestinationBySource()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 12);
        hw.WriteRegisterAt(1, 4);
        hw.WriteBytes(0, Test.Word(Instruction.DIV, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(3, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Div_ByZero_Throws()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 12);
        hw.WriteRegisterAt(1, 0);
        hw.WriteBytes(0, Test.Word(Instruction.DIV, 0, 1, 0));
        Assert.Throws<DivideByZeroException>(() => Instruction.Execute(0, hw));
    }

    [Fact]
    public void Jmp_SetsInstructionPointerToEncodedAddress()
    {
        Hardware hw = Build();
        hw.SetInstructionPointer(0);
        // target = (0x01 << 8) | 0x23 = 291
        hw.WriteBytes(0, Test.Word(Instruction.JMP, 0x01, 0x23, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(291, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jz_WhenZeroFlagSet_Jumps()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 1);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.JZ, 0x00, 0x40, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0x40, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jz_WhenZeroFlagClear_DoesNotJump()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 0);
        hw.SetInstructionPointer(8);
        hw.WriteBytes(8, Test.Word(Instruction.JZ, 0x00, 0x40, 0));
        Instruction.Execute(8, hw);
        Assert.Equal(8, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jnz_WhenZeroFlagClear_Jumps()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 0);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.JNZ, 0x00, 0x50, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0x50, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jnz_WhenZeroFlagSet_DoesNotJump()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 1);
        hw.SetInstructionPointer(12);
        hw.WriteBytes(12, Test.Word(Instruction.JNZ, 0x00, 0x50, 0));
        Instruction.Execute(12, hw);
        Assert.Equal(12, hw.GetInstructionPointer());
    }

    [Fact]
    public void Call_PushesReturnAddressAndJumps()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.ESP, 200);
        hw.SetInstructionPointer(50);
        // target = (0x00 << 8) | 0x64 = 100
        hw.WriteBytes(0, Test.Word(Instruction.CALL, 0x00, 0x64, 0));
        Instruction.Execute(0, hw);

        Assert.Equal(100, hw.GetInstructionPointer());
        Assert.Equal(196, hw.ReadRegister(RegisterName.ESP));
        byte[] stacked = hw.ReadBytes(196);
        int returnAddress = stacked[0] | (stacked[1] << 8) | (stacked[2] << 16) | (stacked[3] << 24);
        Assert.Equal(50, returnAddress);
    }

    [Fact]
    public void CallThenRet_RestoresInstructionPointerAndStack()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.ESP, 200);
        hw.SetInstructionPointer(50);

        hw.WriteBytes(0, Test.Word(Instruction.CALL, 0x00, 0x64, 0));
        Instruction.Execute(0, hw);

        hw.WriteBytes(300, Test.Word(Instruction.RET, 0, 0, 0));
        Instruction.Execute(300, hw);

        Assert.Equal(50, hw.GetInstructionPointer());
        Assert.Equal(200, hw.ReadRegister(RegisterName.ESP));
    }

    [Fact]
    public void Execute_UnknownOpcode_TrapsThroughHardware()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };
        hw.WriteBytes(0, Test.Word(0x7F, 1, 2, 3));
        Instruction.Execute(0, hw);
        Assert.NotNull(captured);
        Assert.Equal(0x7F, captured!.Opcode);
    }
}
