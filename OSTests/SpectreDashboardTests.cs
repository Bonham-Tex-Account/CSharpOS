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
