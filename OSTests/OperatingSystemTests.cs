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
        // Register state sits after the program and the reserved kernel section.
        Assert.Equal(program.Length + os.KernelImage.Length, process.RegisterStateAddress);
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

        // total per process = program(4) + kernel section(128) + memory(16) +
        // user stack(16) + kernel stack(64)
        int perProcess = 4 + os.KernelImage.Length + 16 + 16 + Hardware.KernelStackSize;
        Assert.Equal(0, first.ProgramAddress);
        Assert.Equal(perProcess, second.ProgramAddress);
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
    public void LoadedProcess_HasStackPointerInitializedToUserStackTop()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process process = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(process);

        // The first context switch drains the pending process and loads its saved
        // register state (including the seeded ESP) into the registers.
        os.ContextSwitch(hw);

        int expectedTop = program.Length + os.KernelImage.Length + process.RequiredMemory + process.RequiredStackSize;
        Assert.Equal(expectedTop, hw.ReadRegister(RegisterName.ESP));
    }

    [Fact]
    public void KernelImage_SizesAndFillsTheKernelSection()
    {
        // The kernel section scales with the OS's kernel image, and the image is
        // copied into each process's kernel section (right after the program).
        byte[] image = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };
        KernelImageOS os = new KernelImageOS(new StringWriter(), image);
        Hardware hw = Test.NewHardware(1024, os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process process = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(process);

        os.ContextSwitch(hw);

        // Register state sits after the program + the kernel section (= image length).
        Assert.Equal(program.Length + image.Length, process.RegisterStateAddress);
        // The image bytes occupy the kernel section, just past the program.
        int sectionStart = process.ProgramAddress + program.Length;
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, hw.ReadBytes(sectionStart));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, hw.ReadBytes(sectionStart + 4));
    }

    [Fact]
    public void NewlyLoadedProcess_StartsInUserMode()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16));

        os.ContextSwitch(hw);

        Assert.False(hw.IsKernelMode());
    }

    [Fact]
    public void ContextSwitch_SavesAndRestoresPerProcessMode()
    {
        // Mode is per-process state saved in process memory. If one process is in
        // kernel mode when switched out, that mode must be restored when it next runs.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = Test.NewHardware(1024, os);
        Process first = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        Process second = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);

        os.ContextSwitch(hw);   // second becomes current (user mode)
        hw.EnterKernelMode();   // simulate the current process entering the kernel

        os.ContextSwitch(hw);   // first becomes current; its saved mode is user
        Assert.False(hw.IsKernelMode());

        os.ContextSwitch(hw);   // second again; its kernel mode must be restored
        Assert.True(hw.IsKernelMode());
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
