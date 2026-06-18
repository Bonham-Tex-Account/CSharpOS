using CSharpOS;

namespace OSTests;

/// <summary>
/// Tests that exercise risky edge cases and uninitialized/empty state.
/// These assert intended behavior; some may fail and surface real bugs.
/// </summary>
public class EdgeCaseTests : IDisposable
{
    private readonly List<string> tempFiles = new List<string>();

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ContextSwitch_NoProcesses_DoesNotThrow()
    {
        // Intended: a context switch with nothing to schedule should be a no-op,
        // not a crash. Current code computes (index + 1) % activeProcesses.Count.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);

        os.ContextSwitch(hw);

        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void ContextSwitch_AllProcessesFailToLoad_DoesNotThrow()
    {
        // Every pending process is too large, so draining leaves activeProcesses
        // empty. The subsequent round-robin advance should still not crash.
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(64, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        os.LoadProcess(new Process(CreateProgramFile(program), 5000, 5000));

        os.ContextSwitch(hw);

        Assert.Contains("[LOAD FAILED]", log.ToString());
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void HandleInvalidInstruction_NoCurrentProcess_DoesNotThrow()
    {
        // Trapping before any process is current: removing a null process and
        // freeing zeroed ranges should be harmless.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);

        os.HandleInvalidInstruction(hw, 0x00, 0, 0, 0);

        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void FirstRun_BeforeAnyLoad_LoadsThenContinues()
    {
        // Processes are only drained on the first context switch / trap, so the
        // first Run executes zeroed memory (invalid opcode) before the program
        // is loaded. Intended: the OS recovers and the pending process loads.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16));
        hw.SetInstructionPointer(0);

        hw.Run();

        // After the first (trapping) Run the pending process should have loaded.
        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void Div_NegativeOperands_TruncatesTowardZero()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.WriteRegisterAt(0, -7);
        hw.WriteRegisterAt(1, 2);
        hw.WriteBytes(0, Test.Word(Instruction.DIV, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(-3, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Add_IntegerOverflow_WrapsAround()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.WriteRegisterAt(0, int.MaxValue);
        hw.WriteRegisterAt(1, 1);
        hw.WriteBytes(0, Test.Word(Instruction.ADD, 0, 1, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MinValue, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Jmp_MaximumEncodableAddress_IsSixtyFiveThousandFiveThirtyFive()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(70000, os);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.JMP, 0xFF, 0xFF, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(65535, hw.GetInstructionPointer());
    }

    [Fact]
    public void Call_WhenStackPointerTooLow_Throws()
    {
        // ESP = 0 means the pushed return address is written at index -4.
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.WriteRegister(RegisterName.ESP, 0);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.CALL, 0x00, 0x64, 0));
        Assert.Throws<IndexOutOfRangeException>(() => Instruction.Execute(0, hw));
    }

    [Fact]
    public void ReadBytes_PastEndOfMemory_Throws()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(16, os);
        Assert.Throws<IndexOutOfRangeException>(() => hw.ReadBytes(15));
    }

    [Fact]
    public void WriteBytes_PastEndOfMemory_Throws()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(16, os);
        Assert.Throws<IndexOutOfRangeException>(() => hw.WriteBytes(14, new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void ContextSwitch_RoundRobin_CyclesBetweenTwoProcesses()
    {
        // Two processes with distinct register-state contents; after each switch
        // the loaded register file should match the newly-current process.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process first = new Process(CreateProgramFile(program), 16, 16);
        Process second = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);

        // First switch drains both and makes second current.
        os.ContextSwitch(hw);
        // Tag each process's saved register slot with a recognizable marker.
        hw.WriteBytes(first.RegisterStateAddress, new byte[] { 0xAA, 0, 0, 0 });
        hw.WriteBytes(second.RegisterStateAddress, new byte[] { 0xBB, 0, 0, 0 });

        os.ContextSwitch(hw);
        int afterFirstSwitch = hw.ReadRegisters()[0];
        os.ContextSwitch(hw);
        int afterSecondSwitch = hw.ReadRegisters()[0];

        Assert.NotEqual(afterFirstSwitch, afterSecondSwitch);
    }

    [Fact]
    public void DuplicateRegisterNames_LastIndexWins_OrIsConsistent()
    {
        // Hardware maps RegisterName -> index. Passing the full enum is the norm;
        // here we confirm a minimal register set still maps name-based access
        // correctly to the same slot as index-based access.
        FakeOS os = new FakeOS();
        RegisterName[] minimal = new RegisterName[] { RegisterName.EAX, RegisterName.ESP };
        Hardware hw = new Hardware(64, minimal, os);
        hw.WriteRegister(RegisterName.ESP, 123);
        Assert.Equal(123, hw.ReadRegisterAt(1));
    }
}
