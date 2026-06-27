using CSharpOS;

namespace OSTests;

// Covers edge cases and behaviors not exercised by the primary test suites.
// Tests marked "will fail" assert the correct expected behavior; the code does
// not yet implement that behavior and can be fixed later.
public class MissingCoverageTests
{
    // Layout of the single process used by BuildWithLayout. Kept as named constants
    // so the merged-range assertions can be computed from them (plus the kernel-size
    // constants) and stay correct if any dimension changes.
    private const int LayoutProgramAddress = 100;
    private const int LayoutProgramSize = 4;
    private const int LayoutUserMemory = 64;
    private const int LayoutUserStack = 64;

    // Returns Hardware with a process layout loaded. The merged process range runs
    // from LayoutProgramAddress to RangeEnd (exclusive):
    //   ProgramAddress + ProgramSize + user memory + user stack + kernel stack
    // (there is no per-process kernel section now; the handler is shared OS code).
    private static int RangeEnd =>
        LayoutProgramAddress + LayoutProgramSize
        + LayoutUserMemory + LayoutUserStack + Hardware.KernelStackSize;

    private static Hardware BuildWithLayout()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", LayoutUserMemory, LayoutUserStack);
        process.ProgramAddress = LayoutProgramAddress;
        process.ProgramSize = LayoutProgramSize;
        hw.LoadProcessLayout(process);
        return hw;
    }

    private static int ZeroFlag(Hardware hw) => Test.ZeroFlag(hw);
    private static int SignFlag(Hardware hw) => Test.SignFlag(hw);

    // ---- IsAddressInProcessRanges boundary conditions ----------------------

    [Fact]
    public void IsAddressInProcessRanges_AtRangeStart_ReturnsTrue()
    {
        Hardware hw = BuildWithLayout();
        Assert.True(hw.IsAddressInProcessRanges(LayoutProgramAddress));
    }

    [Fact]
    public void IsAddressInProcessRanges_BeforeRangeStart_ReturnsFalse()
    {
        Hardware hw = BuildWithLayout();
        Assert.False(hw.IsAddressInProcessRanges(LayoutProgramAddress - 1));
    }

    [Fact]
    public void IsAddressInProcessRanges_OneBeforeRangeEnd_ReturnsTrue()
    {
        Hardware hw = BuildWithLayout();
        Assert.True(hw.IsAddressInProcessRanges(RangeEnd - 1));
    }

    [Fact]
    public void IsAddressInProcessRanges_AtRangeEnd_ReturnsFalse()
    {
        Hardware hw = BuildWithLayout();
        Assert.False(hw.IsAddressInProcessRanges(RangeEnd));
    }

    [Fact]
    public void IsAddressInProcessRanges_WithoutLayout_AlwaysReturnsTrue()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        // processLayoutLoaded is false — guard makes all addresses valid for unit tests
        Assert.True(hw.IsAddressInProcessRanges(0));
        Assert.True(hw.IsAddressInProcessRanges(1023));
    }

    // ---- LOAD bounds check --------------------------------------------------

    [Fact]
    public void Load_InUserMode_OutsideProcessRanges_ShouldTrap()
    {
        // Uses BasicOS so the LOAD trap condition is active.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 100;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);

        InvalidInstructionArgs? trapped = null;
        hw.InvalidInstruction += (_, e) => { trapped = e; };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 800); // address = 100 (programBase) + 800 = 900, outside [100, 376)
        hw.WriteBytes(100, Test.Word(Instruction.LOAD, 0, 1, 0));
        Instruction.Execute(100, hw);

        Assert.NotNull(trapped);
        Assert.Equal(Instruction.LOAD, trapped!.Opcode);
    }

    // ---- STORE in kernel mode is unrestricted --------------------------------

    [Fact]
    public void Store_InKernelMode_OutsideProcessRanges_DoesNotTrap()
    {
        // Uses BasicOS so the STORE trap is defined — but the Condition gates on
        // User mode only, so kernel-mode writes must not fire it.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 100;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);

        bool trapped = false;
        hw.InvalidInstruction += (_, _) => { trapped = true; };

        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        // programBase in Kernel = 0 (kernel addresses absolutely); kernel-mode memory
        // access is not bounds-checked (the LOAD/STORE bounds traps gate on User mode).
        hw.WriteRegisterAt(1, 500); // pointer register
        hw.WriteRegisterAt(0, 0xAB); // value register
        hw.WriteBytes(100, Test.Word(Instruction.STORE, 1, 0, 0));
        Instruction.Execute(100, hw);

        Assert.False(trapped);
    }

    // ---- Instruction edge cases ----------------------------------------------

    [Fact]
    public void MovRegReg_SelfAssignment_LeavesValueUnchanged()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, 42);
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_REG, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(42, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Inc_AtIntMaxValue_WrapsToIntMinValue()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, int.MaxValue);
        hw.WriteBytes(0, Test.Word(Instruction.INC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MinValue, hw.ReadRegisterAt(0));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Dec_AtIntMinValue_WrapsToIntMaxValue()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, int.MinValue);
        hw.WriteBytes(0, Test.Word(Instruction.DEC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MaxValue, hw.ReadRegisterAt(0));
        Assert.Equal(0, SignFlag(hw));
    }

    [Fact]
    public void Add_Overflow_WrapsAround()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, int.MaxValue);
        hw.WriteRegisterAt(1, 1);
        hw.WriteBytes(0, Test.Word(Instruction.ADD, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MinValue, hw.ReadRegisterAt(0));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Sub_Underflow_GoesNegative()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, 0);
        hw.WriteRegisterAt(1, 1);
        hw.WriteBytes(0, Test.Word(Instruction.SUB, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(-1, hw.ReadRegisterAt(0));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Cmp_BothNegative_LessThan_SetsSignFlag()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, -5);
        hw.WriteRegisterAt(1, -3); // (-5) - (-3) = -2 → negative
        hw.WriteBytes(0, Test.Word(Instruction.CMP, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(0, ZeroFlag(hw));
        Assert.Equal(1, SignFlag(hw));
    }

    [Fact]
    public void Div_ByOne_DoesNotChangeValue()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        hw.WriteRegisterAt(0, 12345);
        hw.WriteRegisterAt(1, 1);
        hw.WriteBytes(0, Test.Word(Instruction.DIV, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(12345, hw.ReadRegisterAt(0));
        Assert.Equal(0, ZeroFlag(hw));
    }

    [Fact]
    public void Jmp_ToOffsetZero_LandsAtProgramBase()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 200;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.SetInstructionPointer(200);
        hw.WriteBytes(200, Test.Word(Instruction.JMP, 0x00, 0x00, 0));
        Instruction.Execute(200, hw);

        Assert.Equal(200, hw.GetInstructionPointer()); // programBase + 0 = 200
    }

    [Fact]
    public void Call_ThenRet_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegister(RegisterName.ESP, 300);
        hw.SetInstructionPointer(108); // return address (IP after CALL in the Run loop)
        hw.WriteBytes(100, Test.Word(Instruction.CALL, 0x00, 0x14, 0)); // target offset 20

        Instruction.Execute(100, hw);

        Assert.Equal(20, hw.GetInstructionPointer());
        Assert.Equal(296, hw.ReadRegister(RegisterName.ESP)); // ESP decremented by 4

        hw.WriteBytes(20, Test.Word(Instruction.RET, 0, 0, 0));
        Instruction.Execute(20, hw);

        Assert.Equal(108, hw.GetInstructionPointer());
        Assert.Equal(300, hw.ReadRegister(RegisterName.ESP)); // ESP restored
    }

    [Fact]
    public void Ret_PopsReturnAddressFromArbitraryStackLocation()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(100, new byte[] { 200, 0, 0, 0 }); // return address = 200 at memory[100]
        hw.WriteRegister(RegisterName.ESP, 100);
        hw.WriteBytes(0, Test.Word(Instruction.RET, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(200, hw.GetInstructionPointer());
        Assert.Equal(104, hw.ReadRegister(RegisterName.ESP));
    }

    // ---- Assembler edge cases -----------------------------------------------

    [Fact]
    public void Assembler_LabelAtPositionZero_JmpEncodesTargetZero()
    {
        Assembler asm = new Assembler();
        asm.Label("start");              // label at code offset 0
        asm.MovImm(RegisterName.EAX, 1); // 4 bytes at offset 0
        asm.Jmp("start");                 // 4 bytes at offset 4; target = origin(0) + 0 = 0
        byte[] code = asm.Build();

        Assert.Equal(Instruction.JMP, code[4]);
        Assert.Equal(0, code[5]); // b1 = high byte of target 0
        Assert.Equal(0, code[6]); // b2 = low byte of target 0
    }

    [Fact]
    public void Assembler_BuildCalledTwice_WithDataInt_ReturnsSameBytes()
    {
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EAX, "slot");
        asm.DataInt("slot");

        byte[] first = asm.Build();
        byte[] second = asm.Build();

        Assert.Equal(first.Length, second.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Assembler_UndefinedLabel_ThrowsOnBuild()
    {
        Assembler asm = new Assembler();
        asm.Jmp("nonexistent");
        Assert.Throws<InvalidOperationException>(() => asm.Build());
    }

    [Fact]
    public void Assembler_CallInstruction_IsEncodedAsJumpEncoding()
    {
        Assembler asm = new Assembler();
        asm.Call("target");
        asm.Label("target"); // at offset 4
        byte[] code = asm.Build();

        Assert.Equal(Instruction.CALL, code[0]);
        Assert.Equal(0, code[1]); // high byte of target 4
        Assert.Equal(4, code[2]); // low byte of target 4
    }

    [Fact]
    public void Assembler_MovImmLabel_ResolvesLabelOffset()
    {
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EAX, "slot");
        asm.DataInt("slot"); // appended after 4-byte MOV → label at offset 4
        byte[] code = asm.Build();

        Assert.Equal(Instruction.MOV_REG_IMM, code[0]);
        Assert.Equal((byte)RegisterName.EAX, code[1]);
        Assert.Equal(4, code[2]); // immediate = resolved label offset
    }

    // ---- Trap struct --------------------------------------------------------

    [Fact]
    public void Trap_ConditionField_CanBeAssignedALambda()
    {
        bool invoked = false;
        Trap trap = new Trap
        {
            Opcode = 0x42,
            Reason = "test reason",
            Condition = (hw, b1, b2, b3) => { invoked = true; return true; }
        };
        Assert.NotNull(trap.Condition);
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(256, os);
        bool result = trap.Condition(hw, 0, 0, 0);
        Assert.True(invoked);
        Assert.True(result);
    }

    // ---- Computer -----------------------------------------------------------

    [Fact]
    public void Computer_LoadProcess_DelegatesToOS()
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, new byte[] { 0, 0, 0, 0 });
        try
        {
            BasicOS os = new BasicOS(new StringWriter());
            Computer computer = new Computer(os, Test.MachineWithHeap(8192), Test.AllRegisters(), new List<Process>());
            computer.LoadProcess(new Process(path, 128, 64));
            Assert.True(os.HasProcesses);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

// Tests that require file-backed processes.
public class OsEdgeCaseTests : IDisposable
{
    private readonly List<string> tempFiles = new List<string>();

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    private string TempFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }

    private static byte[] HltProgram()
    {
        Assembler asm = new Assembler();
        asm.Hlt();
        return asm.Build();
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++) { hw.Run(); }
    }

    [Fact]
    public void PendingProcess_LoadedAfterRunningProcessTerminates()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (_, e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };

        os.LoadProcess(new Process(TempFile(HltProgram()), 128, 64)); // P1 halts immediately

        // Load P2 while P1 is still present; it runs after P1 halts and frees memory.
        os.LoadProcess(new Process(TempFile(PrintThenHalt(99)), 128, 64));

        RunSteps(os, hw, 2000);

        Assert.Equal(new List<int> { 99 }, outputs); // P2 ran after P1 freed its memory
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void AllProcessesBlockedOnInput_SystemIsIdle()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX); // immediately blocks — no input queued
        asm.Hlt();

        os.LoadProcess(new Process(TempFile(asm.Build()), 128, 64));
        RunSteps(os, hw, 500);

        Assert.True(os.HasProcesses);       // process still exists, just blocked
        Assert.False(os.HasRunningProcess); // but CPU is idle
    }

    [Fact]
    public void TwoProcesses_BothWithKernelHandlers_BothComplete()
    {
        // If privilege mode is not saved and restored across preemptions inside
        // the kernel handler, one process will resume in User mode and IRET will
        // trap, killing the process. Both printing confirms mode was preserved.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (_, e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };

        os.LoadProcess(new Process(TempFile(PrintThenHalt(11)), 128, 64));
        os.LoadProcess(new Process(TempFile(PrintThenHalt(22)), 128, 64));

        RunSteps(os, hw, 5000);

        Assert.Contains(11, outputs);
        Assert.Contains(22, outputs);
        Assert.False(os.HasProcesses);
    }
}
