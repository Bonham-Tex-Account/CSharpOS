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
        return Test.ReadWord(hw, address);
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

        int perProcess = program.Length
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

        int expectedTop = process.ProgramAddress + program.Length
            + process.RequiredMemory + process.RequiredStackSize;
        int entry = OsLayout.ProcessEntryAddress(0);
        // ESP is stored as an offset from the program base (position-independent model).
        Assert.Equal(expectedTop - process.ProgramAddress, ReadWord(hw, entry + hw.GetRegisterOffset(RegisterName.ESP)));
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

    [Fact]
    public void LoadProcess_AssignsMonotonicUniquePids_StartingAtOne()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        byte[] program = new byte[] { 0, 0, 0, 0 };

        Process first = new Process(CreateProgramFile(program), 16, 16);
        Process second = new Process(CreateProgramFile(program), 16, 16);
        Process third = new Process(CreateProgramFile(program), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);
        os.LoadProcess(third);

        Assert.Equal(1, first.Pid);
        Assert.Equal(2, second.Pid);
        Assert.Equal(3, third.Pid);

        // The PID is also recorded in the process-table entry, with no parent / wait target.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1, ReadWord(hw, entry0 + Hardware.ProcessEntryPid));
        Assert.Equal(-1, ReadWord(hw, entry0 + Hardware.ProcessEntryParentPid));
        Assert.Equal(-1, ReadWord(hw, entry0 + Hardware.ProcessEntryWaitTarget));
    }

    [Fact]
    public void Spawn_SeedsRegistersStateAndPid_InIsa()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        byte[] program = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }; // 8-byte image
        Process process = new Process(CreateProgramFile(program), 64, 64);
        os.LoadProcess(process);

        // IvtSpawn seeds the saved register file: EIP offset 0 (program start) and ESP
        // offset = TotalSize - KernelStackSize (top of the user stack, base-relative).
        int entry = OsLayout.ProcessEntryAddress(0);
        int total = ReadWord(hw, entry + Hardware.ProcessEntryTotalSize);
        Assert.Equal(0, ReadWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP)));
        Assert.Equal(total - Hardware.KernelStackSize, ReadWord(hw, entry + hw.GetRegisterOffset(RegisterName.ESP)));

        // ...and the scheduling/identity state.
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal((int)PrivilegeLevel.User, ReadWord(hw, entry + Hardware.ProcessEntryLevel));
        Assert.Equal(0, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(1, ReadWord(hw, entry + Hardware.ProcessEntryPid));
        Assert.Equal(-1, ReadWord(hw, entry + Hardware.ProcessEntryParentPid));
    }

    [Fact]
    public void LoadProcess_SlotBased_CopiesTheDiskImageIntoRamViaDread()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 7);
        asm.Hlt();
        byte[] image = asm.Build();

        int slot = hw.Disk.Store(image);
        Process process = new Process(slot, 16, 16);
        os.LoadProcess(process);

        // The allocator's DREAD placed the program image at the allocated address.
        Assert.Equal(image.Length, process.ProgramSize);
        byte[] inRam = new byte[image.Length];
        for (int i = 0; i < image.Length; i++)
        {
            inRam[i] = hw.ReadBytes(process.ProgramAddress + i)[0];
        }
        Assert.Equal(image, inRam);

        // And it actually runs (schedules into User mode).
        for (int i = 0; i < 100 && !os.HasRunningProcess; i++)
        {
            hw.Run();
        }
        Assert.True(os.HasRunningProcess);
    }

    [Fact]
    public void LoadProcess_SlotBasedAndFilePath_ProduceIdenticalRamImages()
    {
        byte[] program = new byte[] { 0x02, 0x00, 0x05, 0x00, 0x32, 0x00, 0x00, 0x00 };

        // File-path process: auto-stages to a disk slot, then loads from it.
        BasicOS fileOs = new BasicOS(new StringWriter());
        Hardware fileHw = new Hardware(Memory, Test.AllRegisters(), fileOs);
        Process fileProcess = new Process(CreateProgramFile(program), 16, 16);
        fileOs.LoadProcess(fileProcess);

        // Slot-based process: the image is staged explicitly first.
        BasicOS slotOs = new BasicOS(new StringWriter());
        Hardware slotHw = new Hardware(Memory, Test.AllRegisters(), slotOs);
        int slot = slotHw.Disk.Store(program);
        Process slotProcess = new Process(slot, 16, 16);
        slotOs.LoadProcess(slotProcess);

        Assert.Equal(fileProcess.ProgramSize, slotProcess.ProgramSize);
        for (int i = 0; i < program.Length; i++)
        {
            byte fromFile = fileHw.ReadBytes(fileProcess.ProgramAddress + i)[0];
            byte fromSlot = slotHw.ReadBytes(slotProcess.ProgramAddress + i)[0];
            Assert.Equal(fromFile, fromSlot);
        }
    }
}
