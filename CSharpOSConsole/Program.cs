using System.Text;
using CSharpOS;
using CSharpOSConsole;
using CSharpOSConsole.Visualization;
using OperatingSystem = CSharpOS.OperatingSystem;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// Resolve OS plugin path: --os-plugin <path> overrides the default.
string pluginPath = Path.Combine(AppContext.BaseDirectory, "BasicOSPlugin.dll");
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--os-plugin")
    {
        pluginPath = args[i + 1];
        break;
    }
}

// Machine memory must exceed the OS region (OsLayout.TotalSize) with room for the buddy
// heap on top; deriving it from TotalSize keeps it from silently going stale when the OS
// region grows (as it did in Shell §2, when TotalSize crossed the old hardcoded 32768 and
// left the heap starting past the end of memory). +32768 = an exact-power-of-two 32 KB heap.
const int MemorySize = OsLayout.TotalSize + 32768;
const int RequiredMemory = 128;
const int RequiredStackSize = 64;
const int StepDelayMs = 100;

RegisterName[] registers = Enum.GetValues<RegisterName>();

// Program images, built once. Each run stages the ones it needs onto its own
// machine's disk (Hardware.Disk) and references them by slot — there are no program
// files; the disk is the program store.
StagedProgram counter = new StagedProgram("counter", Programs.CounterToTen(), RequiredMemory, RequiredStackSize);
StagedProgram average = new StagedProgram("average", Programs.AverageOfList(), RequiredMemory, RequiredStackSize);
StagedProgram guess = new StagedProgram("guess", Programs.GuessingGame(), RequiredMemory, RequiredStackSize);
StagedProgram spawn = new StagedProgram("spawner", Programs.SpawnChildren(), RequiredMemory, RequiredStackSize);
(byte[] stringsImage, int stringsMemory) = Programs.StringsDemo();
StagedProgram strings = new StagedProgram("strings", stringsImage, stringsMemory, RequiredStackSize);
StagedProgram fsdemo = new StagedProgram("fsdemo", Programs.FilesystemDemo(), RequiredMemory, RequiredStackSize);

// Short, non-interactive, self-terminating jobs of varied lifetimes, used by the
// churn modes to keep the buddy allocator / memory map busy. The churn loop assigns
// each a request size, so these carry only their image and a name.
(string Name, byte[] Bytes)[] busyPrograms =
{
    ("busy_s", Programs.BusyThenHalt(80, 1)),
    ("busy_m", Programs.BusyThenHalt(160, 2)),
    ("busy_l", Programs.BusyThenHalt(240, 3))
};
// Varied request sizes so allocations land on different buddy block sizes.
int[] churnSizes = { 128, 512, 1024 };

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== CSharpOS Visualizer ===");
    Console.WriteLine($"  OS Plugin: {pluginPath}");
    Console.WriteLine("  1) Counter to ten");
    Console.WriteLine("  2) Average of a list");
    Console.WriteLine("  3) Guessing game (interactive, secret = 42)");
    Console.WriteLine("  4) Counter + Average together (round-robin scheduling)");
    Console.WriteLine("  5) All three together");
    Console.WriteLine("  6) Memory churn (short jobs load & exit continuously — watch the buddy tree & memory map)");
    Console.WriteLine("  7) Fill & drain the heap (mixed-size jobs fill memory, then drain — watch reclaim/merging)");
    Console.WriteLine("  8) Scheduler + memory (counter + average run while short jobs churn the heap)");
    Console.WriteLine("  9) Shell (interactive: type a command id to fork/exec a program — fork/exec/wait/setfocus)");
    Console.WriteLine(" 10) Two guessing games (Tab to switch focus, test process switching)");
    Console.WriteLine(" 11) Spawn tree (parent forks two children — watch parent-child tree in Process tree panel)");
    Console.WriteLine(" 12) String I/O demo (type a name in the Screen panel, press Enter — OUTS/INS in action)");
    Console.WriteLine(" 13) Filesystem demo (a process creates a file, writes/reads it via FSYS — prints 'HI!' from disk)");
    Console.WriteLine(" 14) Auto-shell tour (hands-free: the shell runs help/ls/echo/cat/counter — watch fork/exec & the process tree)");
    Console.WriteLine(" 15) Auto-shell job control (hands-free: launches background jobs, then `jobs` — watch the process tree fill in)");
    Console.WriteLine("  q) Quit");
    Console.WriteLine("  (during a run: 'a' auto, 's' single-step, left/right arrows scrub history, 'o' toggle program I/O, 'q' quit run)");
    Console.WriteLine("  (one shared Screen panel shows the focused process; Tab switches focus, type text + Enter sends it as int or string)");
    Console.Write("Select: ");

    string? choice = Console.ReadLine();
    if (choice == null)
    {
        return; // end of input (e.g. piped stdin) - quit instead of looping
    }

    string trimmed = choice.Trim();
    if (trimmed == "q" || trimmed == "Q")
    {
        return;
    }

    switch (trimmed)
    {
        case "1":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { counter }, mode, detail);
            break;
        }
        case "2":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { average }, mode, detail);
            break;
        }
        case "3":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { guess }, mode, detail);
            break;
        }
        case "4":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { counter, average }, mode, detail);
            break;
        }
        case "5":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { counter, average, guess }, mode, detail);
            break;
        }
        case "6":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            Run(Churn(3, 0), Churn(15, 3), 60, mode, detail);
            break;
        }
        case "7":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            Run(Churn(8, 0), new List<StagedProgram>(), 0, mode, detail);
            break;
        }
        case "8":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            List<StagedProgram> initial = new List<StagedProgram> { counter, average };
            Run(initial, Churn(10, 0), 80, mode, detail);
            break;
        }
        case "9":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShell(mode, detail);
            break;
        }
        case "10":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { guess, guess }, mode, detail);
            break;
        }
        case "11":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { spawn }, mode, detail);
            break;
        }
        case "12":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { strings }, mode, detail);
            break;
        }
        case "13":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            RunShared(new List<StagedProgram> { fsdemo }, mode, detail);
            break;
        }
        case "14":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            // Auto-shell tour: fork/exec a few commands hands-free — watch the Program/Kernel
            // streams, the Screen panel's output, and the Process tree grow a child per command.
            List<string> tour = new List<string>
            {
                "/bin/help",
                "/bin/ls /",
                "/bin/echo hello from the auto shell",
                "/bin/cat /note",
                "/bin/counter",
            };
            RunShell(mode, detail, tour);
            break;
        }
        case "15":
        {
            VisualizerMode mode = PromptMode();
            DetailLevel detail = PromptDetail();
            // Auto-shell job control: launch several background jobs so the Process tree shows the
            // shell with multiple concurrent children, list them with `jobs`, then they finish/reap.
            List<string> jobs = new List<string>
            {
                "/bin/counter &",
                "/bin/counter &",
                "/bin/counter &",
                "jobs",
                "/bin/echo three background jobs launched",
            };
            RunShell(mode, detail, jobs);
            break;
        }
        default:
            Console.WriteLine("Unknown option.");
            break;
    }
}

// Builds `count` busy churn requests, cycling through the busy programs and request
// sizes (offset shifts the cycle so successive batches differ).
List<StagedProgram> Churn(int count, int offset)
{
    List<StagedProgram> list = new List<StagedProgram>();
    for (int i = 0; i < count; i++)
    {
        (string name, byte[] bytes) = busyPrograms[(i + offset) % busyPrograms.Length];
        int mem = churnSizes[(i + offset) % churnSizes.Length];
        list.Add(new StagedProgram(name, bytes, mem, RequiredStackSize));
    }
    return list;
}

// Asks which visualizer verbosity mode to use; defaults to Normal on blank/unknown input.
VisualizerMode PromptMode()
{
    Console.WriteLine("  Detail: 1) minimal  2) normal  3) verbose");
    Console.Write("  Select [2]: ");
    string? line = Console.ReadLine();
    if (line != null)
    {
        switch (line.Trim())
        {
            case "1":
                return VisualizerMode.Minimal;
            case "3":
                return VisualizerMode.Verbose;
        }
    }
    return VisualizerMode.Normal;
}

// Asks which performance level to use; defaults to High on blank/unknown input.
DetailLevel PromptDetail()
{
    Console.WriteLine("  Performance: 1) low (fast)  2) medium  3) high (full detail)");
    Console.Write("  Select [3]: ");
    string? line = Console.ReadLine();
    if (line != null)
    {
        switch (line.Trim())
        {
            case "1":
                return DetailLevel.Low;
            case "2":
                return DetailLevel.Medium;
        }
    }
    return DetailLevel.High;
}

// Shared-screen run: the initial programs load up front and share one screen (modes 1-5).
void RunShared(List<StagedProgram> programs, VisualizerMode mode, DetailLevel detail)
{
    Run(programs, new List<StagedProgram>(), 0, mode, detail);
}

// Shell run (mode 9): stage the demo programs to disk as commands, boot the shell as the
// only initial process, and let the user type a command id to fork/exec it. Exercises
// FORK / EXEC / SETFOCUS / WAIT live in the dashboard.
void RunShell(VisualizerMode mode, DetailLevel detail, IReadOnlyList<string>? autoScript = null)
{
    Console.WriteLine();
    OperatingSystem os = OsPluginLoader.Load(pluginPath, Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // Install the shell's /bin command programs (and a couple of demo programs) into the filesystem
    // before any process is loaded — FsImage staging is boot-time only. The shell exec-by-paths them
    // by absolute path. The FS is already formatted (BasicOS auto-formats at boot).
    FsImage.EnsureDir(hw, "/bin");
    FsImage.WriteFile(hw, "/bin/ls", Programs.Ls());
    FsImage.WriteFile(hw, "/bin/cat", Programs.Cat());
    FsImage.WriteFile(hw, "/bin/rm", Programs.Rm());
    FsImage.WriteFile(hw, "/bin/mkdir", Programs.Mkdir());
    FsImage.WriteFile(hw, "/bin/echo", Programs.Echo());
    FsImage.WriteFile(hw, "/bin/help", Programs.Help());
    FsImage.WriteFile(hw, "/bin/edit", Programs.Edit());   // §4.0: author a source file (end input with ".")
    FsImage.WriteFile(hw, "/bin/as", Programs.As());       // §4.2: assemble a source file into a runnable image
    FsImage.WriteFile(hw, "/bin/counter", Programs.CounterToTen());
    FsImage.WriteFile(hw, "/bin/average", Programs.AverageOfList());
    FsImage.WriteFile(hw, "/bin/guess", Programs.GuessingGame());
    FsImage.WriteFile(hw, "/bin/snake", Programs.Snake());   // a playable game (arrow keys, 'q' quits)
    // A text file for `cat` to read (word-per-char, the way cat/OUTS read file content).
    string noteText = "hello from the filesystem";
    byte[] note = new byte[noteText.Length * 4];
    for (int n = 0; n < noteText.Length; n++)
    {
        note[n * 4] = (byte)noteText[n];
    }
    FsImage.WriteFile(hw, "/note", note);
    // A ready-to-assemble sample so the write→compile→run loop can be tried without editing first:
    //   /bin/as /hello.s /bin/hi   then   /bin/hi   → prints 72. Source is word-per-char, like /note.
    string helloSrc = "MOV EAX 72\nOUT EAX\nHLT\n";
    byte[] helloBytes = new byte[helloSrc.Length * 4];
    for (int h = 0; h < helloSrc.Length; h++)
    {
        helloBytes[h * 4] = (byte)helloSrc[h];
    }
    FsImage.WriteFile(hw, "/hello.s", helloBytes);

    int shellSlot = hw.Disk.Store(Programs.Shell());

    if (autoScript == null)
    {
        Console.WriteLine("  Shell: focus it (Tab), type an absolute command + Enter (it forks/execs, then re-prompts):");
        Console.WriteLine("    /bin/help   /bin/ls /   /bin/echo hi there   /bin/cat /note   /bin/counter   /bin/snake");
        Console.WriteLine("    (snake: arrow keys steer, 'q' quits; Ctrl-C kills the foreground job, /bin/snake & backgrounds)");
        Console.WriteLine("    (run speed: '+' faster / '-' slower — a full-screen program like snake auto-paces one frame per tick)");
        Console.WriteLine("  Write -> compile -> run, all inside the OS:");
        Console.WriteLine("    /bin/as /hello.s /bin/hi   then   /bin/hi          (assemble the bundled sample, then run it)");
        Console.WriteLine("    /bin/edit /prog.s   (type asm lines, end with a lone \".\")   /bin/as /prog.s /bin/prog   /bin/prog");
    }
    else
    {
        Console.WriteLine("  Auto-shell demo: the shell is driven automatically — watch the Program/Kernel streams,");
        Console.WriteLine("  the Screen panel, and the Process tree as each command forks and runs. Scripted commands:");
        foreach (string cmd in autoScript)
        {
            Console.WriteLine($"    $ {cmd}");
        }
        Console.WriteLine("  ('a' auto / 's' step / Tab focus / 'q' quit — you can still take over the keyboard.)");
    }
    Console.WriteLine();

    SpectreDashboard dashboard = new SpectreDashboard(hw, os, mode, StepDelayMs, detail);
    if (autoScript != null)
    {
        dashboard.SetAutoInputScript(autoScript);
    }
    // The shell needs enough memory for the largest program it execs into: exec preserves the
    // process's RequiredMemory, so /bin/snake (grid + render buffer in DATA at ~2–3.4 KB) inherits
    // this. 4096 gives it room; the shell's own DATA (LineBuf/jobs table) fits easily.
    Process shellProcess = new Process(shellSlot, 4096, RequiredStackSize);
    shellProcess.DisplayName = "shell";   // so the process panels label P0 as "shell", not "slot N"
    os.LoadProcess(shellProcess);
    dashboard.Run();

    Console.WriteLine();
    Console.WriteLine("--- run finished ---");
}

// Generalized run. `initial` programs load up front; `staggered` programs load one at a
// time during the run (every `interval` instructions) for memory/scheduler churn. Every
// program is first staged onto this machine's disk and referenced by slot. All processes
// share a single screen in the dashboard, bound to the focused (foreground) process; Tab
// switches focus and the live keyboard feeds the focused process's input.
void Run(List<StagedProgram> initial, List<StagedProgram> staggered, int interval, VisualizerMode mode, DetailLevel detail)
{
    Console.WriteLine();
    OperatingSystem os = OsPluginLoader.Load(pluginPath, Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // Stage every image onto the disk up front, so each Process references a real slot
    // before it is loaded (the staggered ones load later, during the run).
    List<Process> initialProcesses = StageAll(hw, initial);
    List<Process> staggeredProcesses = StageAll(hw, staggered);

    SpectreDashboard dashboard = new SpectreDashboard(hw, os, mode, StepDelayMs, detail);

    foreach (Process process in initialProcesses)
    {
        os.LoadProcess(process);
    }
    if (staggeredProcesses.Count > 0)
    {
        dashboard.ScheduleStaggeredLoads(staggeredProcesses, interval);
    }

    // The dashboard owns the run loop: it steps the emulator (which never blocks on
    // input — keystrokes arrive through the dashboard's own key loop), redraws every
    // step, and lets the user pace/scrub/type until every process has finished.
    dashboard.Run();

    Console.WriteLine();
    Console.WriteLine("--- run finished ---");
}

// Stores each program's image on the machine's disk and returns slot-based processes.
List<Process> StageAll(Hardware hw, List<StagedProgram> programs)
{
    List<Process> processes = new List<Process>();
    foreach (StagedProgram program in programs)
    {
        int slot = hw.Disk.Store(program.Bytes);
        processes.Add(new Process(slot, program.Memory, program.Stack));
    }
    return processes;
}

// A program image plus the memory/stack a process needs to run it. Staged onto a
// machine's disk inside Run, then referenced by slot.
class StagedProgram
{
    public string Name;
    public byte[] Bytes;
    public int Memory;
    public int Stack;

    public StagedProgram(string name, byte[] bytes, int memory, int stack)
    {
        Name = name;
        Bytes = bytes;
        Memory = memory;
        Stack = stack;
    }
}

