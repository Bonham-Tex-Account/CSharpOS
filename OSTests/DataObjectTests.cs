using CSharpOS;

namespace OSTests;

public class DataObjectTests
{
    [Fact]
    public void Process_Constructor_SetsSuppliedFields()
    {
        Process process = new Process("program.bin", 64, 32);
        Assert.Equal("program.bin", process.ProgramFilePath);
        Assert.Equal(64, process.RequiredMemory);
        Assert.Equal(32, process.RequiredStackSize);
    }

    [Fact]
    public void Process_Constructor_LeavesLayoutFieldsAtDefault()
    {
        Process process = new Process("program.bin", 64, 32);
        Assert.Equal(0, process.ProgramAddress);
        Assert.Equal(0, process.RegisterStateAddress);
        Assert.Equal(0, process.InstructionPointer);
    }

    [Fact]
    public void Trap_StoresOpcodeReasonAndCondition()
    {
        Trap trap = new Trap
        {
            Opcode = 0x42,
            Reason = "boom",
            Condition = (Hardware hw, byte a, byte b, byte c) => true
        };
        Assert.Equal(0x42, trap.Opcode);
        Assert.Equal("boom", trap.Reason);
        Assert.True(trap.Condition(null!, 0, 0, 0));
    }

    [Fact]
    public void RegisterName_DeclaresExpectedIndexOrdering()
    {
        Assert.Equal(0, (int)RegisterName.EAX);
        Assert.Equal(6, (int)RegisterName.ESP);
        Assert.Equal(9, (int)RegisterName.EFLAGS);
        Assert.Equal(15, (int)RegisterName.SS);
    }

    [Fact]
    public void BasicOS_Constructs_WithNoProcesses()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Assert.False(os.HasProcesses);
    }
}
