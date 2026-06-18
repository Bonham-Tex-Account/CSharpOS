using CSharpOS;

namespace OSTests;

public class NewInstructionTests
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

    private static int SignFlag(Hardware hw)
    {
        return (hw.ReadRegister(RegisterName.EFLAGS) & 2) >> 1;
    }

    [Fact]
    public void Load_ReadsMemoryThroughPointerRegister()
    {
        Hardware hw = Build();
        hw.WriteBytes(40, new byte[] { 0x39, 0x05, 0, 0 }); // 1337
        hw.WriteRegisterAt(1, 40);                          // pointer
        hw.WriteBytes(0, Test.Word(Instruction.LOAD, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(1337, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Store_WritesRegisterThroughPointerRegister()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(1, 40);      // pointer
        hw.WriteRegisterAt(0, 1337);    // value
        hw.WriteBytes(0, Test.Word(Instruction.STORE, 1, 0, 0));
        Instruction.Execute(0, hw);
        byte[] stored = hw.ReadBytes(40);
        int value = stored[0] | (stored[1] << 8) | (stored[2] << 16) | (stored[3] << 24);
        Assert.Equal(1337, value);
    }

    [Fact]
    public void Cmp_EqualOperands_SetsZeroClearsSign()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 5);
        hw.WriteRegisterAt(1, 5);
        hw.WriteBytes(0, Test.Word(Instruction.CMP, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(1, ZeroFlag(hw));
        Assert.Equal(0, SignFlag(hw));
    }

    [Fact]
    public void Cmp_LessThan_SetsSign()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 3);
        hw.WriteRegisterAt(1, 8);
        hw.WriteBytes(0, Test.Word(Instruction.CMP, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, ZeroFlag(hw));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Cmp_GreaterThan_ClearsZeroAndSign()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 9);
        hw.WriteRegisterAt(1, 2);
        hw.WriteBytes(0, Test.Word(Instruction.CMP, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, ZeroFlag(hw));
        Assert.Equal(0, SignFlag(hw));
    }

    [Fact]
    public void Cmp_DoesNotModifyOperands()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 9);
        hw.WriteRegisterAt(1, 2);
        hw.WriteBytes(0, Test.Word(Instruction.CMP, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(9, hw.ReadRegisterAt(0));
        Assert.Equal(2, hw.ReadRegisterAt(1));
    }

    [Fact]
    public void Inc_IncrementsAndUpdatesFlags()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 5);
        hw.WriteBytes(0, Test.Word(Instruction.INC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(6, hw.ReadRegisterAt(0));
        Assert.Equal(0, ZeroFlag(hw));
    }

    [Fact]
    public void Dec_ToZero_SetsZeroFlag()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 1);
        hw.WriteBytes(0, Test.Word(Instruction.DEC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, hw.ReadRegisterAt(0));
        Assert.Equal(1, ZeroFlag(hw));
    }

    [Fact]
    public void Dec_BelowZero_SetsSignFlag()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 0);
        hw.WriteBytes(0, Test.Word(Instruction.DEC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(-1, hw.ReadRegisterAt(0));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Js_WhenSignSet_Jumps()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 2);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.JS, 0x00, 0x40, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0x40, hw.GetInstructionPointer());
    }

    [Fact]
    public void Js_WhenSignClear_DoesNotJump()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 0);
        hw.SetInstructionPointer(8);
        hw.WriteBytes(8, Test.Word(Instruction.JS, 0x00, 0x40, 0));
        Instruction.Execute(8, hw);
        Assert.Equal(8, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jns_WhenSignClear_Jumps()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 0);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.JNS, 0x00, 0x50, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0x50, hw.GetInstructionPointer());
    }

    [Fact]
    public void Jns_WhenSignSet_DoesNotJump()
    {
        Hardware hw = Build();
        hw.WriteRegister(RegisterName.EFLAGS, 2);
        hw.SetInstructionPointer(12);
        hw.WriteBytes(12, Test.Word(Instruction.JNS, 0x00, 0x50, 0));
        Instruction.Execute(12, hw);
        Assert.Equal(12, hw.GetInstructionPointer());
    }

    [Fact]
    public void Out_FiresProgramOutputWithRegisterValue()
    {
        Hardware hw = Build();
        int captured = -1;
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { captured = e.Value; };
        hw.WriteRegisterAt(0, 99);
        hw.WriteBytes(0, Test.Word(Instruction.OUT, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(99, captured);
    }

    [Fact]
    public void In_ReadsFromInputProviderIntoRegister()
    {
        Hardware hw = Build();
        hw.InputProvider = () => 77;
        hw.WriteBytes(0, Test.Word(Instruction.IN, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(77, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void In_WithNoProvider_ReadsZero()
    {
        Hardware hw = Build();
        hw.WriteRegisterAt(0, 5);
        hw.WriteBytes(0, Test.Word(Instruction.IN, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Hlt_DelegatesToOperatingSystem()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.WriteBytes(0, Test.Word(Instruction.HLT, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(1, os.HaltCount);
    }

    [Fact]
    public void Jump_IsRelativeToProgramBase()
    {
        Hardware hw = Build();
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 100;
        process.RegisterStateAddress = 104; // program size = 4
        hw.LoadProcessLayout(process);

        hw.SetInstructionPointer(100);
        hw.WriteBytes(100, Test.Word(Instruction.JMP, 0x00, 0x08, 0));
        Instruction.Execute(100, hw);

        // target offset 8 is relative to the program base (100).
        Assert.Equal(108, hw.GetInstructionPointer());
    }

    [Fact]
    public void ReadRegisterState_ReadsFullRegisterFileWidth()
    {
        // Regression test: register restore must read the whole register file,
        // not just the 4 bytes ReadBytes returns. Otherwise processes leak
        // registers into one another across context switches.
        Hardware hw = Build();
        int registerBytes = Test.AllRegisters().Length * 4;
        byte[] pattern = new byte[registerBytes];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i + 1);
        }
        hw.WriteBytes(200, pattern);

        byte[] state = hw.ReadRegisterState(200);

        Assert.Equal(registerBytes, state.Length);
        Assert.Equal(pattern, state);
    }
}
