using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// End-to-end tests for KILL (Shell §2.5 job control, JC-B): send a signal to an arbitrary process.
/// SigTerm/SigKill tear the target down through the same teardown as a normal exit (freeing its
/// memory, waking a wait()ing parent, leaving a zombie for its parent), and KILL delivers 0 (signal
/// delivered) or -1 (no such live pid) back to the caller in EAX. PIDs are monotonic from 1.
/// </summary>
public class KillTests : IDisposable
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
    public void Kill_TerminatesARunningChild_ReturnsZero_ThenReapYieldsKilledStatus()
    {
        // Parent forks a child that spins forever; the parent KILLs it (SigTerm → 0), then REAPs it
        // (the killed child is a zombie holding exit status -1). Output: 0 (kill ok), 2 (child pid), -1.
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = child pid
        asm.MovImm(RegisterName.EDX, Hardware.SigTerm);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);  // EAX = 0 (delivered)
        asm.Out(RegisterName.EAX);
        asm.Reap(RegisterName.ESI);                    // EAX = child pid, EDX = status (-1)
        asm.Out(RegisterName.EAX);
        asm.Out(RegisterName.EDX);
        asm.Hlt();
        asm.Label("child");
        asm.Label("spin");
        asm.Jmp("spin");                               // run until killed

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 0, 2, -1 }, outputs);
        Assert.False(os.HasProcesses); // killed child reaped, parent halted: no leaked slots
    }

    [Fact]
    public void Kill_UnknownPid_ReturnsMinusOne()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.ESI, 99);              // no such pid
        asm.MovImm(RegisterName.EDX, Hardware.SigTerm);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);
        asm.Out(RegisterName.EAX);                     // -1
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { -1 }, outputs);
    }

    [Fact]
    public void Kill_WakesAParentBlockedInWait_WithKilledStatus()
    {
        // Parent forks victim C (spins) and killer K (which inherits C's pid in ESI and kills it).
        // The parent WAITs on C; K's kill tears C down, and teardown wakes the waiting parent with
        // C's killed status (-1). PIDs: parent 1, C 2, K 3.
        Assembler asm = new Assembler();
        asm.Fork();                                    // fork C
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childC");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = C pid
        asm.Fork();                                    // fork K (inherits ESI = C pid)
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childK");
        asm.Wait(RegisterName.ESI);                    // parent blocks on C; woken when K kills C
        asm.Out(RegisterName.EAX);                     // C's killed status (-1)
        asm.Hlt();
        asm.Label("childC");
        asm.Label("spinC");
        asm.Jmp("spinC");
        asm.Label("childK");
        asm.MovImm(RegisterName.EDX, Hardware.SigTerm);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);  // kill C
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 60000);

        Assert.Contains(-1, outputs);   // the waiting parent collected C's killed status
    }

    [Fact]
    public void Kill_Stop_WakesAWaitingParent_WithStoppedStatus_AndKeepsTheChildAlive()
    {
        // Parent forks victim C (spins) and killer K (inherits C's pid, stops it). The parent WAITs
        // on C; SIGSTOP wakes the waiting parent with the "stopped" status -2 (WUNTRACED) but leaves
        // C alive (stopped, not reaped). PIDs: parent 1, C 2, K 3.
        Assembler asm = new Assembler();
        asm.Fork();                                    // fork C
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childC");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = C pid
        asm.Fork();                                    // fork K (inherits ESI = C pid)
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childK");
        asm.Wait(RegisterName.ESI);                    // parent blocks on C; woken -2 when K stops C
        asm.Out(RegisterName.EAX);                     // stopped status (-2)
        asm.Hlt();
        asm.Label("childC");
        asm.Label("spinC");
        asm.Jmp("spinC");
        asm.Label("childK");
        asm.MovImm(RegisterName.ECX, 200);             // spin so the parent reaches WAIT before we
        asm.Label("kspin");                            // stop C — a stopped child is not a zombie, so
        asm.Dec(RegisterName.ECX);                     // a WAIT that arrives after the stop would block
        asm.Jnz("kspin");                              // forever (unlike a killed child, which reaps).
        asm.MovImm(RegisterName.EDX, Hardware.SigStop);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);  // stop C
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Contains(-2, outputs);   // the waiting parent was woken with the stopped status
        Assert.True(os.HasProcesses);   // C is still alive (stopped), not reaped
    }

    [Fact]
    public void Kill_StopThenCont_HoldsAChildThenLetsItRunToCompletion()
    {
        // Parent forks C (which spins a while, then OUTs 33 and exits). The parent stops C mid-spin,
        // OUTs 100 while C is held (proving C is not scheduled), continues C, then WAITs it. The
        // deterministic order 100 → 33 → 0 shows STOP held C and CONT resumed it.
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("childC");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = C pid
        asm.MovImm(RegisterName.EDX, Hardware.SigStop);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);  // stop C mid-spin
        asm.MovImm(RegisterName.EAX, 100);
        asm.Out(RegisterName.EAX);                     // parent runs while C is held
        asm.MovImm(RegisterName.EDX, Hardware.SigCont);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);  // resume C
        asm.Wait(RegisterName.ESI);
        asm.Out(RegisterName.EAX);                     // C's exit status (0)
        asm.Hlt();
        asm.Label("childC");
        asm.MovImm(RegisterName.ECX, 120);
        asm.Label("cspin");
        asm.Dec(RegisterName.ECX);
        asm.Jnz("cspin");                              // spin so the parent can stop it first
        asm.MovImm(RegisterName.EAX, 33);
        asm.Out(RegisterName.EAX);
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 100, 33, 0 }, outputs);
        Assert.False(os.HasProcesses);
    }

    // Forks a child that spins forever; the parent focuses it and WAITs. A terminal signal
    // (Ctrl-C/Ctrl-Z) then targets the focused child. The parent prints the status WAIT returns.
    private static byte[] FocusedSpinnerWithWaitingParent()
    {
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = child pid
        asm.SetFocus(RegisterName.ESI);                // child is the foreground process
        asm.Wait(RegisterName.ESI);                    // block; a terminal signal wakes us
        asm.Out(RegisterName.EAX);                     // status WAIT returned
        asm.Hlt();
        asm.Label("child");
        asm.Label("spin");
        asm.Jmp("spin");
        return asm.Build();
    }

    private static void RunUntilParentWaiting(BasicOS os, Hardware hw, int cap)
    {
        for (int i = 0; i < cap; i++)
        {
            int e = OsLayout.ProcessEntryAddress(0);
            if (Test.ReadWord(hw, e + Hardware.ProcessEntryState) == (int)ProcessState.Blocked
                && Test.ReadWord(hw, e + Hardware.ProcessEntryWaitReason) == (int)WaitReason.ChildProcess)
            {
                return;
            }
            hw.Run();
        }
    }

    [Fact]
    public void ForegroundSignal_Term_KillsTheFocusedProcess_AndWakesItsWaitingParent()
    {
        // Ctrl-C: RaiseForegroundSignal(SigTerm) tears down the focused (foreground) child; its
        // waiting parent is woken with the killed status -1. No killer process is involved.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(FocusedSpinnerWithWaitingParent()), 128, 64));

        RunUntilParentWaiting(os, hw, 40000);
        hw.RaiseForegroundSignal(Hardware.SigTerm);    // Ctrl-C on the focused child
        for (int i = 0; i < 40000 && !outputs.Contains(-1); i++) { hw.Run(); }

        Assert.Contains(-1, outputs);   // the parent collected the killed child's status
    }

    [Fact]
    public void ForegroundSignal_Stop_StopsTheFocusedProcess_AndWakesItsWaitingParent()
    {
        // Ctrl-Z: RaiseForegroundSignal(SigStop) stops the focused child; its waiting parent is woken
        // with the stopped status -2, and the child stays alive (stopped).
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(FocusedSpinnerWithWaitingParent()), 128, 64));

        RunUntilParentWaiting(os, hw, 40000);
        hw.RaiseForegroundSignal(Hardware.SigStop);    // Ctrl-Z on the focused child
        for (int i = 0; i < 40000 && !outputs.Contains(-2); i++) { hw.Run(); }

        Assert.Contains(-2, outputs);   // the parent was woken with the stopped status
        Assert.True(os.HasProcesses);   // the child is still alive (stopped)
    }

    [Fact]
    public void Kill_ABlockedChild_TearsItDown_AndReapYieldsKilledStatus()
    {
        // The child blocks on IN (no input ever arrives); the parent KILLs it and reaps it. Killing a
        // process blocked mid-syscall must still tear it down cleanly. Output: 0 (kill ok), -1 (status).
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        // Give the child time to reach its blocking IN before killing it.
        asm.MovImm(RegisterName.ECX, 40);
        asm.Label("settle");
        asm.Dec(RegisterName.ECX);
        asm.Jnz("settle");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);   // ESI = child pid
        asm.MovImm(RegisterName.EDX, Hardware.SigTerm);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);
        asm.Out(RegisterName.EAX);                     // 0
        asm.Reap(RegisterName.ESI);                    // EAX = pid, EDX = status
        asm.Out(RegisterName.EDX);                     // -1
        asm.Hlt();
        asm.Label("child");
        asm.In(RegisterName.EAX);                      // block on input forever
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 40000);

        Assert.Equal(new List<int> { 0, -1 }, outputs);
    }
}
