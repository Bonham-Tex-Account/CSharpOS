using CSharpOS;
using CSharpOSConsole;

namespace OSTests;

/// <summary>
/// Phase 4: boot loads programs from the filesystem. LoadProcess installs each program image
/// into the FS (under /bin) and creates the process FS-backed (DiskSlot = -1, FirstBlock = the
/// file's first block); IvtSpawn chain-loads it via fs_load_image instead of DREADing a disk
/// slot. These end-to-end tests prove an FS-backed process runs, that fork of one works, that
/// EXEC(slot) still switches a process back to slot-backed, and that installed programs survive
/// a Bin.Save/Load round-trip.
/// </summary>
public class FsBootTests
{
    private static int Memory => Test.MachineWithHeap(16384);

    private static List<int> CaptureOutputs(Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            outputs.Add(e.Value);
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    private static void Run(CSharpOS.OperatingSystem os, Hardware hw, int cap = 40000)
    {
        for (int i = 0; i < cap && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    // OUT a value, then HLT.
    private static byte[] PrintAndHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void LoadProcess_InstallsProgramIntoTheFilesystem_ProcessRunsFsBacked()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        int slot = hw.Disk.Store(PrintAndHalt(77));
        os.LoadProcess(new Process(slot, 512, 64));

        // Inspect the entry BEFORE running: FS-backed means DiskSlot = -1 and a real FirstBlock.
        int entry = OsLayout.ProcessEntryAddress(0);
        int diskSlot = Test.ReadWord(hw, entry + Hardware.ProcessEntryDiskSlot);
        int firstBlock = Test.ReadWord(hw, entry + Hardware.ProcessEntryFirstBlock);
        Assert.Equal(-1, diskSlot);
        Assert.True(firstBlock >= FsLayout.FirstDataBlock, $"FirstBlock={firstBlock} should be a data block");

        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains(77, outputs);
    }

    [Fact]
    public void LoadProcess_TwoPrograms_InstallToDistinctBinFilesThatBothResolve()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(hw.Disk.Store(PrintAndHalt(11)), 512, 64));
        os.LoadProcess(new Process(hw.Disk.Store(PrintAndHalt(22)), 512, 64));

        // Both installs got their own /bin file (monotonic p<seq>), and both resolve.
        Assert.True(FsImage.ResolveFirstBlock(hw, "/bin/p0") >= FsLayout.FirstDataBlock);
        Assert.True(FsImage.ResolveFirstBlock(hw, "/bin/p1") >= FsLayout.FirstDataBlock);
        // A never-installed name does not resolve.
        Assert.Equal(-1, FsImage.ResolveFirstBlock(hw, "/bin/p9"));

        Run(os, hw);

        Assert.Contains(11, outputs);
        Assert.Contains(22, outputs);
    }

    // Store a marker to EBX-addressed memory is unnecessary; this program simply FORKs, both
    // parent and child OUT their EAX (child sees 0, parent sees the child PID), then HLT.
    private static byte[] ForkBothPrint()
    {
        Assembler asm = new Assembler();
        asm.Fork();                       // child: EAX = 0; parent: EAX = child PID (>0)
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Fork_OfAnFsBackedProcess_ChildInheritsAndBothRun()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(hw.Disk.Store(ForkBothPrint()), 512, 128));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        // Child prints 0; parent prints the child's PID (nonzero). Both ran off the same
        // FS-backed image (the child inherited FirstBlock via ForkCopyField).
        Assert.Contains(0, outputs);
        Assert.Contains(outputs, v => v > 0);
    }

    [Fact]
    public void ExecSlot_FromAnFsBackedProcess_SwitchesToSlotBackedAndRuns()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        // Target program (slot-based) that OUTs 99 and halts.
        int targetSlot = hw.Disk.Store(PrintAndHalt(99));

        // Launcher: EXEC the target slot. Loaded FS-backed by LoadProcess; EXEC(slot) must reset
        // it to slot-backed (FirstBlock = -1, DiskSlot = targetSlot) and run the new image.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.ECX, targetSlot);
        asm.Exec(RegisterName.ECX);
        asm.Hlt();
        os.LoadProcess(new Process(hw.Disk.Store(asm.Build()), 512, 64));

        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains(99, outputs);
    }

    [Fact]
    public void FilesystemDemoProgram_WritesAndReadsAFileViaFsys_PrintsItBack()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<string?> strings = new List<string?>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            strings.Add(e.StringValue);
            hw.RaiseOutputComplete(e.Device);
        };

        os.LoadProcess(new Process(hw.Disk.Store(Programs.FilesystemDemo()), 128, 64));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains("HI!", strings);
    }

    [Fact]
    public void InstalledProgram_PersistsAcrossBinSaveLoad()
    {
        byte[] program = PrintAndHalt(55);
        string binPath = Path.Combine(Path.GetTempPath(), $"fsboot_{Guid.NewGuid():N}.bin");
        try
        {
            // Machine 1: install the program (via LoadProcess), flush the write-back cache so the
            // dirty FS blocks reach the disk's file-block region, then persist.
            BasicOS os1 = new BasicOS(new StringWriter());
            Hardware hw1 = new Hardware(Memory, Test.AllRegisters(), os1);
            os1.LoadProcess(new Process(hw1.Disk.Store(program), 512, 64));
            hw1.RunOsRoutineSynchronously(Hardware.IvtCacheOp, Hardware.CacheOpFlush);
            hw1.Disk.Save(binPath);

            // Machine 2: boot on the persisted disk. OnBooted sees the FS magic and does NOT
            // reformat, so the installed file survives.
            Bin loaded = new Bin(
                Hardware.DefaultDiskSlots + OsLayout.SwapSlotCount,
                Hardware.DefaultDiskSlotSize,
                Hardware.DefaultFileBlockCount,
                Hardware.DefaultFileBlockSize);
            loaded.Load(binPath);
            BasicOS os2 = new BasicOS(new StringWriter());
            Hardware hw2 = new Hardware(Memory, Test.AllRegisters(), os2, loaded);

            // The directory entry persisted...
            int firstBlock = FsImage.ResolveFirstBlock(hw2, "/bin/p0");
            Assert.True(firstBlock >= FsLayout.FirstDataBlock, $"FirstBlock={firstBlock}");

            // ...and so did the content: the first block holds the program's words verbatim.
            byte[] block = hw2.Disk.ReadFileBlock(firstBlock);
            for (int i = 0; i < program.Length; i++)
            {
                Assert.Equal(program[i], block[i]);
            }
        }
        finally
        {
            if (File.Exists(binPath))
            {
                File.Delete(binPath);
            }
        }
    }
}
