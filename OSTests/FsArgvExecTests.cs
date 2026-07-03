using CSharpOS;

namespace OSTests;

/// <summary>
/// End-to-end tests for the Shell §2 exec ABI: FSYS Exec now takes a whole command line (EBX =
/// pointer to a word-per-char, null-terminated line). The exec core tokenizes it — token0 is the
/// program path (resolved and run), the remaining tokens become argv (delivered in Increment B).
/// Increment A covers the ABI + tokenizer + path-from-token0: a line with trailing args runs the
/// named program (args captured but not yet delivered); an empty/whitespace line fails with -1.
/// </summary>
public class FsArgvExecTests
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

    // A "parent" program that FSYS-execs the command line stored word-per-char at offset 64 of its
    // own image. On success exec never returns; on failure it OUTs the -1 result and halts so a
    // test can observe the failure.
    private static byte[] ExecCmd(string commandLine)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, 64);      // user pointer to the command line
        asm.Fsys();
        asm.Out(RegisterName.EAX);               // only reached if exec failed (result = -1)
        asm.Hlt();
        byte[] code = asm.Build();

        byte[] image = new byte[64 + (commandLine.Length + 1) * 4];
        Array.Copy(code, image, code.Length);
        for (int i = 0; i < commandLine.Length; i++)
        {
            image[64 + i * 4] = (byte)commandLine[i];
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
    public void ExecWithTrailingArgs_RunsTheNamedProgram()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/child", PrintAndHalt(42));

        int slot = hw.Disk.Store(ExecCmd("/child alpha beta"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 42 }, outputs);   // token0 resolved + ran; args ignored (Inc A)
    }

    [Fact]
    public void ExecWithExtraSpacesBetweenTokens_StillRuns()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/child", PrintAndHalt(9));

        int slot = hw.Disk.Store(ExecCmd("/child     x"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 9 }, outputs);
    }

    [Fact]
    public void ExecNoArgs_StillRuns()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/child", PrintAndHalt(5));

        int slot = hw.Disk.Store(ExecCmd("/child"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 5 }, outputs);
    }

    [Fact]
    public void ExecEmptyCommandLine_ReturnsMinusOne()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        int slot = hw.Disk.Store(ExecCmd(""));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1 }, outputs);   // no token0 → exec fails, parent survives
    }

    [Fact]
    public void ExecWhitespaceOnlyCommandLine_ReturnsMinusOne()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        int slot = hw.Disk.Store(ExecCmd("   "));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1 }, outputs);
    }

    [Fact]
    public void ExecMissingProgramWithArgs_ReturnsMinusOne()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        int slot = hw.Disk.Store(ExecCmd("/nope arg"));
        os.LoadProcess(new Process(slot, 512, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1 }, outputs);
    }

    // ---- Increment B: parsed argv delivered to the child ------------------

    // Captures both int (OUT → argc) and string (OUTS → each argv[k]) program output.
    private static (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) NewMachineDual()
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

    // A program that reads its argv: at entry EAX = argc, EBX = argv base (virtual). It OUTs argc,
    // then OUTS each argv[k] string, then halts. Deref pattern: argv[k] = *(argvBase + k*4) is a
    // virtual pointer to a null-terminated word-per-char string; OUTS stops at the null.
    private static byte[] ArgvEcho()
    {
        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);   // ESI = argv base (survives syscalls)
        asm.Mov(RegisterName.EDI, RegisterName.EAX);   // EDI = argc
        asm.Out(RegisterName.EDI);                     // OUT argc
        asm.MovImm(RegisterName.ECX, 0);               // k = 0
        asm.Label("k_loop");
        asm.Cmp(RegisterName.ECX, RegisterName.EDI);
        asm.Jns("k_done");                             // k >= argc → done
        asm.Mov(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm(RegisterName.EDX, 4);
        asm.Mul(RegisterName.EAX, RegisterName.EDX);   // k*4
        asm.Add(RegisterName.EAX, RegisterName.ESI);   // &argv[k]
        asm.Load(RegisterName.EAX, RegisterName.EAX);  // argv[k] = virtual string pointer
        asm.MovImm(RegisterName.EDX, 20);              // max words to print (stops at null)
        asm.Outs(RegisterName.EAX, RegisterName.EDX);
        asm.Inc(RegisterName.ECX);
        asm.Jmp("k_loop");
        asm.Label("k_done");
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Argv_DeliversArgcAndEachArgument()
    {
        (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) = NewMachineDual();
        FsImage.WriteFile(hw, "/argvprog", ArgvEcho());

        int slot = hw.Disk.Store(ExecCmd("/argvprog A bb ccc"));
        os.LoadProcess(new Process(slot, 1024, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 4 }, ints);                                   // argc = 4
        Assert.Equal(new List<string?> { "/argvprog", "A", "bb", "ccc" }, strings); // argv[0..3]
    }

    [Fact]
    public void Argv_NoArguments_ArgcIsOne_Argv0IsThePath()
    {
        (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) = NewMachineDual();
        FsImage.WriteFile(hw, "/argvprog", ArgvEcho());

        int slot = hw.Disk.Store(ExecCmd("/argvprog"));
        os.LoadProcess(new Process(slot, 1024, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 1 }, ints);
        Assert.Equal(new List<string?> { "/argvprog" }, strings);
    }

    [Fact]
    public void Argv_ArgumentLongerThanNameMaxChars_IsNotTruncated()
    {
        (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) = NewMachineDual();
        FsImage.WriteFile(hw, "/argvprog", ArgvEcho());

        // 16 chars > FsLayout.NameMaxChars (12): the tokenizer must not clamp arg strings to a name.
        int slot = hw.Disk.Store(ExecCmd("/argvprog abcdefghijklmnop"));
        os.LoadProcess(new Process(slot, 1024, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 2 }, ints);
        Assert.Equal(new List<string?> { "/argvprog", "abcdefghijklmnop" }, strings);
    }

    [Fact]
    public void Argv_MoreThanMaxArgs_IsCappedAtFsArgvMaxArgs()
    {
        (BasicOS os, Hardware hw, List<int> ints, List<string?> strings) = NewMachineDual();
        FsImage.WriteFile(hw, "/argvprog", ArgvEcho());

        // 11 args + argv[0] = 12 tokens, capped at FsArgvMaxArgs (8).
        int slot = hw.Disk.Store(ExecCmd("/argvprog a b c d e f g h i j"));
        os.LoadProcess(new Process(slot, 1024, 128));
        RunToCompletion(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { OsLayout.FsArgvMaxArgs }, ints);
        Assert.Equal("/argvprog", strings[0]);
        Assert.Equal("g", strings[OsLayout.FsArgvMaxArgs - 1]);   // argv[7] = the 7th typed arg
        Assert.Equal(OsLayout.FsArgvMaxArgs, strings.Count);
    }
}
