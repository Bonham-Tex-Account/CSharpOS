using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers the event system added for visualization: Hardware fires
/// InstructionExecuted / MemoryWritten / InvalidInstruction / ProgramOutput,
/// and OperatingSystem fires ContextSwitched / InvalidInstruction.
/// These assert intended behavior; some may fail and surface real bugs.
/// </summary>
public class EventTests : IDisposable
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

    // ---- Hardware events ------------------------------------------------

    [Fact]
    public void Run_FiresInstructionExecuted_WithAddressAndInstructionBytes()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        InstructionExecutedArgs? captured = null;
        hw.InstructionExecuted += (object? sender, InstructionExecutedArgs e) => { captured = e; };
        hw.WriteBytes(8, Test.Word(Instruction.MOV_REG_IMM, 0, 42, 0));
        hw.SetInstructionPointer(8);

        hw.Run();

        Assert.NotNull(captured);
        Assert.Equal(8, captured!.Address);
        Assert.Equal(Instruction.MOV_REG_IMM, captured.Opcode);
        Assert.Equal(0, captured.B1);
        Assert.Equal(42, captured.B2);
        Assert.Equal(0, captured.B3);
    }

    [Fact]
    public void WriteBytes_FiresMemoryWritten_WithAddressAndData()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        MemoryWrittenArgs? captured = null;
        hw.MemoryWritten += (object? sender, MemoryWrittenArgs e) => { captured = e; };
        byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        hw.WriteBytes(40, data);

        Assert.NotNull(captured);
        Assert.Equal(40, captured!.Address);
        Assert.Equal(data, captured.Data);
    }

    [Fact]
    public void WriteBytes_MemoryWrittenData_IsDefensiveCopy()
    {
        // The event arg should snapshot the bytes; mutating the caller's array
        // afterwards must not change what a subscriber observed.
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        MemoryWrittenArgs? captured = null;
        hw.MemoryWritten += (object? sender, MemoryWrittenArgs e) => { captured = e; };
        byte[] data = new byte[] { 1, 2, 3, 4 };

        hw.WriteBytes(0, data);
        data[0] = 0xFF;

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.Data[0]);
    }

    [Fact]
    public void Store_FiresMemoryWritten()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        int count = 0;
        hw.MemoryWritten += (object? sender, MemoryWrittenArgs e) => { count++; };
        hw.WriteRegisterAt(1, 40);   // pointer
        hw.WriteRegisterAt(0, 1337); // value
        hw.WriteBytes(0, Test.Word(Instruction.STORE, 1, 0, 0));
        count = 0; // ignore the write that loaded the instruction itself

        Instruction.Execute(0, hw);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Run_InvalidOpcode_FiresHardwareInvalidInstructionEvent()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };
        hw.WriteBytes(0, Test.Word(0xFF, 1, 2, 3));
        hw.SetInstructionPointer(0);

        hw.Run();

        Assert.NotNull(captured);
        Assert.Equal(0xFF, captured!.Opcode);
        Assert.Equal(1, captured.B1);
        Assert.Equal(2, captured.B2);
        Assert.Equal(3, captured.B3);
    }

    [Fact]
    public void Run_InvalidOpcode_DoesNotAlsoFireInstructionExecuted()
    {
        // An instruction that trapped as invalid arguably should not also be
        // reported as successfully executed. Documents intended behavior;
        // currently Run fires InstructionExecuted unconditionally after Execute.
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        bool executedFired = false;
        hw.InstructionExecuted += (object? sender, InstructionExecutedArgs e) => { executedFired = true; };
        hw.WriteBytes(0, Test.Word(0xFF, 0, 0, 0));
        hw.SetInstructionPointer(0);

        hw.Run();

        Assert.False(executedFired);
    }

    [Fact]
    public void Output_FiresProgramOutput_WithValue()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        ProgramOutputArgs? captured = null;
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { captured = e; };

        hw.Output(12345);

        Assert.NotNull(captured);
        Assert.Equal(12345, captured!.Value);
    }

    // ---- Hardware-fired scheduling/observability events ----------------

    private static byte[] MovThenHalt()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 1);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Scheduler_FiresContextSwitched_OnFirstResume_WithNoPriorProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        string path = CreateProgramFile(MovThenHalt());
        os.LoadProcess(new Process(path, 128, 64));

        ContextSwitchArgs? captured = null;
        hw.ContextSwitched += (object? sender, ContextSwitchArgs e) => { captured ??= e; };

        for (int i = 0; i < 200 && captured == null; i++)
        {
            hw.Run();
        }

        Assert.NotNull(captured);
        Assert.Equal(-1, captured!.FromProgramBase);   // nothing was running before
        Assert.Equal(path, os.NameForBase(captured.ToProgramBase));
    }

    [Fact]
    public void Scheduler_FiresContextSwitched_WithDistinctProcessesAcrossSwitches()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        // Two long-running processes so the scheduler round-robins between them.
        string firstPath = CreateProgramFile(LoopForever());
        string secondPath = CreateProgramFile(LoopForever());
        os.LoadProcess(new Process(firstPath, 128, 64));
        os.LoadProcess(new Process(secondPath, 128, 64));

        HashSet<int> resumedBases = new HashSet<int>();
        hw.ContextSwitched += (object? sender, ContextSwitchArgs e) => { resumedBases.Add(e.ToProgramBase); };

        for (int i = 0; i < 200 && resumedBases.Count < 2; i++)
        {
            hw.Run();
        }

        // Both processes were resumed at least once, identifiable by name.
        Assert.Equal(2, resumedBases.Count);
        List<string?> names = resumedBases.Select(os.NameForBase).ToList();
        Assert.Contains(firstPath, names);
        Assert.Contains(secondPath, names);
    }

    [Fact]
    public void Run_PrivilegeTrap_FiresHardwareInvalidInstruction_WithReason()
    {
        // A user-mode IRET violates BasicOS's privilege trap; Hardware fires the
        // fault event carrying the trap's reason.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        Assembler asm = new Assembler();
        asm.Iret(); // privileged: traps in user mode
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };

        for (int i = 0; i < 200 && captured == null; i++)
        {
            hw.Run();
        }

        Assert.NotNull(captured);
        Assert.Equal(Instruction.IRET, captured!.Opcode);
        Assert.Equal("IRET is a privileged instruction", captured.Reason);
    }

    private static byte[] LoopForever()
    {
        Assembler asm = new Assembler();
        asm.Label("top");
        asm.MovImm(RegisterName.EAX, 1);
        asm.Jmp("top");
        return asm.Build();
    }
}
