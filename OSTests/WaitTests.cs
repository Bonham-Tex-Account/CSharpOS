using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// End-to-end tests for WAIT / EXIT and zombie reaping: a parent that waits on a child
/// receives the child's exit status (whether the child exits before or after the wait),
/// and exited children do not leak process-table slots.
/// </summary>
public class WaitTests : IDisposable
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

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    // FORK; child EXIT(status); parent WAIT(child) then OUT the status; HLT.
    private static byte[] WaitForChild(int childExitStatus)
    {
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        // parent: EAX = child PID -> WAIT delivers the child's exit status into EAX.
        asm.Wait(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("child");
        asm.MovImm(RegisterName.EAX, childExitStatus);
        asm.Exit(RegisterName.EAX);
        return asm.Build();
    }

    [Fact]
    public void Wait_DeliversChildExitStatusToParent()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(WaitForChild(42)), 128, 64));

        RunSteps(os, hw, 40000);

        // The parent waited and printed the child's exit status (42), not the child PID.
        Assert.Equal(new List<int> { 42 }, outputs);
        Assert.False(os.HasProcesses); // child reaped, parent halted: no leaked slots
    }

    [Fact]
    public void Wait_OnAlreadyExitedChild_StillReturnsStatus_AndReapsSlot()
    {
        // The child spins briefly is unnecessary; the scheduler interleaves them, so this
        // exercises whichever order occurs. Either way the parent must collect status 7.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(WaitForChild(7)), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 7 }, outputs);

        // No zombie slots remain: every entry is Terminated.
        int count = Test.ReadWord(hw, OsLayout.ProcessCountOffset);
        for (int i = 0; i < count; i++)
        {
            int state = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(i) + Hardware.ProcessEntryState);
            Assert.Equal((int)ProcessState.Terminated, state);
        }
    }
}
