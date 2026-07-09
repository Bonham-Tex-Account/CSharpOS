using CSharpOS;
using CSharpOSConsole;

namespace OSTests;

/// <summary>
/// §4.2a — the self-hosted assembler `/bin/as` (<see cref="Programs.As"/>). These drive it end-to-end
/// through a live scheduler: stage a word-per-char source file (as `/bin/edit` writes), exec
/// `/bin/as /src /out`, then either read the produced image back through the FS cores and
/// golden-compare it byte-for-byte against the C# <see cref="Assembler"/>, or exec the produced
/// program and observe its output. Covers every shape: None/Reg/RegReg/RegRegReg/Mov (§4.2a/b) and
/// Addr16 labels + branches via the two-pass label table (§4.2c).
///
/// Chained exec phases (assemble then run) drain to idle between programs via
/// <see cref="Test.RunUntilIdle"/> — see <c>FsExecChainTests</c> for why.
/// </summary>
public class FsAssemblerTests
{
    private const int ScratchProc = OsLayout.MaxProcesses - 1;
    // /bin/as is a large, page-thrashing image (tables + buffers all resident), so give it a roomy
    // step budget and heap.
    private const int RunCap = 4_000_000;
    private static int Memory => Test.MachineWithHeap(32768);

    private static (BasicOS os, Hardware hw, List<int> outputs) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (e.StringValue == null)
            {
                outputs.Add(e.Value);
            }
            hw.RaiseOutputComplete(e.Device);
        };
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/as", Programs.As());
        return (os, hw, outputs);
    }

    // A parent that FSYS-execs the command line stored word-per-char at offset 64 of its own image.
    private static byte[] ExecCmd(string commandLine)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, 64);
        asm.Fsys();
        asm.Out(RegisterName.EAX);      // only reached if exec failed
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

    private static void Exec(BasicOS os, Hardware hw, string commandLine)
    {
        int slot = hw.Disk.Store(ExecCmd(commandLine));
        os.LoadProcess(new Process(slot, 1024, 128));
        Test.RunUntilIdle(hw, os, RunCap);
    }

    private static int FsOp(Hardware hw, int op, int a1, int a2, int a3, int a4)
    {
        hw.WriteRegister(RegisterName.EBX, a1);
        hw.WriteRegister(RegisterName.ECX, a2);
        hw.WriteRegister(RegisterName.EDX, a3);
        hw.WriteRegister(RegisterName.ESI, a4);
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, op);
        return Test.ReadWord(hw, OsLayout.FsResultOffset);
    }

    // Writes `text` as a word-per-char source file (exactly what /bin/edit produces). Staged in the
    // free heap above the OS region — safe because it is only called during machine setup, before any
    // process is loaded.
    private static void WriteSource(Hardware hw, string path, string text)
    {
        int pathAddr = OsLayout.TotalSize + 256;
        int dataAddr = OsLayout.TotalSize + 4096;
        for (int i = 0; i < path.Length; i++) { Test.WriteWord(hw, pathAddr + i * 4, path[i]); }
        Test.WriteWord(hw, pathAddr + path.Length * 4, 0);
        for (int i = 0; i < text.Length; i++) { Test.WriteWord(hw, dataAddr + i * 4, text[i]); }

        int fd = FsOp(hw, Hardware.FsOpOpen, pathAddr, Hardware.FsysCreateFlag, ScratchProc, 0);
        Assert.True(fd >= 0, $"could not create source {path}");
        int written = FsOp(hw, Hardware.FsOpWrite, fd, dataAddr, text.Length, ScratchProc);
        Assert.Equal(text.Length, written);
        FsOp(hw, Hardware.FsOpClose, fd, ScratchProc, 0, 0);
    }

    // Reads a file's content back as 32-bit words through the FS cores (absolute buffer, scratch
    // proc). Called when the machine is idle, so staging in the free heap is safe.
    private static int[] ReadWords(Hardware hw, string path)
    {
        int pathAddr = OsLayout.TotalSize + 256;
        int bufAddr = OsLayout.TotalSize + 4096;
        for (int i = 0; i < path.Length; i++) { Test.WriteWord(hw, pathAddr + i * 4, path[i]); }
        Test.WriteWord(hw, pathAddr + path.Length * 4, 0);

        int fd = FsOp(hw, Hardware.FsOpOpen, pathAddr, 0, ScratchProc, 0);
        if (fd < 0)
        {
            return Array.Empty<int>();
        }
        List<int> words = new List<int>();
        while (true)
        {
            int n = FsOp(hw, Hardware.FsOpRead, fd, bufAddr, 64, ScratchProc);
            if (n <= 0)
            {
                break;
            }
            for (int i = 0; i < n; i++)
            {
                words.Add(Test.ReadWord(hw, bufAddr + i * 4));
            }
        }
        FsOp(hw, Hardware.FsOpClose, fd, ScratchProc, 0, 0);
        return words.ToArray();
    }

    // The instruction words the C# Assembler produces for the same program (the golden reference).
    private static int[] GoldenWords(Action<Assembler> build)
    {
        Assembler asm = new Assembler();
        build(asm);
        byte[] bytes = asm.Build();
        int[] words = new int[bytes.Length / 4];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = bytes[i * 4] | (bytes[i * 4 + 1] << 8) | (bytes[i * 4 + 2] << 16) | (bytes[i * 4 + 3] << 24);
        }
        return words;
    }

    [Fact]
    public void Assembles_RegImm_RegReg_Reg_None_MatchesGoldenAssembler()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "MOV EAX 5\nADD EAX EBX\nOUT EAX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.MovImm(RegisterName.EAX, 5);
            a.Add(RegisterName.EAX, RegisterName.EBX);
            a.Out(RegisterName.EAX);
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void Assembles_MovRegisterForm()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "MOV EAX EBX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.Mov(RegisterName.EAX, RegisterName.EBX);
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void Assembles_LargeImmediate_AsMovImm16()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "MOV R8 300\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.MovImm16(RegisterName.R8, 300);
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void Assembles_RegRegReg_DiskInstructions()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        // Also exercises a two-digit register name (R15) through the tokenizer/str_eq scan.
        WriteSource(hw, "/src.s", "DREAD EAX EBX ECX\nDWRITE R8 R15 EDX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.DRead(RegisterName.EAX, RegisterName.EBX, RegisterName.ECX);
            a.DWrite(RegisterName.R8, RegisterName.R15, RegisterName.EDX);
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void BlankLinesAndComments_AreIgnored()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "; a program\n\nMOV EAX 7   ; set eax\n\nOUT EAX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.MovImm(RegisterName.EAX, 7);
            a.Out(RegisterName.EAX);
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void AssembledProgram_ExecutesAndProducesOutput()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        WriteSource(hw, "/src.s", "MOV EAX 42\nOUT EAX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /prog");   // assemble
        Assert.False(os.HasProcesses);
        outputs.Clear();
        Exec(os, hw, "/prog");                  // run the assembled program

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 42 }, outputs);
    }

    [Fact]
    public void Assembles_LabelsAndBranches_MatchesGoldenAssembler()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        // A backward branch (loop) — the label is defined before its use.
        WriteSource(hw, "/src.s",
            "MOV EAX 0\nMOV EBX 3\nloop:\nINC EAX\nOUT EAX\nCMP EAX EBX\nJNZ loop\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.MovImm(RegisterName.EAX, 0);
            a.MovImm(RegisterName.EBX, 3);
            a.Label("loop");
            a.Inc(RegisterName.EAX);
            a.Out(RegisterName.EAX);
            a.Cmp(RegisterName.EAX, RegisterName.EBX);
            a.Jnz("loop");
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void Assembles_ForwardBranch_MatchesGoldenAssembler()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        // A forward branch — the label is used before it is defined (pass 1 resolves it).
        WriteSource(hw, "/src.s",
            "MOV EAX 1\nCMP EAX EAX\nJZ done\nOUT EAX\ndone:\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        int[] expected = GoldenWords(a =>
        {
            a.MovImm(RegisterName.EAX, 1);
            a.Cmp(RegisterName.EAX, RegisterName.EAX);
            a.Jz("done");
            a.Out(RegisterName.EAX);
            a.Label("done");
            a.Hlt();
        });
        Assert.Equal(expected, ReadWords(hw, "/out"));
    }

    [Fact]
    public void AssembledLoop_ExecutesAndCountsToThree()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        WriteSource(hw, "/src.s",
            "MOV EAX 0\nMOV EBX 3\nloop:\nINC EAX\nOUT EAX\nCMP EAX EBX\nJNZ loop\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /prog");   // assemble
        Assert.False(os.HasProcesses);
        outputs.Clear();
        Exec(os, hw, "/prog");                  // run the assembled loop

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 1, 2, 3 }, outputs);
    }

    [Fact]
    public void AssembledCallRet_Subroutine_ExecutesAndReturns()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        WriteSource(hw, "/src.s",
            "MOV EAX 5\nCALL sub\nOUT EAX\nHLT\nsub:\nINC EAX\nRET\n");

        Exec(os, hw, "/bin/as /src.s /prog");   // assemble
        Assert.False(os.HasProcesses);
        outputs.Clear();
        Exec(os, hw, "/prog");                  // CALL sub increments EAX 5→6, RET, OUT 6

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 6 }, outputs);
    }

    [Fact]
    public void UndefinedLabel_ProducesNoOutputFile()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "JMP nowhere\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        // An unresolved branch aborts the assemble and unlinks the half-written image.
        Assert.True(FsImage.ResolveFirstBlock(hw, "/out") < FsLayout.FirstDataBlock);
    }

    [Fact]
    public void UnknownMnemonic_ProducesNoOutputFile()
    {
        (BasicOS os, Hardware hw, List<int> _) = NewMachine();
        WriteSource(hw, "/src.s", "BOGUS EAX\nHLT\n");

        Exec(os, hw, "/bin/as /src.s /out");

        // A malformed source aborts the assemble and unlinks the half-written image: no /out remains.
        Assert.True(FsImage.ResolveFirstBlock(hw, "/out") < FsLayout.FirstDataBlock);
    }
}
