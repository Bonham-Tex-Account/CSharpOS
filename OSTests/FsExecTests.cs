using CSharpOS;

namespace OSTests;

/// <summary>
/// End-to-end tests for Increment 6: launching a program stored as a filesystem file via the
/// FSYS Exec-by-path syscall. A parent process is loaded from a disk image slot, then execs a
/// program that was placed in the FS with <see cref="FsImage.WriteFile"/>; on success the
/// parent's image is replaced by the file's and the new program runs. Also covers the boot
/// auto-format (no test formats the disk here) and the failure paths (missing path, directory).
/// </summary>
public class FsExecTests
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

    // A standalone program that OUTs `value` and halts — the "child" image stored in the FS.
    private static byte[] PrintAndHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // A "parent" program that FSYS-execs the file at `path` (stored word-per-char at offset 64
    // of its own image). On success the exec never returns; on failure it OUTs the -1 result
    // and halts, so a test can observe the failure.
    private static byte[] ExecPath(string path)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, 64);      // user pointer to the path
        asm.Fsys();
        asm.Out(RegisterName.EAX);               // only reached if exec failed (result = -1)
        asm.Hlt();
        byte[] code = asm.Build();

        byte[] image = new byte[64 + (path.Length + 1) * 4];
        Array.Copy(code, image, code.Length);
        for (int i = 0; i < path.Length; i++)
        {
            image[64 + i * 4] = (byte)path[i];
        }
        return image;
    }

    private static (BasicOS os, Hardware hw, List<int> outputs) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        return (os, hw, outputs);
    }

    private static void RunToCompletion(BasicOS os, Hardware hw)
    {
        for (int i = 0; i < 40000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    [Fact]
    public void ExecByPath_RunsTheProgramStoredInTheFilesystem()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        // The FS is already formatted by boot; place a program in it, then exec it by name.
        FsImage.WriteFile(hw, "/child", PrintAndHalt(42));

        int slot = hw.Disk.Store(ExecPath("/child"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 42 }, outputs);   // child ran; parent's failure OUT never fired
    }

    [Fact]
    public void ExecByPath_MultiBlockProgram_RunsCorrectly()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        // Pad the child image beyond one file block (CharsPerBlock*4 = 252 bytes) so exec must
        // walk a multi-block chain to reconstruct the image. The program still just prints 7:
        // NOTs on a fresh register are harmless padding that leaves the OUT value untouched.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 7);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        for (int i = 0; i < 80; i++)
        {
            asm.Not(RegisterName.R15);   // dead padding after HLT; grows the image past one block
        }
        byte[] big = asm.Build();
        Assert.True(big.Length > FsLayout.CharsPerBlock * 4);

        FsImage.WriteFile(hw, "/big", big);

        int slot = hw.Disk.Store(ExecPath("/big"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 7 }, outputs);
    }

    [Fact]
    public void ExecByPath_MissingFile_ReturnsMinusOneAndKeepsRunning()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        int slot = hw.Disk.Store(ExecPath("/nope"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1 }, outputs);   // exec failed; parent survived to OUT -1
    }

    [Fact]
    public void ExecByPath_Directory_ReturnsMinusOne()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        // Make "/dir" a directory (not a file), then try to exec it.
        int root = RootDir(hw);
        int nameAddr = OsLayout.TotalSize;             // scratch name, word-per-char
        WriteName(hw, nameAddr, "dir");
        hw.WriteRegister(RegisterName.EBX, root);
        hw.WriteRegister(RegisterName.ECX, nameAddr);
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, Hardware.FsOpMkdir);

        int slot = hw.Disk.Store(ExecPath("/dir"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1 }, outputs);
    }

    private static int RootDir(Hardware hw)
    {
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, Hardware.FsOpRootDir);
        return Test.ReadWord(hw, OsLayout.FsResultOffset);
    }

    private static void WriteName(Hardware hw, int addr, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            Test.WriteWord(hw, addr + i * 4, name[i]);
        }
        Test.WriteWord(hw, addr + name.Length * 4, 0);
    }
}
