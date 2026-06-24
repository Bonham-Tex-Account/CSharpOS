using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers BasicOS loading programs into the OS memory region via the ISA allocator
/// and seeding process-table entries, plus the liveness queries the run loop uses.
/// The scheduling/allocation algorithms themselves are covered by the OS-routine
/// isolation tests; these check the C# loader integration and end-to-end behavior.
/// </summary>
public class OperatingSystemTests : IDisposable
{
    private static int Memory => Test.MinMachineSize;
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

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    [Fact]
    public void HasProcesses_FalseBeforeAnyLoad()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void LoadProcess_MakesHasProcessesTrue()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16));
        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void LoadProcess_AllocatesAboveTheOsRegion()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Process process = new Process(CreateProgramFile(new byte[] { 1, 2, 3, 4 }), 16, 16);
        os.LoadProcess(process);

        // The first free range starts just past the reserved OS region.
        Assert.Equal(os.OsMemorySize, process.ProgramAddress);
    }

    [Fact]
    public void LoadProcess_TwoProcesses_GetNonOverlappingRegions()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process first = new Process(CreateProgramFile(program), 16, 16);
        Process second = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);

        int perProcess = program.Length + (Hardware.KernelHeaderSize + os.KernelImage.Length)
            + first.RequiredMemory + first.RequiredStackSize + Hardware.KernelStackSize;
        Assert.Equal(os.OsMemorySize, first.ProgramAddress);
        // The buddy allocator rounds up to the next power-of-2 block size, so the second
        // process lands at first + blockSize (>= perProcess), not first + perProcess exactly.
        Assert.True(second.ProgramAddress >= first.ProgramAddress + perProcess,
            "Second process must not overlap first");
        Assert.True(second.ProgramAddress < first.ProgramAddress + 2 * perProcess * 4,
            "Second process must be allocated near the heap start, not arbitrarily far away");
    }

    [Fact]
    public void LoadProcess_NotEnoughMemory_LogsFailure()
    {
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 50000, 50000));

        Assert.Contains("[LOAD FAILED]", log.ToString());
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void LoadProcess_SeedsStackPointerAtTopOfUserStack()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process process = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(process);

        int kernelSection = Hardware.KernelHeaderSize + os.KernelImage.Length;
        int expectedTop = process.ProgramAddress + program.Length + kernelSection
            + process.RequiredMemory + process.RequiredStackSize;
        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(expectedTop, ReadWord(hw, entry + hw.GetRegisterOffset(RegisterName.ESP)));
    }

    [Fact]
    public void LoadProcess_CopiesKernelImageIntoTheKernelSection()
    {
        byte[] image = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };
        KernelImageOS os = new KernelImageOS(new StringWriter(), image);
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        Process process = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(process);

        int imageStart = process.ProgramAddress + program.Length + Hardware.KernelHeaderSize;
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, hw.ReadBytes(imageStart));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, hw.ReadBytes(imageStart + 4));
    }

    [Fact]
    public void ScheduledProcess_StartsInUserMode()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 1);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 16, 16));

        for (int i = 0; i < 100 && !os.HasRunningProcess; i++)
        {
            hw.Run();
        }

        Assert.True(os.HasRunningProcess);
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void InvalidInstruction_TerminatesTheProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0xFF, 0, 0, 0 }), 16, 16));

        for (int i = 0; i < 500 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.False(os.HasProcesses);
    }
}
