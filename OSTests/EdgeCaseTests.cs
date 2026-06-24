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

    private static byte[] Print(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Run_NoProcesses_IdlesWithoutThrowing()
    {
        // With nothing to schedule, the idle Run path asks the scheduler and stays
        // idle rather than crashing.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), os);

        for (int i = 0; i < 50; i++)
        {
            hw.Run();
        }

        Assert.False(os.HasProcesses);
        Assert.False(os.HasRunningProcess);
    }

    [Fact]
    public void LoadProcess_TooBigToFit_LogsFailure()
    {
        // A process larger than any free range fails allocation and is logged,
        // without being scheduled.
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 50000, 50000));

        Assert.Contains("[LOAD FAILED]", log.ToString());
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void FirstRun_BeforeBoot_SchedulesLoadedProcess()
    {
        // When idle, Run asks the scheduler to make a loaded process current; no
        // explicit boot is required.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 1);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 16, 16));

        for (int i = 0; i < 100 && !os.HasRunningProcess; i++)
        {
            hw.Run(); // idle -> Schedule routine makes the process current
        }

        Assert.True(os.HasRunningProcess);
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
    public void Scheduler_RoundRobin_RunsBothProcessesToCompletion()
    {
        // Two independent processes both run under the round-robin scheduler and
        // each produce their output.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(16384), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        os.LoadProcess(new Process(CreateProgramFile(Print(1)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(Print(2)), 128, 64));

        for (int i = 0; i < 4000 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.Contains(1, outputs);
        Assert.Contains(2, outputs);
        Assert.False(os.HasProcesses);
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
