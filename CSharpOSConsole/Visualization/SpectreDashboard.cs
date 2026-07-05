using System.Text;
using CSharpOS;
using OperatingSystem = CSharpOS.OperatingSystem;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// A single live "dashboard" window (built on Spectre.Console) showing the whole
/// OS/hardware visualization at once: split program/kernel instruction streams with
/// switch indicators, the MLFQ process table and ready-queues, the buddy-allocator
/// tree, registers with change highlighting, the heap/free map, and run stats. It reads
/// the <see cref="VisualizerModel"/> (kept current by <see cref="HardwareEventBridge"/>)
/// and redraws each step; the <see cref="FrameHistory"/> lets the user scrub backward and
/// forward with the arrow keys. A single shared "Screen" panel shows the focused
/// (foreground) process's I/O; Tab switches focus and digits + Enter send it a number.
/// </summary>
public sealed class SpectreDashboard
{
    private const int StreamWindow = 12;
    private const int LookBack = 80;

    private readonly Hardware hw;
    private readonly OperatingSystem os;
    private readonly VisualizerMode mode;
    private readonly DetailLevel detail;
    private readonly int renderStride;
    private readonly VisualizerModel model;
    private readonly FrameHistory frames;
    private readonly HardwareEventBridge bridge;
    private readonly InteractionController interaction;

    // Optional staggered process loading: processes are injected mid-run to keep the
    // memory/scheduler views busy (allocation + termination churn).
    private readonly Queue<Process> pendingLoads = new Queue<Process>();
    private int staggerInterval;
    private int lastLoadStep;

    // When true the shared "Buddy allocator" panel slot renders the filesystem/disk view
    // instead (toggled by the `d` key). They are alternate resource views seen one at a time.
    // Public so the headless render seam can exercise the disk panel without a keyboard.
    public bool ShowDisk { get; set; }

    public SpectreDashboard(Hardware hw, OperatingSystem os, VisualizerMode mode, int delayMs,
        DetailLevel detail = DetailLevel.High, bool showProgramIo = false)
    {
        this.hw = hw;
        this.os = os;
        this.mode = mode;
        this.detail = detail;
        if (detail == DetailLevel.Low)
        {
            renderStride = 10;
        }
        else if (detail == DetailLevel.Medium)
        {
            renderStride = 3;
        }
        else
        {
            renderStride = 1;
        }
        model = new VisualizerModel { ShowProgramIo = showProgramIo };
        frames = new FrameHistory();
        // The dashboard paces itself via the InteractionController, so the bridge's pacer
        // is inert (non-interactive, no delay).
        Pacer inertPacer = new Pacer(Console.Out, false, false, 0, () => { });
        bridge = new HardwareEventBridge(hw, os, model, new NoOpRenderer(), inertPacer, detail);
        interaction = new InteractionController(frames, true, delayMs, ToggleIo, CycleFocus, SubmitInput, SubmitStringInput, SubmitKey, ToggleDisk, ForegroundSignal);

        // The dashboard owns the single shared screen, so it also drives output
        // completion: the console transfers instantly, so each OUT is acknowledged at
        // once, letting the producing process continue (or unblock if it was waiting on
        // a busy output device). This replaces the retired per-process I/O router.
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => hw.RaiseOutputComplete(e.Device);
    }

    // ===== Focus + I/O Helpers (ToggleIo, SubmitInput, CycleFocus, EnsureFocus)
    private void ToggleIo()
    {
        model.ShowProgramIo = !model.ShowProgramIo;
    }

    // Swaps the shared Buddy/Disk panel slot between the buddy tree and the filesystem view.
    private void ToggleDisk()
    {
        ShowDisk = !ShowDisk;
    }

    // Sends a tty-style job-control signal (Ctrl-C = SigTerm, Ctrl-Z = SigStop) to the foreground
    // process, so a shell running a foreground job can be interrupted/stopped from the keyboard.
    private void ForegroundSignal(int sig)
    {
        hw.RaiseForegroundSignal(sig);
    }

    // ---- focus (foreground process) ---------------------------------------

    // Submits a typed number to the focused process's stdin (via the live keyboard
    // path, which RaiseInputInterrupt routes to the focused process's device).
    private void SubmitInput(int value)
    {
        hw.RaiseInputInterrupt(value);
    }

    // Submits a typed string to the focused process's stdin string buffer.
    private void SubmitStringInput(string value)
    {
        hw.RaiseStringInputInterrupt(value);
    }

    // Sends a raw keycode to the focused process's key input queue (for INK/INPOLL).
    private void SubmitKey(int keyCode)
    {
        hw.RaiseKeyInterrupt(keyCode);
    }

    // Tab: move focus to the next live process (wrapping).
    private void CycleFocus()
    {
        List<int> live = LiveProcessIndices();
        if (live.Count == 0)
        {
            SetFocus(-1);
            return;
        }
        int position = live.IndexOf(model.FocusedProcess);
        int next = (position + 1) % live.Count;
        SetFocus(live[next]);
    }

    // Keeps focus on a live process: if nothing is focused, or the focused process has
    // terminated, focus the lowest-index live process (or none when all are gone).
    private void EnsureFocus()
    {
        List<int> live = LiveProcessIndices();
        if (live.Count == 0)
        {
            SetFocus(-1);
            return;
        }
        if (!live.Contains(model.FocusedProcess))
        {
            SetFocus(live[0]);
        }
    }

    private void SetFocus(int index)
    {
        model.FocusedProcess = index;
        hw.SetActiveProcess(index);
    }

    private List<int> LiveProcessIndices()
    {
        List<int> live = new List<int>();
        foreach (BuddyHeapView.ProcessRow row in model.ProcessTable)
        {
            if (row.State != ProcessState.Terminated && row.State != ProcessState.Zombie)
            {
                live.Add(row.Index);
            }
        }
        return live;
    }

    /// <summary>
    /// Schedules processes to be loaded one at a time during the run — every
    /// <paramref name="everyNInstructions"/> instructions (and immediately whenever the
    /// CPU would otherwise go idle). Drives allocation/termination churn so the buddy
    /// tree and memory map stay active.
    /// </summary>
    // ===== Staggered Loading + Run Loop (ScheduleStaggeredLoads, Run, Inject) =
    public void ScheduleStaggeredLoads(IEnumerable<Process> processes, int everyNInstructions)
    {
        foreach (Process process in processes)
        {
            pendingLoads.Enqueue(process);
        }
        staggerInterval = everyNInstructions;
    }

    public void Run()
    {
        // Deliver Ctrl-C to the emulated foreground process (tty-style job control) instead of
        // terminating the visualizer. The visualizer's own quit key is 'q'. Restored on exit.
        bool priorTreatCtrlC = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
        }
        catch (IOException)
        {
            // No real console (e.g. redirected output): nothing to configure.
        }
        Layout layout = BuildLayout();
        AnsiConsole.Live(layout)
            .AutoClear(false)
            .Start(ctx =>
            {
                frames.Capture(model); // seed so the panels have a frame to draw
                RenderInto(layout);
                ctx.Refresh();

                while (true)
                {
                    InjectPendingLoads();

                    // If idle and more work is queued, load the next one immediately so
                    // the run keeps going (and the heap churns).
                    if (!os.HasProcesses && pendingLoads.Count > 0)
                    {
                        LoadNextPending();
                        RenderInto(layout);
                        ctx.Refresh();
                        continue;
                    }

                    StepAction action = interaction.NextAction();
                    if (action == StepAction.Quit)
                    {
                        break;
                    }
                    if (action == StepAction.Execute)
                    {
                        if (os.HasProcesses)
                        {
                            // Only user/kernel instructions count toward the stride; OS
                            // (Privileged-mode) instructions run at full speed in the inner loop.
                            // Without this, a 70-instruction context-switch routine would charge
                            // the 100ms NextAction delay 70 times = ~7-second freeze per switch.
                            // osGuard caps the spin when all processes are blocked on I/O.
                            int stepsToRun = interaction.Paused ? 1 : renderStride;
                            int userSteps = 0;
                            int osGuard = 0;
                            while (userSteps < stepsToRun && os.HasProcesses && osGuard < 10000)
                            {
                                int before = model.InstructionCount;
                                hw.Run();
                                if (model.InstructionCount > before)
                                {
                                    frames.Capture(model);
                                    userSteps++;
                                    osGuard = 0;
                                }
                                else
                                {
                                    osGuard++;
                                }
                            }
                            // Yield at the render boundary (not per OS step) when idle.
                            if (!os.HasRunningProcess)
                            {
                                Thread.Sleep(15);
                            }
                        }
                        // Refresh after every batch: OS routines that ran inside the
                        // osGuard spin (termination, priority writes) update memory but
                        // don't fire ContextSwitched, so the model would otherwise lag.
                        // Always capture so the live-edge frame reflects the refreshed model
                        // even when no user instruction ran (e.g. one process terminated
                        // while the other is blocked waiting for input).
                        bridge.RefreshProcessTable();
                        frames.Capture(model);
                        RenderInto(layout);
                        ctx.Refresh();
                    }
                    else
                    {
                        // Redraw: time-travel or key state change — always render immediately.
                        RenderInto(layout);
                        ctx.Refresh();
                    }
                }

                RenderInto(layout);
                ctx.Refresh();
            });

        try
        {
            Console.TreatControlCAsInput = priorTreatCtrlC;
        }
        catch (IOException)
        {
        }
        RenderSummary(AnsiConsole.Console);
    }

    private void InjectPendingLoads()
    {
        if (pendingLoads.Count == 0 || staggerInterval <= 0)
        {
            return;
        }
        if (model.InstructionCount - lastLoadStep >= staggerInterval)
        {
            LoadNextPending();
        }
    }

    private void LoadNextPending()
    {
        Process next = pendingLoads.Dequeue();
        os.LoadProcess(next);
        lastLoadStep = model.InstructionCount;
    }

    /// <summary>
    /// Headless render path for tests/smoke checks: advances the emulator up to
    /// <paramref name="maxSteps"/> instructions (capturing frames), then renders the
    /// full dashboard once to the supplied console. Exercises the entire build pipeline
    /// without a live display or keyboard.
    /// </summary>
    // ===== Headless Testing Seams (RenderSnapshot, RenderSummary) ============
    public void RenderSnapshot(IAnsiConsole console, int maxSteps)
    {
        frames.Capture(model);
        int steps = 0;
        while ((os.HasProcesses || pendingLoads.Count > 0) && steps < maxSteps)
        {
            InjectPendingLoads();
            if (!os.HasProcesses)
            {
                if (pendingLoads.Count > 0)
                {
                    LoadNextPending();
                    continue;
                }
                break;
            }
            int before = model.InstructionCount;
            hw.Run();
            if (model.InstructionCount > before)
            {
                frames.Capture(model);
                steps++;
            }
        }
        Layout layout = BuildLayout();
        RenderInto(layout);
        console.Write(layout);
    }

    /// <summary>
    /// Renders the end-of-run summary: aggregate counters plus a per-process breakdown
    /// of how many instructions each program executed. Written once after the live loop.
    /// </summary>
    public void RenderSummary(IAnsiConsole console)
    {
        Table totals = new Table();
        totals.Border = TableBorder.Rounded;
        totals.Title = new TableTitle("Run summary");
        totals.AddColumn("metric");
        totals.AddColumn("value");
        totals.AddRow("instructions", model.InstructionCount.ToString());
        totals.AddRow("context switches", model.ContextSwitchCount.ToString());
        totals.AddRow("privilege transitions", model.Transitions.Count.ToString());
        totals.AddRow("faults", model.FaultCount.ToString());
        totals.AddRow("blocks", model.BlockCount.ToString());
        totals.AddRow("wakes", model.WakeCount.ToString());
        totals.AddRow("outputs", model.OutputCount.ToString());
        totals.AddRow("branches", $"{model.BranchPredictions} (acc {model.BranchAccuracy:P0}, miss {model.BranchMisses})");
        totals.AddRow("cycles", model.Cycles.ToString());

        Table perProcess = new Table();
        perProcess.Border = TableBorder.Rounded;
        perProcess.Title = new TableTitle("Per-process instructions");
        perProcess.AddColumn("process");
        perProcess.AddColumn("instructions");
        List<KeyValuePair<string, int>> rows = new List<KeyValuePair<string, int>>(model.InstructionsByProcess);
        rows.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (KeyValuePair<string, int> entry in rows)
        {
            perProcess.AddRow(Markup.Escape(entry.Key), entry.Value.ToString());
        }

        console.Write(new Rows(totals, perProcess));
    }

    // ---- layout ------------------------------------------------------------

    // ===== Layout + Top-Level Render (BuildLayout, RenderInto, Panel) ========
    private static Layout BuildLayout()
    {
        return new Layout("root").SplitRows(
            new Layout("streams").SplitColumns(
                new Layout("program"),
                new Layout("kernel")),
            new Layout("middle").SplitColumns(
                new Layout("procs"),
                new Layout("buddy"),
                new Layout("screen")),
            new Layout("lower").SplitColumns(
                new Layout("registers"),
                new Layout("heap"),
                new Layout("tree")),
            new Layout("status").Size(3));
    }

    private void RenderInto(Layout layout)
    {
        Frame? frame = frames.Current;
        if (frame == null)
        {
            return;
        }

        // Keep focus pointed at a live process (auto-advances when the focused one ends).
        EnsureFocus();

        (IRenderable program, IRenderable kernel) = BuildStreams(frame);
        layout["program"].Update(Panel("Program (user)", program, ActiveColor(frame, true)));
        layout["kernel"].Update(Panel("Kernel / OS", kernel, ActiveColor(frame, false)));
        layout["procs"].Update(Panel("Processes (MLFQ)", BuildProcessAndQueues(frame), Color.Grey));
        if (ShowDisk)
        {
            layout["buddy"].Update(Panel("Disk (filesystem)", BuildFsDisk(frame), Color.Grey));
        }
        else
        {
            layout["buddy"].Update(Panel("Buddy allocator", BuildBuddyTree(frame), Color.Grey));
        }
        layout["screen"].Update(Panel("Screen (focused I/O)", BuildScreen(), Color.Grey));
        layout["registers"].Update(Panel("Registers", BuildRegisters(frame), Color.Grey));
        layout["heap"].Update(Panel("Heap / free memory", BuildHeap(frame), Color.Grey));
        layout["tree"].Update(Panel("Process tree", BuildProcessTree(frame), Color.Grey));
        layout["status"].Update(BuildStatus(frame));
    }

    private static Panel Panel(string title, IRenderable body, Color border)
    {
        Panel panel = new Panel(body);
        panel.Header = new PanelHeader($" {title} ");
        panel.Border = BoxBorder.Rounded;
        panel.BorderColor(border);
        panel.Expand = true;
        return panel;
    }

    // Highlights the side that is currently executing.
    private static Color ActiveColor(Frame frame, bool programSide)
    {
        bool programActive = frame.Privilege == PrivilegeLevel.User;
        if (programActive == programSide)
        {
            return Color.Aqua;
        }
        return Color.Grey;
    }

    // ---- instruction streams ----------------------------------------------

    private (IRenderable Program, IRenderable Kernel) BuildStreams(Frame frame)
    {
        List<IRenderable> program = new List<IRenderable>();
        List<IRenderable> kernel = new List<IRenderable>();

        // Use the live tail of History; HistoryLength is capped at MaxHistoryLength so
        // absolute indices would shift as old entries are evicted from the front.
        int end = model.History.Count;
        int start = Math.Max(0, end - LookBack);
        bool havePrev = false;
        PrivilegeLevel prev = PrivilegeLevel.User;

        for (int i = start; i < end; i++)
        {
            InstructionStep step = model.History[i];
            bool isProgram = step.IsProgramSide;

            if (havePrev && OnProgramSide(prev) != isProgram)
            {
                // Control crossed sides: annotate the side being entered. Recorded steps
                // are only user code and the (preemptible) syscall handler — never an
                // atomic OS routine — so interrupts are not masked at these crossings.
                string desc = HardwareEventBridge.DescribeTransition(prev, step.Privilege, false);
                IRenderable marker = new Markup($"[grey39]── {Markup.Escape(desc)} @{step.Address} ──[/]");
                if (isProgram)
                {
                    program.Add(marker);
                }
                else
                {
                    kernel.Add(marker);
                }
            }

            bool isLast = i == end - 1;
            IRenderable row = new Markup(FormatStep(step, isLast));
            if (isProgram)
            {
                program.Add(row);
            }
            else
            {
                kernel.Add(row);
            }

            prev = step.Privilege;
            havePrev = true;
        }

        return (TailRows(program), TailRows(kernel));
    }

    private static bool OnProgramSide(PrivilegeLevel level)
    {
        return level == PrivilegeLevel.User;
    }

    private static string FormatStep(InstructionStep step, bool isLast)
    {
        string text = $"{step.Address,5}: {Markup.Escape(step.Mnemonic)}";
        if (isLast)
        {
            return $"[white on grey23]▶ {text}[/]";
        }
        return $"[grey85]  {text}[/]";
    }

    private static IRenderable TailRows(List<IRenderable> rows)
    {
        if (rows.Count == 0)
        {
            return new Markup("[grey39](idle)[/]");
        }
        int skip = Math.Max(0, rows.Count - StreamWindow);
        return new Rows(rows.GetRange(skip, rows.Count - skip));
    }

    // ---- process table + scheduler queues ---------------------------------

    // ===== MLFQ + Buddy Panels (BuildProcessAndQueues, BuildQueues, BuildBuddyTree)
    private IRenderable BuildProcessAndQueues(Frame frame)
    {
        if (!frame.HasOsImage)
        {
            return new Markup("[grey39](no OS image)[/]");
        }

        Table table = new Table();
        table.Border = TableBorder.Minimal;
        table.Expand = true;
        table.AddColumn(new TableColumn("[grey]#[/]"));
        table.AddColumn(new TableColumn("[grey]name[/]"));
        table.AddColumn(new TableColumn("[grey]state[/]"));
        table.AddColumn(new TableColumn("[grey]lvl[/]"));
        table.AddColumn(new TableColumn("[grey]ticks[/]"));

        foreach (BuddyHeapView.ProcessRow row in frame.ProcessTable)
        {
            string marker = " ";
            if (row.Index == frame.CurrentIndex)
            {
                marker = "[aqua]▶[/]";
            }
            string name = Markup.Escape(PlainTextRenderer.FriendlyName(row.Path));
            string state = StateMarkup(row);
            table.AddRow(
                new Markup($"{marker}{row.Index}"),
                new Markup(name),
                new Markup(state),
                new Markup(row.Priority.ToString()),
                new Markup(row.TicksUsed.ToString()));
        }

        IRenderable queues = BuildQueues(frame);
        return new Rows(table, new Markup("[grey]ready queues[/]"), queues);
    }

    private static string StateMarkup(BuddyHeapView.ProcessRow row)
    {
        string text = row.State.ToString();
        if (row.Wait != WaitReason.None)
        {
            text = $"{text}/{row.Wait}";
        }
        if (row.State == ProcessState.Ready)
        {
            return $"[green]{text}[/]";
        }
        if (row.State == ProcessState.Blocked)
        {
            return $"[yellow]{Markup.Escape(text)}[/]";
        }
        if (row.State == ProcessState.Terminated)
        {
            return $"[grey39]{text}[/]";
        }
        return Markup.Escape(text);
    }

    private static IRenderable BuildQueues(Frame frame)
    {
        List<IRenderable> lines = new List<IRenderable>();
        for (int level = 0; level < OsLayout.QueueCount; level++)
        {
            List<string> names = new List<string>();
            foreach (BuddyHeapView.ProcessRow row in frame.ProcessTable)
            {
                if (row.State == ProcessState.Ready && row.Priority == level)
                {
                    names.Add(PlainTextRenderer.FriendlyName(row.Path));
                }
            }
            string joined = "·";
            if (names.Count > 0)
            {
                joined = Markup.Escape(string.Join(", ", names));
            }
            lines.Add(new Markup($"[grey]L{level}[/] {joined}"));
        }
        return new Rows(lines);
    }

    // ---- buddy allocator tree ---------------------------------------------
    // Shows only the leaf nodes (Free and Allocated blocks) in address order as a
    // flat list. The recursive Spectre Tree widget overflows the panel for deep trees
    // (6 levels = 63 nodes); this always fits regardless of tree depth.

    private static IRenderable BuildBuddyTree(Frame frame)
    {
        if (frame.BuddyTree == null)
        {
            return new Markup("[grey39](heap not configured)[/]");
        }
        List<BuddyHeapView.Segment> segments = BuddyHeapView.Flatten(frame.BuddyTree);
        if (segments.Count == 0)
        {
            return new Markup("[grey39](empty)[/]");
        }
        List<IRenderable> lines = new List<IRenderable>();
        foreach (BuddyHeapView.Segment segment in segments)
        {
            lines.Add(new Markup(SegmentLabel(segment)));
        }
        return new Rows(lines);
    }

    private static string SegmentLabel(BuddyHeapView.Segment segment)
    {
        if (segment.Kind == BuddyHeapView.BuddyNodeKind.Free)
        {
            return $"[green]FREE  {segment.Base}+{segment.Size}[/]";
        }
        string owner = PlainTextRenderer.FriendlyName(segment.OwnerPath);
        return $"[red]ALLOC {segment.Base}+{segment.Size}[/] [grey]{Markup.Escape(owner)}[/]";
    }

    // ---- filesystem / disk view -------------------------------------------
    // Alternate content for the buddy panel slot (key `d`): a superblock stats header, the
    // directory tree as an indented list, and a per-block allocation map (S super / B bitmap
    // / # used / · free). Reads only the immutable frame snapshot (rebuilt by the bridge).

    private static IRenderable BuildFsDisk(Frame frame)
    {
        FsDiskView.Snapshot? disk = frame.DiskView;
        if (disk == null)
        {
            return new Markup("[grey39](no filesystem)[/]");
        }
        if (!disk.Formatted)
        {
            return new Markup("[grey39](disk not formatted)[/]");
        }
        List<IRenderable> lines = new List<IRenderable>();
        lines.Add(new Markup($"[grey]blocks[/] {disk.BlockCount}  [green]free[/] {disk.FreeCount}  [red]used[/] {disk.UsedCount}  [grey]root[/] {disk.RootBlock}"));
        foreach (FsDiskView.TreeRow row in FsDiskView.FlattenTree(disk))
        {
            lines.Add(new Markup(DiskTreeLabel(row)));
        }
        lines.Add(BuildBlockMap(disk));
        return new Rows(lines);
    }

    private static string DiskTreeLabel(FsDiskView.TreeRow row)
    {
        string indent = new string(' ', row.Depth * 2);
        string name = Markup.Escape(row.Node.Name);
        if (row.Node.IsDir)
        {
            if (row.Node.Name == "/")
            {
                return $"{indent}[aqua]/[/]";
            }
            return $"{indent}[aqua]{name}/[/]";
        }
        return $"{indent}[grey85]{name}[/] [grey]{row.Node.Size}B[/]";
    }

    private static IRenderable BuildBlockMap(FsDiskView.Snapshot disk)
    {
        const int perRow = 32;
        List<IRenderable> rows = new List<IRenderable>();
        StringBuilder line = new StringBuilder();
        for (int b = 0; b < disk.BlockRoles.Length; b++)
        {
            line.Append(BlockGlyph(disk.BlockRoles[b]));
            if ((b + 1) % perRow == 0 || b == disk.BlockRoles.Length - 1)
            {
                rows.Add(new Markup(line.ToString()));
                line.Clear();
            }
        }
        if (rows.Count == 0)
        {
            return new Markup("[grey39](no blocks)[/]");
        }
        return new Rows(rows);
    }

    private static string BlockGlyph(FsDiskView.BlockRole role)
    {
        if (role == FsDiskView.BlockRole.Super)
        {
            return "[yellow]S[/]";
        }
        if (role == FsDiskView.BlockRole.Bitmap)
        {
            return "[blue]B[/]";
        }
        if (role == FsDiskView.BlockRole.Used)
        {
            return "[red]#[/]";
        }
        return "[grey19]·[/]";
    }

    // ---- registers ---------------------------------------------------------

    // ===== Registers + Heap Panels (BuildRegisters, BuildHeap, BuildMapBar...) =
    private static IRenderable BuildRegisters(Frame frame)
    {
        if (frame.Registers == null)
        {
            return new Markup("[grey39](no registers)[/]");
        }
        List<IRenderable> lines = new List<IRenderable>();
        for (int i = 0; i < RegisterSnapshot.Shown.Length; i++)
        {
            string name = RegisterSnapshot.Shown[i].ToString();
            int value = frame.Registers.Values[i];
            bool changed = frame.Registers.Changed(frame.PreviousRegisters, i);
            if (changed)
            {
                lines.Add(new Markup($"[yellow]{name}={value}[/]"));
            }
            else
            {
                lines.Add(new Markup($"[grey85]{name}={value}[/]"));
            }
        }
        string zero = "-";
        if (frame.Registers.Zero)
        {
            zero = "Z";
        }
        string sign = "-";
        if (frame.Registers.Sign)
        {
            sign = "S";
        }
        lines.Add(new Markup($"[grey]flags[/] [[{zero}{sign}]]"));
        lines.Add(new Markup($"[grey]mode[/] {frame.Privilege}"));
        return new Rows(lines);
    }

    // ---- process tree (rendered inside the procs panel) --------------------

    private static IRenderable BuildProcessTree(Frame frame)
    {
        if (!frame.HasOsImage || frame.ProcessTable.Count == 0)
        {
            return new Markup("[grey39](no processes)[/]");
        }

        Dictionary<int, BuddyHeapView.ProcessRow> byPid = new Dictionary<int, BuddyHeapView.ProcessRow>();
        foreach (BuddyHeapView.ProcessRow row in frame.ProcessTable)
        {
            byPid[row.Pid] = row;
        }

        List<IRenderable> lines = new List<IRenderable>();
        foreach (BuddyHeapView.ProcessRow row in frame.ProcessTable)
        {
            if (row.ParentPid == -1 || !byPid.ContainsKey(row.ParentPid))
            {
                RenderTreeNode(row, frame.ProcessTable, "", true, lines);
            }
        }
        return new Rows(lines);
    }

    private static void RenderTreeNode(
        BuddyHeapView.ProcessRow row,
        IReadOnlyList<BuddyHeapView.ProcessRow> allRows,
        string prefix,
        bool isRoot,
        List<IRenderable> lines)
    {
        string name = Markup.Escape(PlainTextRenderer.FriendlyName(row.Path));
        string nodePrefix = isRoot ? "[aqua]◉[/] " : $"{prefix}[grey]└─[/] ";
        lines.Add(new Markup($"{nodePrefix}[white]{name}[/][grey]({row.Pid})[/] {StateMarkup(row)}"));

        string childPrefix = isRoot ? "  " : prefix + "   ";
        foreach (BuddyHeapView.ProcessRow child in allRows)
        {
            if (child.ParentPid == row.Pid)
            {
                RenderTreeNode(child, allRows, childPrefix, false, lines);
            }
        }
    }

    // ---- heap: linear memory map + free summary ---------------------------

    private const int MapWidth = 28;
    private static readonly string[] OwnerPalette =
    {
        "aqua", "fuchsia", "yellow", "orange1", "deepskyblue1", "springgreen3", "mediumpurple", "gold3"
    };

    private static IRenderable BuildHeap(Frame frame)
    {
        if (!frame.HasOsImage)
        {
            return new Markup("[grey39](no OS image)[/]");
        }
        List<BuddyHeapView.Segment> segments = BuddyHeapView.Flatten(frame.BuddyTree);
        int heapSize = 0;
        foreach (BuddyHeapView.Segment s in segments)
        {
            heapSize += s.Size;
        }
        if (heapSize == 0)
        {
            return new Markup("[grey39](heap not configured)[/]");
        }

        List<IRenderable> lines = new List<IRenderable>();
        lines.Add(new Markup(BuildMapBar(segments, heapSize)));
        lines.Add(BuildMapLegend(segments));

        int freeTotal = 0;
        foreach (BuddyHeapView.FreeBlock block in frame.FreeBlocks)
        {
            freeTotal += block.Size;
        }
        int usedPercent = (int)Math.Round(100.0 * (heapSize - freeTotal) / heapSize);
        lines.Add(new Markup($"[grey]free[/] {freeTotal}/{heapSize}  [grey]used[/] {usedPercent}%"));
        return new Rows(lines);
    }

    private static string BuildMapBar(List<BuddyHeapView.Segment> segments, int heapSize)
    {
        List<string> cells = new List<string>();
        foreach (BuddyHeapView.Segment segment in segments)
        {
            int width = (int)Math.Round((double)segment.Size / heapSize * MapWidth);
            if (width < 1)
            {
                width = 1;
            }
            string color = "green";
            if (segment.Kind == BuddyHeapView.BuddyNodeKind.Allocated)
            {
                color = OwnerColor(PlainTextRenderer.FriendlyName(segment.OwnerPath));
            }
            cells.Add($"[on {color}]{new string(' ', width)}[/]");
        }
        return string.Concat(cells);
    }

    private static IRenderable BuildMapLegend(List<BuddyHeapView.Segment> segments)
    {
        List<string> seen = new List<string>();
        List<string> swatches = new List<string>();
        swatches.Add("[on green]  [/] free");
        foreach (BuddyHeapView.Segment segment in segments)
        {
            if (segment.Kind != BuddyHeapView.BuddyNodeKind.Allocated)
            {
                continue;
            }
            string owner = PlainTextRenderer.FriendlyName(segment.OwnerPath);
            if (seen.Contains(owner))
            {
                continue;
            }
            seen.Add(owner);
            swatches.Add($"[on {OwnerColor(owner)}]  [/] {Markup.Escape(owner)}");
        }
        return new Markup(string.Join("  ", swatches));
    }

    private static string OwnerColor(string owner)
    {
        int hash = 0;
        foreach (char c in owner)
        {
            hash = (hash * 31 + c) & 0x7FFFFFFF;
        }
        return OwnerPalette[hash % OwnerPalette.Length];
    }

    // ---- shared screen (focused process I/O) ------------------------------
    // One screen bound to the focused (foreground) process: it shows that process's
    // own output and the number being typed to it. Tab switches which process is shown.

    private const int ScreenLines = 8;

    // ===== Screen + Status (BuildScreen, FocusedName, BuildStatus) ===========
    private IRenderable BuildScreen()
    {
        List<IRenderable> lines = new List<IRenderable>();
        if (model.FocusedProcess < 0)
        {
            lines.Add(new Markup("[grey39](no focused process)[/]"));
        }
        else
        {
            string name = Markup.Escape(FocusedName());
            lines.Add(new Markup($"[aqua]▶ P{model.FocusedProcess}[/] [grey]{name}[/]  [grey](Tab switches)[/]"));
        }

        IReadOnlyList<string> outputs = model.FocusedOutput();
        if (outputs.Count == 0)
        {
            lines.Add(new Markup("[grey39](no output yet)[/]"));
        }
        else if (outputs[outputs.Count - 1].Contains('\n'))
        {
            // Canvas mode: the latest output is a full multi-line frame (e.g. a snake grid). Show only
            // that frame — its newlines render as rows — instead of the horizontally-joined scroll log.
            // Auto-detected by shape, so a program that redraws a whole screen each frame just works.
            lines.Add(new Markup("[grey85]" + Markup.Escape(outputs[outputs.Count - 1]) + "[/]"));
        }
        else
        {
            int skip = Math.Max(0, outputs.Count - ScreenLines);
            List<string> shown = new List<string>();
            for (int i = skip; i < outputs.Count; i++)
            {
                shown.Add(outputs[i]);
            }
            lines.Add(new Markup("[grey85]" + Markup.Escape(string.Join("  ", shown)) + "[/]"));
        }

        lines.Add(new Markup($"[grey]>[/] {Markup.Escape(interaction.InputLine)}"));
        return new Rows(lines);
    }

    private string FocusedName()
    {
        foreach (BuddyHeapView.ProcessRow row in model.ProcessTable)
        {
            if (row.Index == model.FocusedProcess)
            {
                return PlainTextRenderer.FriendlyName(row.Path);
            }
        }
        return "?";
    }

    // ---- status footer -----------------------------------------------------

    private IRenderable BuildStatus(Frame frame)
    {
        string position = $"step {frame.StepNumber}/{model.InstructionCount}";
        string state = "[green]live[/]";
        if (!frames.AtLiveEdge)
        {
            state = "[yellow]reviewing history[/]";
        }
        else if (!os.HasProcesses && pendingLoads.Count == 0)
        {
            state = "[red]finished — ← → to review, q to quit[/]";
        }
        else if (interaction.Paused)
        {
            state = "[aqua]paused[/]";
        }
        string keys = interaction.KeyPassthrough
            ? "[yellow]F1[/] [yellow bold]KEY PASSTHROUGH[/]  all keys → process  [grey](F1 to exit)[/]"
            : "[grey]a[/] auto  [grey]s[/] step  [grey]←/→[/] hist  [grey]Tab[/] focus  [grey]0-9/⏎[/] input  [grey]o[/] I/O  [grey]d[/] disk  [grey]q[/] quit  [grey]F1[/] passthrough";
        return new Panel(new Markup($"{position}   {state}   process [aqua]{Markup.Escape(frame.CurrentProcess)}[/]   [grey]perf[/] {detail}   {keys}"))
        {
            Border = BoxBorder.None,
            Expand = true
        };
    }
}
