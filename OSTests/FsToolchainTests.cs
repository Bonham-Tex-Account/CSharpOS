using CSharpOS;
using CSharpOSConsole;
using Xunit;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace OSTests;

/// <summary>
/// The in-OS write→compile→run toolchain (§4). §4.0: `/bin/edit` authors a source file from stdin
/// lines; a later `/bin/as` (§4.2) will assemble it and the result is exec'd. These tests drive
/// `/bin/edit` end-to-end through a live scheduler, then read the file it wrote back through the
/// filesystem cores to confirm the content — the same word-per-char convention `/bin/as` will read.
/// </summary>
public class FsToolchainTests
{
    private static int Memory => Test.MachineWithHeap(16384);
    private const int ScratchProc = OsLayout.MaxProcesses - 1;

    private static (BasicOS os, Hardware hw) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        // Drain output so a program that prints never blocks on a busy device.
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => hw.RaiseOutputComplete(e.Device);
        return (os, hw);
    }

    // A parent that FSYS-execs the command line stored word-per-char at offset 64 of its image.
    private static byte[] ExecCmd(string commandLine)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, 64);
        asm.Fsys();
        asm.Hlt();                               // only reached if exec failed
        byte[] code = asm.Build();
        byte[] image = new byte[64 + (commandLine.Length + 1) * 4];
        Array.Copy(code, image, code.Length);
        for (int i = 0; i < commandLine.Length; i++)
        {
            image[64 + i * 4] = (byte)commandLine[i];
        }
        return image;
    }

    private static void RunToCompletion(BasicOS os, Hardware hw)
    {
        for (int i = 0; i < 80000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    // Runs an FsOp core synchronously against hand-set registers and returns the FsResult word.
    private static int FsOp(Hardware hw, int op, int a1, int a2, int a3, int a4)
    {
        hw.WriteRegister(RegisterName.EBX, a1);
        hw.WriteRegister(RegisterName.ECX, a2);
        hw.WriteRegister(RegisterName.EDX, a3);
        hw.WriteRegister(RegisterName.ESI, a4);
        hw.RunOsRoutineSynchronously(op == Hardware.IvtFsOp ? op : Hardware.IvtFsOp, op);
        return Test.ReadWord(hw, OsLayout.FsResultOffset);
    }

    // Reads a whole file back through the FS cores (absolute buffer, scratch proc) and decodes the
    // word-per-char content to a string. Independent of any user program, so it verifies exactly
    // what landed in the filesystem.
    private static string ReadFile(Hardware hw, string path)
    {
        int pathAddr = OsLayout.InstallPathBase;
        for (int i = 0; i < path.Length; i++) { Test.WriteWord(hw, pathAddr + i * 4, path[i]); }
        Test.WriteWord(hw, pathAddr + path.Length * 4, 0);

        int fd = FsOp(hw, Hardware.FsOpOpen, pathAddr, 0, ScratchProc, 0);
        if (fd < 0)
        {
            return "<open-failed>";
        }
        int bufAddr = OsLayout.InstallBufBase;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        while (true)
        {
            int n = FsOp(hw, Hardware.FsOpRead, fd, bufAddr, OsLayout.InstallBufWords, ScratchProc);
            if (n <= 0)
            {
                break;
            }
            for (int i = 0; i < n; i++)
            {
                sb.Append((char)(Test.ReadWord(hw, bufAddr + i * 4) & 0xFF));
            }
        }
        FsOp(hw, Hardware.FsOpClose, fd, ScratchProc, 0, 0);
        return sb.ToString();
    }

    // Execs `/bin/edit <path>`, feeds the given lines (a lone "." ends input), and runs to completion.
    private static void RunEdit(BasicOS os, Hardware hw, string path, params string[] lines)
    {
        os.LoadProcess(new Process(hw.Disk.Store(ExecCmd("/bin/edit " + path)), 1024, 128));
        hw.SetActiveProcess(0);                          // stdin routes to the (only) process
        foreach (string line in lines)
        {
            hw.RaiseStringInputInterrupt(line);
        }
        hw.RaiseStringInputInterrupt(".");               // end-of-input sentinel
        RunToCompletion(os, hw);
    }

    [Fact]
    public void Edit_WritesTypedLinesToAFile_EachNewlineTerminated()
    {
        (BasicOS os, Hardware hw) = NewMachine();
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/edit", Programs.Edit());

        RunEdit(os, hw, "/src.s", "MOV EAX 5", "OUT EAX");
        Assert.False(os.HasProcesses);

        // The file holds exactly the two lines, each terminated by a newline.
        Assert.Equal("MOV EAX 5\nOUT EAX\n", ReadFile(hw, "/src.s"));
    }

    [Fact]
    public void Edit_EmptyInput_CreatesAnEmptyFile()
    {
        (BasicOS os, Hardware hw) = NewMachine();
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/edit", Programs.Edit());

        RunEdit(os, hw, "/empty.s");                      // just the "." sentinel, no lines
        Assert.False(os.HasProcesses);

        Assert.True(FsImage.ResolveFirstBlock(hw, "/empty.s") >= FsLayout.FirstDataBlock);  // exists
        Assert.Equal("", ReadFile(hw, "/empty.s"));                                          // but empty
    }

    [Fact]
    public void Edit_PreservesBlankLines()
    {
        (BasicOS os, Hardware hw) = NewMachine();
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/edit", Programs.Edit());

        RunEdit(os, hw, "/gap.s", "a", "", "b");          // a blank line between two content lines
        Assert.False(os.HasProcesses);

        Assert.Equal("a\n\nb\n", ReadFile(hw, "/gap.s"));
    }
}
