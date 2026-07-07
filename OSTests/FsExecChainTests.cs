using CSharpOS;

namespace OSTests;

/// <summary>
/// Regression for the "FSYS-exec alternation flake": running several exec parents in sequence on
/// ONE machine used to alternate pass/fail because a <c>while (os.HasProcesses) hw.Run()</c> loop
/// stops the instant the last process is marked Terminated — while the exit-teardown ISA routine is
/// still running with interrupts masked. The next <c>os.LoadProcess</c> then injected a process into
/// that half-finished state, corrupting on alternate runs.
///
/// The root cause is the harness contract, not the OS: <see cref="Test.RunUntilIdle"/> drains the
/// machine to a clean idle state (interrupts on, nothing running) between programs. These tests
/// prove that with the drain in place, chained exec round-trips deterministically — no alternation.
/// </summary>
public class FsExecChainTests
{
    private static int Memory => Test.MachineWithHeap(16384);

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
        return (os, hw, outputs);
    }

    // A standalone program that OUTs `value` and halts — the exec target stored in the FS.
    private static byte[] PrintAndHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // A parent that FSYS-execs the command line stored word-per-char at offset 64 of its own image.
    // On success exec never returns; on failure it OUTs the -1 result and halts.
    private static byte[] ExecCmd(string commandLine)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, 64);
        asm.Fsys();
        asm.Out(RegisterName.EAX);
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

    private static void RunExec(BasicOS os, Hardware hw, string commandLine)
    {
        int slot = hw.Disk.Store(ExecCmd(commandLine));
        os.LoadProcess(new Process(slot, 512, 128));
        Test.RunUntilIdle(hw, os);
    }

    [Fact]
    public void RepeatedExecOfSameProgram_EachRunProducesOutput_NoAlternation()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/child", PrintAndHalt(42));

        for (int run = 0; run < 6; run++)
        {
            outputs.Clear();
            RunExec(os, hw, "/child");

            Assert.False(os.HasProcesses);
            Assert.Equal(new List<int> { 42 }, outputs);   // every run, not just the even ones
        }
    }

    [Fact]
    public void ChainedExecOfDifferentPrograms_EachRunsInOrder()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/a", PrintAndHalt(1));
        FsImage.WriteFile(hw, "/b", PrintAndHalt(2));
        FsImage.WriteFile(hw, "/c", PrintAndHalt(3));

        RunExec(os, hw, "/a");
        RunExec(os, hw, "/b");
        RunExec(os, hw, "/c");

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 1, 2, 3 }, outputs);
    }

    [Fact]
    public void ChainedExec_FailingExecStillLeavesTheMachineRunnable()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        FsImage.WriteFile(hw, "/ok", PrintAndHalt(7));

        RunExec(os, hw, "/missing");       // exec fails → parent OUTs -1 and halts
        RunExec(os, hw, "/ok");            // the machine must still run the next program cleanly

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { -1, 7 }, outputs);
    }
}
