using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// Streams the visualization as deterministic, line-oriented text to an injected
/// <see cref="TextWriter"/>. Color is an optional side-channel applied to the real
/// console only when <c>useColor</c> is set, so a captured run (e.g. into a
/// StringWriter for tests, or a log file) is plain, stable text. This renderer
/// reproduces the original ConsoleVisualizer output.
/// </summary>
public sealed class PlainTextRenderer : IVisualizerRenderer
{
    private readonly TextWriter output;
    private readonly bool useColor;
    private readonly VisualizerMode mode;
    private string lastFreeMap = "";

    public PlainTextRenderer(TextWriter output, bool useColor, VisualizerMode mode)
    {
        this.output = output;
        this.useColor = useColor;
        this.mode = mode;
    }

    // Per-tier gates. Higher tiers are supersets of lower ones.
    private bool ShowInstructions { get { return mode >= VisualizerMode.Normal; } }
    private bool ShowTables { get { return mode >= VisualizerMode.Normal; } }
    private bool ShowMemory { get { return mode >= VisualizerMode.Verbose; } }
    private bool ShowPrivilege { get { return mode >= VisualizerMode.Verbose; } }

    public void OsRoutineEntered(string name)
    {
        WriteColored(ConsoleColor.Magenta, $"      *  OS routine: {name}");
    }

    public void ContextSwitched(VisualizerModel model)
    {
        WriteColored(ConsoleColor.Cyan, $"  === context switch -> {model.CurrentProcess}");
        if (ShowTables && model.HasOsImage)
        {
            RenderProcessTable(model);
            RenderFreeMemoryIfChanged(model);
        }
    }

    public void InstructionExecuted(InstructionStep step, VisualizerModel model)
    {
        if (!ShowInstructions)
        {
            return;
        }
        SetColor(ConsoleColor.DarkGray);
        output.Write($"[{step.Process,-12}] ");
        ResetColor();
        output.Write($"{step.Address,4}: ");
        SetColor(ConsoleColor.White);
        output.Write($"{step.Mnemonic,-18}");
        ResetColor();
        output.WriteLine($"  {step.Registers.Format()}");
    }

    public void MemoryWritten(int address, int value)
    {
        if (!ShowMemory)
        {
            return;
        }
        WriteColored(ConsoleColor.DarkYellow, $"      mem[{address}] = {value}");
    }

    public void ProgramOutput(int value)
    {
        WriteColored(ConsoleColor.Green, $"      >  OUTPUT: {value}");
    }

    public void InvalidInstruction(byte opcode, string reason, string process)
    {
        WriteColored(ConsoleColor.Red, $"      XX INVALID [{opcode:X2}] in {process} - {reason}");
    }

    public void PrivilegeChanged(PrivilegeTransition transition, VisualizerModel model)
    {
        if (!ShowPrivilege)
        {
            return;
        }
        // An atomic OS-routine dispatch (interrupts masked) is already announced by
        // OsRoutineEntered; skip its transition line so it isn't doubly reported.
        if (transition.To == PrivilegeLevel.Kernel && transition.InterruptsMasked)
        {
            return;
        }
        WriteColored(ConsoleColor.DarkMagenta,
            $"      *  {transition.From} -> {transition.To}  ({transition.Description})");
    }

    public void ProcessBlocked(string process, WaitReason reason)
    {
        WriteColored(ConsoleColor.DarkYellow, $"      || {process} blocked on {reason}");
    }

    public void ProcessWoken(WaitReason reason, int value, bool showValue)
    {
        string detail = "";
        if (showValue)
        {
            detail = $" (value {value})";
        }
        WriteColored(ConsoleColor.Green, $"      >> wake signal: {reason}{detail}");
    }

    public void ProgramIoToggled(bool on)
    {
        string state = "off";
        if (on)
        {
            state = "on";
        }
        WriteColored(ConsoleColor.DarkGreen, $"      [program I/O in this window: {state}]");
    }

    private void RenderProcessTable(VisualizerModel model)
    {
        int count = model.ProcessTable.Count;
        string plural = "s";
        if (count == 1)
        {
            plural = "";
        }
        WriteColored(ConsoleColor.DarkCyan,
            $"      +- process table ({count} slot{plural}, current={model.CurrentIndex}) -");
        foreach (BuddyHeapView.ProcessRow row in model.ProcessTable)
        {
            string name = ProcessLabel(row, model.DiskView);
            string marker = " ";
            if (row.Index == model.CurrentIndex)
            {
                marker = ">";
            }
            string waitText = "";
            if (row.Wait != WaitReason.None)
            {
                waitText = $" on {row.Wait}";
            }
            WriteColored(ConsoleColor.DarkCyan,
                $"      | {marker} [{row.Index}] {name,-12} {row.State}{waitText}");
        }
    }

    private void RenderFreeMemoryIfChanged(VisualizerModel model)
    {
        List<string> parts = new List<string>();
        foreach (BuddyHeapView.FreeBlock block in model.FreeBlocks)
        {
            parts.Add($"[{block.Start}+{block.Size}]");
        }
        string map;
        if (parts.Count == 0)
        {
            map = "(none)";
        }
        else
        {
            map = string.Join(" ", parts);
        }
        if (map == lastFreeMap)
        {
            return;
        }
        lastFreeMap = map;
        WriteColored(ConsoleColor.DarkGray, $"      free memory: {map}");
    }

    public static string FriendlyName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(none)";
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// The display name for a process row. Prefers the OS-registered name (boot programs, e.g.
    /// "shell"); for a forked/exec'd process — which has no registered name — resolves the program
    /// it is running from its FS first block via the disk snapshot (e.g. an exec'd "/bin/snake" →
    /// "snake"); falls back to a "pN" slot label. This is what turns the process panels from bare
    /// numbers into program names.
    /// </summary>
    public static string ProcessLabel(BuddyHeapView.ProcessRow row, FsDiskView.Snapshot? disk)
    {
        if (!string.IsNullOrEmpty(row.Path))
        {
            return FriendlyName(row.Path);
        }
        if (disk != null && row.FirstBlock >= 0)
        {
            Dictionary<int, string> names = FsDiskView.NameByFirstBlock(disk);
            if (names.TryGetValue(row.FirstBlock, out string? name))
            {
                return name;
            }
        }
        return "p" + row.Index;
    }

    private void SetColor(ConsoleColor color)
    {
        if (useColor)
        {
            Console.ForegroundColor = color;
        }
    }

    private void ResetColor()
    {
        if (useColor)
        {
            Console.ResetColor();
        }
    }

    private void WriteColored(ConsoleColor color, string text)
    {
        SetColor(color);
        output.WriteLine(text);
        ResetColor();
    }
}
