using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Shell §2.5 job control, JC-E (catchable signals) — E1 plumbing.
/// SIGACTION installs a per-process catchable-signal handler virtual address into the running
/// process's table entry (ProcessEntrySigHandler); this file verifies the install/clear behaviour.
/// Delivery (redirecting a signalled process to its handler) and SIGRETURN (resuming afterwards)
/// are wired in E2/E3 and tested there. PIDs/slots start at 0 for a single loaded process.
/// </summary>
public class SignalTests : IDisposable
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

    // Runs until the single process emits its marker (proof SIGACTION executed), or the cap is hit.
    private static void RunUntilOutput(BasicOS os, Hardware hw, List<int> outputs, int cap)
    {
        for (int i = 0; i < cap && outputs.Count == 0 && os.HasProcesses; i++)
        {
            hw.Run();
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

    [Fact]
    public void Sigaction_StoresHandlerVaddr_InRunningProcessEntry()
    {
        // Install a handler at vaddr 64 for SigInt, emit a marker, then spin. After the marker the
        // running process's entry must hold 64 in ProcessEntrySigHandler.
        const int Handler = 64;
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, Handler);           // handler vaddr
        asm.MovImm(RegisterName.EAX, Hardware.SigInt);   // signal selector
        asm.Sigaction(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EBX);                       // marker: SIGACTION ran
        asm.Label("spin");
        asm.Jmp("spin");

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunUntilOutput(os, hw, outputs, 40000);

        Assert.Equal(new List<int> { Handler }, outputs);
        int handlerField = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntrySigHandler);
        Assert.Equal(Handler, handlerField);
    }

    [Fact]
    public void Sigaction_WithZeroHandler_ClearsAPreviouslyInstalledHandler()
    {
        // Install a handler, then install 0 (the "reset to default" form): the field ends up cleared.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 64);
        asm.MovImm(RegisterName.EAX, Hardware.SigInt);
        asm.Sigaction(RegisterName.EAX, RegisterName.EBX);   // install 64
        asm.MovImm(RegisterName.EBX, 0);
        asm.Sigaction(RegisterName.EAX, RegisterName.EBX);   // clear
        asm.MovImm(RegisterName.ECX, 7);
        asm.Out(RegisterName.ECX);                           // marker
        asm.Label("spin");
        asm.Jmp("spin");

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunUntilOutput(os, hw, outputs, 40000);

        Assert.Equal(new List<int> { 7 }, outputs);
        int handlerField = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntrySigHandler);
        Assert.Equal(0, handlerField);
    }

    [Fact]
    public void Sigaction_FreshProcess_HasNoHandlerInstalled()
    {
        // A newly loaded process starts with a zero (no) handler — the default-action state.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.ECX, 9);
        asm.Out(RegisterName.ECX);
        asm.Label("spin");
        asm.Jmp("spin");

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunUntilOutput(os, hw, outputs, 40000);

        int handlerField = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntrySigHandler);
        Assert.Equal(0, handlerField);
        int pending = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntrySigPending);
        int inHandler = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryInHandler);
        Assert.Equal(0, pending);
        Assert.Equal(0, inHandler);
    }

    // A process that installs a SigInt handler, prints 10 (installed), then spins on a DATA flag; the
    // handler sets the flag, prints 77, and SIGRETURNs, after which the spin sees the flag and prints
    // 88 before halting. The handler label must sit < 256 bytes into the image (MovImmLabel is 8-bit).
    // Installs a SigInt handler, prints 10, then spins on a DATA flag. A SigInt runs the handler,
    // which prints 77 (a syscall inside the handler — the case that exercises the level-save fix) and
    // sets the flag, then SIGRETURNs; the restored context resumes the spin, which now sees the flag
    // and prints 88 before halting. The handler label must sit < 256 bytes in (MovImmLabel is 8-bit).
    private const int FlagAddr = 400;   // DATA vaddr (needs RequiredMemory >= ~512)
    private static byte[] CatchSigIntThenResume()
    {
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EDX, "h");            // handler vaddr
        asm.MovImm(RegisterName.EAX, Hardware.SigInt);
        asm.Sigaction(RegisterName.EAX, RegisterName.EDX); // install
        asm.MovImm(RegisterName.EAX, 10);
        asm.Out(RegisterName.EAX);                         // "installed"
        asm.Label("loop");
        asm.MovImm16(RegisterName.EBX, FlagAddr);
        asm.Load(RegisterName.ECX, RegisterName.EBX);      // ECX = flag
        asm.MovImm(RegisterName.EDX, 0);
        asm.Cmp(RegisterName.ECX, RegisterName.EDX);
        asm.Jnz("done");
        asm.Jmp("loop");
        asm.Label("done");
        asm.MovImm(RegisterName.EAX, 88);
        asm.Out(RegisterName.EAX);                         // resumed after the handler; flag was set
        asm.Hlt();
        asm.Label("h");                                    // signal handler
        asm.MovImm(RegisterName.EAX, 77);
        asm.Out(RegisterName.EAX);                         // handler ran (a syscall inside the handler)
        asm.MovImm16(RegisterName.EBX, FlagAddr);
        asm.MovImm(RegisterName.ECX, 1);
        asm.Store(RegisterName.EBX, RegisterName.ECX);     // set the flag (persists across SIGRETURN)
        asm.SigReturn();
        return asm.Build();
    }

    // Runs until the process has emitted `value` (or the cap is hit), so the test can act at a known point.
    private static void RunUntilOutputValue(BasicOS os, Hardware hw, List<int> outputs, int value, int cap)
    {
        for (int i = 0; i < cap && os.HasProcesses; i++)
        {
            hw.Run();
            if (outputs.Contains(value))
            {
                return;
            }
        }
    }

    private static int Pid0(Hardware hw)
    {
        return Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPid);
    }

    [Fact]
    public void ForegroundSigInt_WithHandler_RunsHandler_ThenResumesTheInterruptedProgram()
    {
        // Ctrl-C (SigInt) to a running foreground process that installed a handler: the handler runs
        // (77), SIGRETURNs, and the interrupted program resumes and finishes (88). Output: 10, 77, 88.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(CatchSigIntThenResume()), 1024, 64));

        RunUntilOutputValue(os, hw, outputs, 10, 40000);   // handler installed
        for (int i = 0; i < 300; i++) { hw.Run(); }        // settle into the spin loop
        hw.SetFocus(Pid0(hw));                             // foreground
        hw.RaiseForegroundSignal(Hardware.SigInt);         // Ctrl-C
        for (int i = 0; i < 40000 && os.HasProcesses; i++) { hw.Run(); }

        Assert.Equal(new List<int> { 10, 77, 88 }, outputs);
        Assert.False(os.HasProcesses);                     // resumed after the handler and halted cleanly
    }

    [Fact]
    public void ForegroundSigInt_WithoutHandler_TerminatesTheProcess()
    {
        // Ctrl-C to a process with NO handler installed takes the default action (teardown) — the
        // process dies and never reaches its (would-be) later output.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 10);
        asm.Out(RegisterName.EAX);
        asm.Label("spin");
        asm.Jmp("spin");

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunUntilOutputValue(os, hw, outputs, 10, 40000);
        hw.SetFocus(Pid0(hw));
        hw.RaiseForegroundSignal(Hardware.SigInt);         // no handler → default teardown

        for (int i = 0; i < 40000 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.Equal(new List<int> { 10 }, outputs);
        Assert.False(os.HasProcesses);                     // killed by the default action
    }

    [Fact]
    public void SigKill_IgnoresAnInstalledHandler_AndTerminates()
    {
        // SigKill is uncatchable: even with a handler installed, it runs the default teardown (the
        // handler's 77 is never emitted). Delivered as a self-KILL from C# against the running process.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(CatchSigIntThenResume()), 1024, 64));

        RunUntilOutputValue(os, hw, outputs, 10, 40000);   // handler installed, spinning
        hw.Kill(Pid0(hw), Hardware.SigKill);               // self-KILL (killer == running target)

        for (int i = 0; i < 40000 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.DoesNotContain(77, outputs);                // handler never ran
        Assert.DoesNotContain(88, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void SecondSigInt_DuringHandler_IsDeferred_ThenDeliveredOnSigReturn()
    {
        // Two SigInts arrive while the process is spinning. The first is delivered and the process
        // enters its handler; the second arrives while it is already in the handler (InHandler=1), so
        // it is queued as pending rather than re-entering. On SIGRETURN the pending signal is
        // re-delivered, so the handler runs a SECOND time before the program resumes. Output: the
        // "installed" 10, then 77 twice (two handler runs), then 88 once (resumed and finished).
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);
        os.LoadProcess(new Process(CreateProgramFile(CatchSigIntThenResume()), 1024, 64));

        RunUntilOutputValue(os, hw, outputs, 10, 40000);   // handler installed, spinning
        for (int i = 0; i < 300; i++) { hw.Run(); }
        hw.SetFocus(Pid0(hw));
        hw.RaiseForegroundSignal(Hardware.SigInt);         // delivered → enters handler
        hw.RaiseForegroundSignal(Hardware.SigInt);         // arrives while in-handler → pending

        for (int i = 0; i < 40000 && os.HasProcesses; i++) { hw.Run(); }

        Assert.Equal(new List<int> { 10, 77, 77, 88 }, outputs);
        Assert.False(os.HasProcesses);
    }
}
