using CSharpOS;

namespace OSTests;

public class OperatingSystemTests : IDisposable
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
    public void HasProcesses_FalseWhenEmpty()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void LoadProcess_EnqueuesProcess_MakesHasProcessesTrue()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        os.LoadProcess(process);
        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void ContextSwitch_DrainsPending_AssignsProcessAddresses()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 1, 2, 3, 4 };
        Process process = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(process);

        os.ContextSwitch(hw);

        Assert.Equal(0, process.ProgramAddress);
        Assert.Equal(program.Length, process.RegisterStateAddress);
    }

    [Fact]
    public void ContextSwitch_TwoProcesses_AllocatesNonOverlappingRegions()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process first = new Process(CreateProgramFile(program), 16, 16);
        Process second = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);

        os.ContextSwitch(hw);

        // total per process = program(4) + memory(16) + stack(16) = 36
        Assert.Equal(0, first.ProgramAddress);
        Assert.Equal(36, second.ProgramAddress);
    }

    [Fact]
    public void DrainPending_NotEnoughMemory_LogsFailureWithoutCrashing()
    {
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process fits = new Process(CreateProgramFile(program), 16, 16);
        Process tooBig = new Process(CreateProgramFile(program), 5000, 5000);
        os.LoadProcess(fits);
        os.LoadProcess(tooBig);

        os.ContextSwitch(hw);

        Assert.Contains("[LOAD FAILED]", log.ToString());
    }

    [Fact]
    public void HandleInvalidInstruction_RemovesCurrentProcess()
    {
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        os.LoadProcess(process);
        os.ContextSwitch(hw);

        os.HandleInvalidInstruction(hw, 0xFF, 0, 0, 0);

        Assert.False(os.HasProcesses);
        Assert.Contains("[INVALID INSTRUCTION]", log.ToString());
    }

    [Fact]
    public void HandleInvalidInstruction_UsesMatchingTrapReason()
    {
        StringWriter log = new StringWriter();
        List<Trap> traps = new List<Trap>
        {
            new Trap { Opcode = 0xFF, Reason = "custom trap reason", Condition = (Hardware h, byte a, byte b, byte c) => true }
        };
        TrappingOS os = new TrappingOS(traps, log);
        Hardware hw = Test.NewHardware(1024, os);

        os.HandleInvalidInstruction(hw, 0xFF, 0, 0, 0);

        Assert.Contains("custom trap reason", log.ToString());
    }

    [Fact]
    public void HandleInvalidInstruction_UnknownOpcode_UsesDefaultReason()
    {
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(1024, os);

        os.HandleInvalidInstruction(hw, 0x99, 0, 0, 0);

        Assert.Contains("Unknown invalid instruction", log.ToString());
    }
}
