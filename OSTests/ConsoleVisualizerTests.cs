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
    // returned StringWriter. The output device completes instantly so OUT does not
    // block. The visualizer is returned only to keep it referenced.
    private (Hardware hw, BasicOS os, StringWriter sink, ConsoleVisualizer vis) NewRun()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        StringWriter sink = new StringWriter();
        ConsoleVisualizer vis = new ConsoleVisualizer(hw, os, 0, sink, useColor: false, interactive: false);
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { hw.RaiseOutputComplete(); };
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
}
