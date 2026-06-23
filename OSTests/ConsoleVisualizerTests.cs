using CSharpOS;
using CSharpOSConsole;
using Xunit;

namespace OSTests;

/// <summary>
/// Exercises the ConsoleVisualizer by capturing its output into a StringWriter
/// (the injected TextWriter) and asserting on the rendered text. Color is off and
/// interactivity is disabled, so each run produces deterministic plain text with
/// no dependency on a real console.
/// </summary>
public class ConsoleVisualizerTests : IDisposable
{
    private const int Memory = 16384;
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

    // Builds a BasicOS + Hardware wired to a fresh visualizer that renders into the
    // returned StringWriter. Defaults to Verbose so every channel is captured; tier
    // tests pass an explicit mode. The output device completes instantly so OUT does
    // not block. The visualizer is returned only to keep it referenced.
    private (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) NewRun(
        VisualizerMode mode = VisualizerMode.Verbose, bool showProgramIo = true)
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        StringWriter sink = new StringWriter();
        ConsoleVisualizer vis = new ConsoleVisualizer(hw, os, 0, sink, useColor: false, interactive: false,
            mode: mode, showProgramIo: showProgramIo);
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { hw.RaiseOutputComplete(e.Device); };
        return (hw, os, sink, vis);
    }

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
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
    public void RendersDecodedInstructionsRegistersAndOutput()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("MOV EAX, 5", text);   // decoded user instruction
        Assert.Contains("OUT ESI", text);        // kernel syscall handler's real OUT
        Assert.Contains("EAX=5", text);          // register snapshot
        Assert.Contains("OUTPUT: 5", text);      // program output
    }

    [Fact]
    public void OutputGoesToInjectedWriter_NotLeftEmpty()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(7)), 128, 64));

        RunSteps(os, hw, 2000);

        // The injected TextWriter received the render; nothing relies on Console.
        Assert.NotEqual(0, sink.ToString().Length);
        Assert.Contains("OUTPUT: 7", sink.ToString());
    }

    [Fact]
    public void CapturedOutputIsPlainText_WhenColorDisabled()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));

        RunSteps(os, hw, 2000);

        // No ANSI escape (ESC, 0x1B) sequences leak into the captured text when
        // color is disabled — the writer receives readable plain text only.
        char esc = (char)0x1B;
        Assert.DoesNotContain(esc, sink.ToString());
    }

    [Fact]
    public void RendersContextSwitchAndProcessTable()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 128, 64));

        RunSteps(os, hw, 4000);

        string text = sink.ToString();
        Assert.Contains("context switch", text);
        Assert.Contains("process table", text);
        Assert.Contains("[0]", text);   // first slot listed
        Assert.Contains("[1]", text);   // second slot listed
    }

    [Fact]
    public void ProcessTable_ShowsBlockedStateAndWaitReason()
    {
        // P1 blocks on input; P2 runs. The context switch to P2 renders the table,
        // which must show P1 as Blocked on Input.
        Assembler reader = new Assembler();
        reader.In(RegisterName.EAX);
        reader.Out(RegisterName.EAX);
        reader.Hlt();

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(reader.Build()), 128, 64));     // P1
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(9)), 128, 64));   // P2

        RunSteps(os, hw, 4000);

        Assert.Contains("Blocked on Input", sink.ToString());
    }

    [Fact]
    public void RendersPrivilegeTransitions_ForSyscallAndOsRoutine()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(3)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("OS routine", text);       // scheduler dispatch -> Privileged
        Assert.Contains("syscall trap", text);     // OUT in user mode -> Kernel
        Assert.Contains("User", text);
        Assert.Contains("Kernel", text);
    }

    [Fact]
    public void RendersBlockAndWakeEvents()
    {
        Assembler reader = new Assembler();
        reader.In(RegisterName.EAX);
        reader.Out(RegisterName.EAX);
        reader.Hlt();

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(reader.Build()), 128, 64));

        RunSteps(os, hw, 2000);
        Assert.Contains("blocked on Input", sink.ToString());

        hw.RaiseInputInterrupt(42);
        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("wake signal: Input (value 42)", text);
        Assert.Contains("OUTPUT: 42", text);
    }

    [Fact]
    public void RendersInvalidInstructionFault()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0xFF, 0, 0, 0 }), 128, 64));

        RunSteps(os, hw, 2000);

        Assert.Contains("INVALID [FF]", sink.ToString());
    }

    [Fact]
    public void RendersFreeMemoryMap()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));

        RunSteps(os, hw, 2000);

        Assert.Contains("free memory:", sink.ToString());
    }

    [Fact]
    public void RendersMemoryWrites_FromStoreInstructions()
    {
        // A program that stores a value into its data area; the visualizer reports
        // word-sized writes as the program's own STOREs.
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EBX, "slot");
        asm.MovImm(RegisterName.EAX, 123);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Hlt();
        asm.DataInt("slot");

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 2000);

        Assert.Contains("= 123", sink.ToString());
    }

    [Fact]
    public void EndToEnd_CounterDemo_PrintsOneThroughTen()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(Programs.CounterToTen()), 128, 64));

        RunSteps(os, hw, 5000);

        string text = sink.ToString();
        Assert.Contains("OUTPUT: 1", text);
        Assert.Contains("OUTPUT: 10", text);
        Assert.False(os.HasProcesses);
    }

    // ---- OS routine markers ---------------------------------------------

    [Fact]
    public void MarksNamedOsRoutines_WhenTheyRun()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("OS routine: Schedule", text);   // idle -> picks the process
        Assert.Contains("OS routine: Halt", text);       // HLT tears it down
    }

    [Fact]
    public void MarksContextSwitchRoutine_WithMultipleProcesses()
    {
        // Two long-running programs force quantum preemption -> ContextSwitch routine.
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(Programs.CounterToTen()), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(Programs.CounterToTen()), 128, 64));

        RunSteps(os, hw, 6000);

        Assert.Contains("OS routine: ContextSwitch", sink.ToString());
    }

    [Fact]
    public void MarksBlockAndWakeRoutines_OnIoBlocking()
    {
        Assembler reader = new Assembler();
        reader.In(RegisterName.EAX);
        reader.Out(RegisterName.EAX);
        reader.Hlt();

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun();
        os.LoadProcess(new Process(CreateProgramFile(reader.Build()), 128, 64));

        RunSteps(os, hw, 2000);
        Assert.Contains("OS routine: Block (input)", sink.ToString());

        hw.RaiseInputInterrupt(42);
        RunSteps(os, hw, 2000);
        Assert.Contains("OS routine: Wake (input)", sink.ToString());
    }

    // ---- Detail tiers ---------------------------------------------------

    [Fact]
    public void MinimalMode_ShowsOsNarrative_OmitsInstructionsAndTables()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Minimal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        // High-level narrative is present...
        Assert.Contains("context switch", text);
        Assert.Contains("OS routine:", text);
        Assert.Contains("OUTPUT: 5", text);
        // ...but the instruction stream, tables, memory and privilege lines are not.
        Assert.DoesNotContain("MOV EAX, 5", text);
        Assert.DoesNotContain("process table", text);
        Assert.DoesNotContain("mem[", text);
        Assert.DoesNotContain("syscall trap", text);
    }

    [Fact]
    public void NormalMode_ShowsInstructionsAndTables_OmitsMemoryAndPrivilege()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("MOV EAX, 5", text);     // instruction stream on
        Assert.Contains("process table", text);    // tables on
        Assert.DoesNotContain("mem[", text);       // memory writes off
        Assert.DoesNotContain("syscall trap", text); // privilege transitions off
    }

    // ---- program I/O mirroring toggle -----------------------------------

    [Fact]
    public void ProgramOutput_HiddenInOsWindow_WhenIoMirroringOff()
    {
        // I/O belongs to the process window; with mirroring off the OS/Hardware
        // window shows the hardware data (instructions) but not the OUTPUT.
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Verbose, showProgramIo: false);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.DoesNotContain("OUTPUT", text);   // program output not mirrored
        Assert.Contains("MOV EAX, 5", text);       // instruction/hardware data still shown
    }

    [Fact]
    public void ProgramOutput_ShownInOsWindow_WhenIoMirroringOn()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Verbose, showProgramIo: true);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 2000);

        Assert.Contains("OUTPUT: 5", sink.ToString());
    }

    [Fact]
    public void WakeSignal_OmitsInputValue_WhenIoMirroringOff()
    {
        Assembler reader = new Assembler();
        reader.In(RegisterName.EAX);
        reader.Out(RegisterName.EAX);
        reader.Hlt();

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Verbose, showProgramIo: false);
        os.LoadProcess(new Process(CreateProgramFile(reader.Build()), 128, 64));

        RunSteps(os, hw, 2000);
        hw.RaiseInputInterrupt(42);
        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("wake signal: Input", text); // the interrupt event still shows
        Assert.DoesNotContain("value 42", text);       // but not the input value (it's I/O)
    }

    [Fact]
    public void VerboseMode_AddsMemoryWritesAndPrivilegeTransitions()
    {
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EBX, "slot");
        asm.MovImm(RegisterName.EAX, 123);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.DataInt("slot");

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Verbose);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("mem[", text);           // memory writes on
        Assert.Contains("syscall trap", text);     // privilege transitions on
        Assert.Contains("OS routine:", text);      // routine markers still present
    }

    // ---- OsLayout offset regression tests (MLFQ shift) -----------------

    // EDGE CASE: ProcessTableOffset shifted from DataBase+16 to DataBase+36 when
    // BoostTimer and QuantumTable were inserted. ConsoleVisualizer reads the process
    // table via OsLayout.ProcessEntryAddress, which derives from ProcessTableOffset.
    // If the visualizer caches a stale offset or hard-codes the old value, the
    // process table render would show garbage or crash. This test verifies that a
    // real run after LoadProcess produces a non-empty process-table block.
    [Fact]
    public void RenderProcessTable_ReadsCorrectOffsets_AfterMlfqShift()
    {
        // EDGE CASE: OsLayout.ProcessTableOffset = DataBase+36; visualizer must read
        // from this shifted address, not a stale DataBase+16 literal.

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        // A process table block is always rendered on a context switch in Normal mode.
        Assert.Contains("process table", text);
        // The slot line must show slot 0, meaning the count was read correctly.
        Assert.Contains("[0]", text);
        // The slot must not read garbage (e.g. a state of "2147483647"); accepting
        // only the three valid state names is a proxy for a correct-offset read.
        bool hasValidState = text.Contains("Ready") || text.Contains("Blocked") || text.Contains("Terminated");
        Assert.True(hasValidState, "Process table entry shows an unrecognised state — offset may be wrong.");
    }

    // EDGE CASE: The free-range map is read from FreeRangeTableOffset, which is
    // computed as ProcessTableOffset + MaxProcesses * ProcessEntrySize. With the
    // shifted ProcessTableOffset the free-range table also shifted. If the visualizer
    // reads from the old unshifted address it would display stale/wrong range data.
    [Fact]
    public void RenderFreeMemoryMap_ReadsCorrectOffset_AfterMlfqShift()
    {
        // EDGE CASE: FreeRangeTableOffset derives from the shifted ProcessTableOffset;
        // an old hard-coded offset would misread the count, potentially displaying
        // a garbage range count or crashing with a huge loop.

        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        Assert.Contains("free memory:", text);
        // A correctly read free map shows a bracketed range like "[N+M]".
        Assert.Contains("[", text);
        Assert.Contains("+", text);
        Assert.Contains("]", text);
    }

    // EDGE CASE: CurrentIndexOffset and ProcessCountOffset are at DataBase+0 and
    // DataBase+4 — these did NOT shift. The process table header line ("N slot(s),
    // current=M") must parse these correctly. Verify the current-index label matches
    // the process that just ran.
    [Fact]
    public void RenderProcessTable_CurrentIndexLabel_MatchesRunningProcess()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(3)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        // The process-table header must include "current=0" since there is only one
        // process (index 0).
        Assert.Contains("current=0", text);
    }

    // EDGE CASE: After a process terminates (HLT), a second context switch renders
    // the table with the terminated slot. Verify the visualizer does not crash when
    // iterating a table that contains a Terminated entry. The Terminated state name
    // must appear in the rendered output.
    [Fact]
    public void RenderProcessTable_AfterProcessTerminates_ShowsTerminatedSlot()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(Programs.CounterToTen()), 128, 64));

        RunSteps(os, hw, 4000);

        string text = sink.ToString();
        Assert.Contains("Terminated", text);
        Assert.Contains("[0]", text);
        Assert.Contains("[1]", text);
    }

    // EDGE CASE: With two processes that have the same MLFQ-boosted priority (0 after
    // wake), the process table must list both without showing a negative current index
    // or an index beyond the count. This guards against a visualizer that misreads
    // CurrentIndexOffset and produces "current=-1" while a process is running.
    [Fact]
    public void RenderProcessTable_CurrentIndex_IsNeverNegative_WhenProcessIsRunning()
    {
        (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) = NewRun(VisualizerMode.Normal);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));

        RunSteps(os, hw, 2000);

        string text = sink.ToString();
        // While a process is being scheduled, "current=-1" must not appear in a
        // table line rendered during an active context switch.
        Assert.DoesNotContain("current=-1", text);
    }
}
