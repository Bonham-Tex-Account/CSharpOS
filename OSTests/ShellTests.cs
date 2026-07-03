using CSharpOS;
using CSharpOSConsole;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers SETFOCUS (map a PID to the foreground process) and the shell program (Shell §2): the
/// shell prompts, reads a command line (INS), and exec-by-paths the typed command with its args
/// (FSYS Exec). Commands are absolute paths; a command that does not resolve prints "?". v1 is
/// single-shot (the shell becomes the command); a looping shell is deferred (see Programs.Shell).
/// </summary>
public class ShellTests : IDisposable
{
    private static int Memory => Test.MachineWithHeap(16384);
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

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void SetFocus_MapsPidToTheForegroundProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 64, 64)); // PID 1, slot 0
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 64, 64)); // PID 2, slot 1

        Assert.Equal(-1, hw.GetActiveProcess());

        hw.SetFocus(2); // focus the process with PID 2 (slot 1)
        Assert.Equal(1, hw.GetActiveProcess());

        hw.SetFocus(1); // focus PID 1 (slot 0)
        Assert.Equal(0, hw.GetActiveProcess());

        hw.SetFocus(99); // unknown PID: focus unchanged
        Assert.Equal(0, hw.GetActiveProcess());
    }

    private static (List<int> ints, List<string?> strings) CaptureAll(Hardware hw)
    {
        List<int> ints = new List<int>();
        List<string?> strings = new List<string?>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (e.StringValue != null)
            {
                strings.Add(e.StringValue);
            }
            else
            {
                ints.Add(e.Value);
            }
            hw.RaiseOutputComplete(e.Device);
        };
        return (ints, strings);
    }

    [Fact]
    public void Shell_RunsATypedCommand_ByPath()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, List<string?> strings) = CaptureAll(hw);

        // Install a command program in /bin, then load the shell.
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/p", PrintThenHalt(55));
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        // Let the (focused) shell reach its INS prompt, then type the command line; the shell
        // exec-by-paths it, becoming /bin/p, which prints 55.
        for (int i = 0; i < 3000; i++)
        {
            hw.Run();
        }
        hw.RaiseStringInputInterrupt("/bin/p");
        for (int i = 0; i < 60000 && !ints.Contains(55); i++)
        {
            hw.Run();
        }

        Assert.Contains(55, ints);   // the typed command ran via the shell's exec-by-path
        _ = strings;
    }

    [Fact]
    public void Shell_UnknownCommand_PrintsError()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (_, List<string?> strings) = CaptureAll(hw);

        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));
        hw.SetActiveProcess(0);

        for (int i = 0; i < 3000; i++)
        {
            hw.Run();
        }
        hw.RaiseStringInputInterrupt("/bin/nope");
        for (int i = 0; i < 60000 && !strings.Contains("?"); i++)
        {
            hw.Run();
        }

        Assert.Contains("?", strings);   // the failed exec reported the error
    }
}
