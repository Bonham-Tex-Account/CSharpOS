using CSharpOS;
using CSharpOSConsole;
using CSharpOSConsole.Visualization;
using Spectre.Console;

namespace OSTests;

/// <summary>
/// Smoke tests for the live dashboard's render pipeline. The dashboard is a TUI, so we
/// can't assert on a real terminal, but we can drive a real BasicOS run and render the
/// whole layout once to an off-screen, plain-text Spectre console — catching markup
/// parse errors, layout failures, and ensuring every panel is produced from real run
/// data. (The pure pacing/scrub/model logic is covered deterministically elsewhere.)
/// </summary>
public class SpectreDashboardTests : IDisposable
{
    private readonly List<string> tempFiles = new List<string>();

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

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static IAnsiConsole PlainConsole(StringWriter sink)
    {
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sink)
        });
    }

    [Fact]
    public void RendersAllPanels_FromARealRun_WithoutThrowing()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        StringWriter sink = new StringWriter();
        IAnsiConsole console = PlainConsole(sink);

        // Should render the full layout from live run data without throwing.
        dashboard.RenderSnapshot(console, 2000);

        string text = sink.ToString();
        Assert.Contains("Program", text);     // program instruction panel
        Assert.Contains("Kernel", text);      // kernel instruction panel
        Assert.Contains("Buddy allocator", text);
        Assert.Contains("Registers", text);
        Assert.Contains("Process tree", text);
        Assert.Contains("Screen", text);      // shared focused-process I/O panel
    }

    [Fact]
    public void WithDiskToggledOn_RendersTheFilesystemPanel_FromARealRun()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        dashboard.ShowDisk = true; // swap the buddy panel slot to the disk view
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(7)), 128, 64));

        StringWriter sink = new StringWriter();
        // BasicOS auto-formats the FS at boot and installs the program under /bin, so the
        // disk panel renders a real, formatted filesystem — this catches markup/layout errors.
        dashboard.RenderSnapshot(PlainConsole(sink), 4000);

        string text = sink.ToString();
        Assert.Contains("Disk (filesystem)", text);
        Assert.DoesNotContain("Buddy allocator", text); // the slot is swapped, not duplicated
    }

    // Emits a single-line log string, then a multi-line "frame" string (rows split by '\n'). The
    // Screen panel's canvas mode should show only the latest multi-line frame, not the joined log.
    private static byte[] LogThenMultiLineFrame()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, 128);
        asm.MovImm(RegisterName.ECX, 7);              // "OLDLINE"
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, 160);
        asm.MovImm(RegisterName.ECX, 11);             // "ABCDE\nFGHIJ"
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Label("spin");
        asm.Jmp("spin");                              // stay alive + focused so the frame renders
        byte[] code = asm.Build();
        byte[] image = new byte[160 + 11 * 4];
        Array.Copy(code, image, code.Length);
        WriteChars(image, 128, "OLDLINE");
        WriteChars(image, 160, "ABCDE\nFGHIJ");
        return image;
    }

    private static void WriteChars(byte[] image, int offset, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            image[offset + i * 4] = (byte)s[i];       // word-per-char, low byte
        }
    }

    // Emits two plain (single-line) outputs, then spins so it stays focused. The Screen panel should
    // put each on its own line (terminal scrollback), not join them onto one row.
    private static byte[] TwoPlainOutputs()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, 128);
        asm.MovImm(RegisterName.ECX, 4);              // "AAAA"
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, 160);
        asm.MovImm(RegisterName.ECX, 4);              // "BBBB"
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Label("spin");
        asm.Jmp("spin");
        byte[] code = asm.Build();
        byte[] image = new byte[160 + 4 * 4];
        Array.Copy(code, image, code.Length);
        WriteChars(image, 128, "AAAA");
        WriteChars(image, 160, "BBBB");
        return image;
    }

    [Fact]
    public void Screen_ShowsEachOutputOnItsOwnLine_NotJoined()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        // A short display name keeps the panel header to one line so both outputs fit the small
        // headless Screen panel (a long temp-file name wraps and pushes the second output off-panel).
        Process proc = new Process(CreateProgramFile(TwoPlainOutputs()), 128, 64);
        proc.DisplayName = "io";
        os.LoadProcess(proc);

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 4000);

        string text = sink.ToString();
        Assert.Contains("AAAA", text);
        Assert.Contains("BBBB", text);
        // They must be on separate lines, not joined onto one row with the old two-space separator.
        Assert.DoesNotContain("AAAA  BBBB", text);
    }

    [Fact]
    public void Screen_CanvasMode_ShowsLatestMultiLineFrame_NotTheJoinedLog()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        os.LoadProcess(new Process(CreateProgramFile(LogThenMultiLineFrame()), 128, 64));

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 4000);

        string text = sink.ToString();
        // The first row of the latest frame is shown and the earlier log line is dropped — canvas
        // mode shows only the latest multi-line frame. (Lower rows can clip in the small headless
        // console's Screen panel; the real TUI panel is taller.)
        Assert.Contains("ABCDE", text);
        Assert.DoesNotContain("OLDLINE", text);
    }

    // Mirrors the shell → foreground-job path (e.g. typing /bin/snake): the parent FORKs a child,
    // hands it the terminal with SETFOCUS, then WAITs. The child renders a distinctive multi-line
    // frame and spins so it stays the live foreground process. The Screen panel must follow the
    // OS-designated foreground to the child, so the child's frame — not the parent's (empty) output
    // — is what shows. Regression for "snake doesn't visualize": before the focus-follows-foreground
    // fix, the panel stayed pinned to the still-live parent and the child's grid never appeared.
    private static byte[] ForkFocusChildFrame()
    {
        const int FrameOff = 128;
        Assembler asm = new Assembler();
        asm.Fork();                                   // EAX = 0 in the child, child pid in the parent
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        // Parent: EAX holds the child's pid. Hand it the foreground, then wait on it.
        asm.SetFocus(RegisterName.EAX);
        asm.Wait(RegisterName.EAX);                   // child spins forever, so this never returns
        asm.Hlt();
        asm.Label("child");
        asm.MovImm16(RegisterName.EAX, FrameOff);
        asm.MovImm(RegisterName.ECX, 11);             // "CHILD\nWORLD" (has '\n' → canvas mode)
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Label("cspin");
        asm.Jmp("cspin");                             // stay alive + focused so the frame renders
        byte[] code = asm.Build();
        byte[] image = new byte[FrameOff + 11 * 4];
        Array.Copy(code, image, code.Length);
        WriteChars(image, FrameOff, "CHILD\nWORLD");
        return image;
    }

    [Fact]
    public void Screen_FollowsForegroundChild_WhenParentHandsOffWithSetFocus()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        os.LoadProcess(new Process(CreateProgramFile(ForkFocusChildFrame()), 256, 64));

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 8000);

        string text = sink.ToString();
        // The Screen panel adopted the OS foreground (the SETFOCUS'd child), so its frame shows.
        // Before the fix, focus stayed on the still-live parent and this content never appeared.
        Assert.Contains("CHILD", text);
    }

    // Blocks reading a line (INS), then loops to read again — it produces no output of its own, so
    // anything that appears in the Screen panel came from the dashboard echoing the submitted input.
    private static byte[] ReadLineLoop()
    {
        Assembler asm = new Assembler();
        asm.Label("top");
        asm.MovImm16(RegisterName.EAX, 256);          // data-region line buffer (past the tiny image)
        asm.MovImm(RegisterName.ECX, 32);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);  // block on StringInput (the shell's INS prompt)
        asm.Jmp("top");
        return asm.Build();
    }

    [Fact]
    public void Screen_EchoesTypedInput_SoItStaysVisibleAfterEnter()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadLineLoop()), 1024, 128));
        hw.SetActiveProcess(0);

        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Verbose, 0);
        // The scripted-input path submits a line exactly as pressing Enter does (SubmitStringInput),
        // once the program is blocked at its INS prompt.
        dashboard.SetAutoInputScript(new List<string> { "typed command" });
        dashboard.RunScriptedHeadless(200_000);

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 0); // maxSteps 0 = render current state, no further run

        // The program never OUTs, so "typed command" can only appear because the dashboard echoed the
        // submitted line into the receiving process's scrollback — the fix for input vanishing on Enter.
        Assert.Contains("typed command", sink.ToString());
    }

    [Fact]
    public void ShouldFramePace_NoFocus_UsesInstructionPacing()
    {
        Assert.False(SpectreDashboard.ShouldFramePace(-1, true, new List<string> { "row1\nrow2" }));
    }

    [Fact]
    public void ShouldFramePace_FocusedNotReady_UsesInstructionPacing()
    {
        // A blocked or just-terminated focused process is not Ready → per-instruction pacing (so the
        // burst can't spin against a process that will never produce another frame).
        Assert.False(SpectreDashboard.ShouldFramePace(0, false, new List<string> { "row1\nrow2" }));
    }

    [Fact]
    public void ShouldFramePace_ReadyWithCanvasOutput_FramePaces()
    {
        // Ready + latest output is a multi-line frame → frame-pace (a full-screen redraw like snake).
        Assert.True(SpectreDashboard.ShouldFramePace(0, true, new List<string> { "row1\nrow2" }));
    }

    [Fact]
    public void ShouldFramePace_ReadyWithNoOutput_FramePaces_ToPrimeFirstFrame()
    {
        Assert.True(SpectreDashboard.ShouldFramePace(0, true, new List<string>()));
    }

    [Fact]
    public void ShouldFramePace_ReadyWithPlainOutput_UsesInstructionPacing()
    {
        // A Ready process whose latest output is a single line (not a frame) is NOT a full-screen
        // program, so it keeps per-instruction pacing.
        Assert.False(SpectreDashboard.ShouldFramePace(0, true, new List<string> { "42" }));
    }

    [Fact]
    public void RendersWithTwoProcesses_ShowingSchedulerActivity()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 128, 64));

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 4000);

        string text = sink.ToString();
        // Panel headers render reliably regardless of headless console height; inner
        // content can be clipped, so we assert the panels are present and nothing threw.
        Assert.Contains("Processes", text);
        Assert.Contains("Buddy allocator", text);
    }

    [Fact]
    public void RenderSummary_AfterRun_ShowsTotalsAndPerProcessBreakdown()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        StringWriter sink = new StringWriter();
        IAnsiConsole console = PlainConsole(sink);
        dashboard.RenderSnapshot(console, 2000); // drive the run, capturing stats
        dashboard.RenderSummary(console);

        string text = sink.ToString();
        Assert.Contains("Run summary", text);
        Assert.Contains("instructions", text);
        Assert.Contains("Per-process instructions", text);
    }

    [Fact]
    public void RendersAllPanels_WithDetailLevelLow_WithoutThrowing()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0, DetailLevel.Low);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 2000);

        string text = sink.ToString();
        Assert.Contains("Program", text);
        Assert.Contains("Buddy allocator", text);
    }

    [Fact]
    public void RendersAllPanels_WithDetailLevelMedium_WithoutThrowing()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0, DetailLevel.Medium);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 2000);

        string text = sink.ToString();
        Assert.Contains("Program", text);
        Assert.Contains("Buddy allocator", text);
    }

    [Fact]
    public void StaggeredLoads_DriveMemoryChurn_AcrossManyProcesses()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(32768), Test.AllRegisters(), os);
        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0, showProgramIo: true);

        // A short job up front, then several varied-size jobs injected over time => more
        // jobs than the 8 table slots, so slots must be recycled (allocation/free churn).
        os.LoadProcess(new Process(CreateProgramFile(Programs.BusyThenHalt(80, 1)), 128, 64));
        List<Process> staggered = new List<Process>();
        for (int i = 0; i < 8; i++)
        {
            int mem = 128 + (i % 3) * 256;
            staggered.Add(new Process(CreateProgramFile(Programs.BusyThenHalt(80, i + 1)), mem, 64));
        }
        dashboard.ScheduleStaggeredLoads(staggered, everyNInstructions: 30);

        StringWriter sink = new StringWriter();
        dashboard.RenderSnapshot(PlainConsole(sink), 20000);

        StringWriter summary = new StringWriter();
        dashboard.RenderSummary(PlainConsole(summary));
        string summaryText = summary.ToString();

        Assert.Contains("Run summary", summaryText);
        Assert.Contains("Per-process instructions", summaryText);
        Assert.False(os.HasProcesses, "all churn jobs should have finished");
    }
}
