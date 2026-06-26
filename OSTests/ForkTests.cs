using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// End-to-end tests for the FORK instruction and its privileged ISA routine: the child
/// is a memory + register copy of the parent at a new base (position-independent), the
/// parent receives the child's PID while the child receives 0, and both run to
/// completion as independent processes.
/// </summary>
public class ForkTests : IDisposable
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

    // Collects every emitted output value, completing each output so the producer continues.
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

    // FORK; OUT EAX; HLT  — parent prints the child PID it received, child prints 0.
    private static byte[] ForkEcho()
    {
        Assembler asm = new Assembler();
        asm.Fork();
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // FORK; if EAX==0 take the child branch, else the parent branch.
    private static byte[] ForkBranch()
    {
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        asm.MovImm(RegisterName.EAX, 100);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("child");
        asm.MovImm(RegisterName.EAX, 200);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Fork_DeliversChildPidToParent_AndZeroToChild()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(ForkEcho()), 128, 64)); // parent PID 1

        RunSteps(os, hw, 20000);

        // Two outputs: the child's view (0) and the parent's view (the child PID = 2).
        Assert.Equal(2, outputs.Count);
        Assert.Contains(0, outputs);
        Assert.Contains(2, outputs);
        Assert.False(os.HasProcesses); // both parent and child ran to HLT
    }

    [Fact]
    public void Fork_ParentAndChildTakeDifferentBranches()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(ForkBranch()), 128, 64));

        RunSteps(os, hw, 20000);

        // Parent took the "100" branch, child took the "200" branch.
        Assert.Contains(100, outputs);
        Assert.Contains(200, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Fork_AddsASecondLiveProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(ForkEcho()), 128, 64));

        // Step only until the parent's FORK has produced the child (the parent's first
        // output appears after the fork), then check two processes occupy the table.
        for (int i = 0; i < 4000 && CountLive(hw) < 2; i++)
        {
            hw.Run();
        }
        Assert.Equal(2, CountLive(hw));
    }

    // Counts non-terminated, non-zombie entries by reading the process table directly.
    private static int CountLive(Hardware hw)
    {
        int count = Test.ReadWord(hw, OsLayout.ProcessCountOffset);
        int live = 0;
        for (int i = 0; i < count; i++)
        {
            int state = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(i) + Hardware.ProcessEntryState);
            if (state != (int)ProcessState.Terminated && state != (int)ProcessState.Zombie)
            {
                live++;
            }
        }
        return live;
    }
}
