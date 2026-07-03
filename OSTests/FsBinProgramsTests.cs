using CSharpOS;
using CSharpOSConsole;

namespace OSTests;

/// <summary>
/// End-to-end tests for the /bin command programs (Shell §2): ls, cat, rm, mkdir, echo, help.
/// Each is installed into the filesystem and launched by a small parent that FSYS-execs a command
/// line, exercising the full argv path (exec tokenizes, the program reads argc/argv and calls
/// FSYS). These are what the shell runs; the shell itself is covered separately.
/// </summary>
public class FsBinProgramsTests
{
    private static int Memory => Test.MachineWithHeap(16384);

    private static (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
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
        return (os, hw, ints, strings);
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

    // Encodes text as file content the word-per-char way cat/OUTS read it (one char per 4-byte
    // word, low byte). FsImage.WriteFile stores these bytes verbatim, so word[i] low byte = s[i].
    private static byte[] WordPerChar(string s)
    {
        byte[] bytes = new byte[s.Length * 4];
        for (int i = 0; i < s.Length; i++)
        {
            bytes[i * 4] = (byte)s[i];
        }
        return bytes;
    }

    private static void RunToCompletion(BasicOS os, Hardware hw)
    {
        for (int i = 0; i < 60000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    // Installs the given /bin program, then loads a parent that execs `commandLine` and runs it.
    private static (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) RunCommand(
        string binName, byte[] binProgram, string commandLine, Action<Hardware>? seed = null)
    {
        (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) = NewMachine();
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/" + binName, binProgram);
        if (seed != null)
        {
            seed(hw);   // create any data files BEFORE a process is allocated (WriteFile staging)
        }
        os.LoadProcess(new Process(hw.Disk.Store(ExecCmd(commandLine)), 1024, 128));
        RunToCompletion(os, hw);
        Assert.False(os.HasProcesses);
        return (os, hw, ints, strings);
    }

    [Fact]
    public void Echo_PrintsEachArgument()
    {
        (_, _, _, List<string?> strings) = RunCommand("echo", Programs.Echo(), "/bin/echo hello world");
        Assert.Equal(new List<string?> { "hello", "world" }, strings);
    }

    [Fact]
    public void Cat_PrintsFileContents()
    {
        (_, _, _, List<string?> strings) = RunCommand("cat", Programs.Cat(), "/bin/cat /note",
            hw => FsImage.WriteFile(hw, "/note", WordPerChar("hello")));
        Assert.Contains("hello", strings);
    }

    [Fact]
    public void Rm_DeletesTheFile()
    {
        (_, Hardware hw, _, _) = RunCommand("rm", Programs.Rm(), "/bin/rm /victim",
            hw => FsImage.WriteFile(hw, "/victim", WordPerChar("bye")));
        Assert.Equal(-1, FsImage.ResolveFirstBlock(hw, "/victim"));   // gone
    }

    [Fact]
    public void Mkdir_CreatesTheDirectory()
    {
        (_, Hardware hw, _, _) = RunCommand("mkdir", Programs.Mkdir(), "/bin/mkdir /newdir");
        Assert.True(FsImage.ResolveFirstBlock(hw, "/newdir") >= FsLayout.FirstDataBlock);   // exists
    }

    [Fact]
    public void Ls_ListsRootEntries()
    {
        (_, _, _, List<string?> strings) = RunCommand("ls", Programs.Ls(), "/bin/ls /",
            hw =>
            {
                FsImage.WriteFile(hw, "/aaa", WordPerChar("x"));
                FsImage.WriteFile(hw, "/bbb", WordPerChar("y"));
            });
        // Names are null-padded to NameMaxChars; OUTS stops at the null, so they print exactly.
        Assert.Contains("aaa", strings);
        Assert.Contains("bbb", strings);
        Assert.Contains("bin", strings);   // created by EnsureDir + LoadProcess installs
    }

    [Fact]
    public void Ls_DefaultsToRoot_WhenNoArgument()
    {
        (_, _, _, List<string?> strings) = RunCommand("ls", Programs.Ls(), "/bin/ls",
            hw => FsImage.WriteFile(hw, "/solo", WordPerChar("z")));
        Assert.Contains("solo", strings);
    }

    [Fact]
    public void Help_PrintsUsage()
    {
        (_, _, _, List<string?> strings) = RunCommand("help", Programs.Help(), "/bin/help");
        Assert.Contains(strings, s => s != null && s.StartsWith("cmds:"));
    }
}
