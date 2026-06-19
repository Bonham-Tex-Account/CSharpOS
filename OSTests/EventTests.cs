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

    // ---- OperatingSystem events ----------------------------------------

    [Fact]
    public void ContextSwitch_FiresContextSwitched_FirstSwitchHasNullFromProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        string path = CreateProgramFile(new byte[] { 0, 0, 0, 0 });
        os.LoadProcess(new Process(path, 16, 16));
        ContextSwitchArgs? captured = null;
        os.ContextSwitched += (object? sender, ContextSwitchArgs e) => { captured = e; };

        os.ContextSwitch(hw);

        Assert.NotNull(captured);
        Assert.Null(captured!.FromProcess);
        Assert.Equal(path, captured.ToProcess);
    }

    [Fact]
    public void ContextSwitch_FiresContextSwitched_WithFromAndToProcessNames()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        string firstPath = CreateProgramFile(new byte[] { 0, 0, 0, 0 });
        string secondPath = CreateProgramFile(new byte[] { 0, 0, 0, 0 });
        os.LoadProcess(new Process(firstPath, 16, 16));
        os.LoadProcess(new Process(secondPath, 16, 16));

        List<ContextSwitchArgs> switches = new List<ContextSwitchArgs>();
        os.ContextSwitched += (object? sender, ContextSwitchArgs e) => { switches.Add(e); };

        os.ContextSwitch(hw); // null -> second
        os.ContextSwitch(hw); // second -> first

        Assert.Equal(2, switches.Count);
        Assert.Equal(secondPath, switches[0].ToProcess);
        Assert.Equal(secondPath, switches[1].FromProcess);
        Assert.Equal(firstPath, switches[1].ToProcess);
    }

    [Fact]
    public void HandleInvalidInstruction_FiresOsInvalidInstruction_WithProcessNameAndReason()
    {
        StringWriter log = new StringWriter();
        List<Trap> traps = new List<Trap>
        {
            new Trap { Opcode = 0xFF, Reason = "custom trap reason", Condition = (Hardware h, byte a, byte b, byte c) => true }
        };
        TrappingOS os = new TrappingOS(traps, log);
        Hardware hw = Test.NewHardware(1024, os);
        string path = CreateProgramFile(new byte[] { 0, 0, 0, 0 });
        os.LoadProcess(new Process(path, 16, 16));
        os.ContextSwitch(hw); // make the process current

        InvalidInstructionArgs? captured = null;
        os.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };

        os.HandleInvalidInstruction(hw, 0xFF, 1, 2, 3);

        Assert.NotNull(captured);
        Assert.Equal(0xFF, captured!.Opcode);
        Assert.Equal(path, captured.ProcessName);
        Assert.Equal("custom trap reason", captured.Reason);
    }
}
