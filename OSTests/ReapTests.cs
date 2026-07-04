using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// End-to-end tests for REAP (Shell §2.5 job control): a non-blocking reap of a dead child.
/// REAP reg (reg = target PID, 0 = any child) delivers the reaped PID in EAX (0 if none dead)
/// and its exit status in EDX. Unlike WAIT it never blocks — a shell uses it to collect
/// background jobs. PIDs are monotonic from 1, so a first-loaded process is PID 1 and its
/// forked children are PID 2, 3, … in fork order (deterministic assertions below).
/// </summary>
public class ReapTests : IDisposable
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

    [Fact]
    public void Reap_NoDeadChild_ReturnsZero()
    {
        // A lone process reaps: no children at all, so REAP delivers pid 0.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 0);      // target = any child
        asm.Reap(RegisterName.EAX);           // EAX = reaped pid (0), EDX = status
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 0 }, outputs);
    }

    [Fact]
    public void ReapAny_CollectsExitedChild_ReturnsPidAndStatus_AndFreesTheSlot()
    {
        // FORK; child EXIT(9); parent does NOT wait — it polls REAP(any) until the child is
        // dead, then prints its pid (2) and status (9), then reaps again (expects 0 — the slot
        // is freed). No zombie slot should leak.
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        asm.Label("poll");
        asm.MovImm(RegisterName.EAX, 0);      // target = any child
        asm.Reap(RegisterName.EAX);           // EAX = pid (0 until the child dies), EDX = status
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("poll");
        asm.Out(RegisterName.EAX);            // child pid = 2
        asm.Out(RegisterName.EDX);            // child status = 9
        asm.MovImm(RegisterName.EAX, 0);
        asm.Reap(RegisterName.EAX);           // nothing left to reap
        asm.Out(RegisterName.EAX);            // 0
        asm.Hlt();
        asm.Label("child");
        asm.MovImm(RegisterName.EAX, 9);
        asm.Exit(RegisterName.EAX);

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 2, 9, 0 }, outputs);

        // The reaped child left no live slot: every entry is Terminated after the parent halts.
        int count = Test.ReadWord(hw, OsLayout.ProcessCountOffset);
        for (int i = 0; i < count; i++)
        {
            int state = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(i) + Hardware.ProcessEntryState);
            Assert.Equal((int)ProcessState.Terminated, state);
        }
    }

    [Fact]
    public void ReapTargeted_ReapsTheNamedChild_ThenReapAnyGetsTheOther()
    {
        // Parent forks two children (A pid 2 status 11, B pid 3 status 22). It reaps B by its
        // specific PID first, then reap-any collects A — proving targeting picks one child and
        // leaves the other for a later reap.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 0);      // comparison zero (child EAX after fork)
        asm.Fork();                            // fork A
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childA");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = pidA
        asm.Fork();                            // fork B
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childB");
        asm.Mov(RegisterName.EDI, RegisterName.EAX);   // EDI = pidB
        // Reap B specifically.
        asm.Label("reapB");
        asm.Mov(RegisterName.EAX, RegisterName.EDI);   // target = pidB
        asm.Reap(RegisterName.EAX);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("reapB");
        asm.Out(RegisterName.EAX);            // pidB = 3
        asm.Out(RegisterName.EDX);            // statusB = 22
        // Reap the remaining child (A) with reap-any.
        asm.Label("reapA");
        asm.MovImm(RegisterName.EAX, 0);
        asm.Reap(RegisterName.EAX);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("reapA");
        asm.Out(RegisterName.EAX);            // pidA = 2
        asm.Out(RegisterName.EDX);            // statusA = 11
        asm.Hlt();
        asm.Label("childA");
        asm.MovImm(RegisterName.EAX, 11);
        asm.Exit(RegisterName.EAX);
        asm.Label("childB");
        asm.MovImm(RegisterName.EAX, 22);
        asm.Exit(RegisterName.EAX);

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 60000);

        Assert.Equal(new List<int> { 3, 22, 2, 11 }, outputs);
    }
}
