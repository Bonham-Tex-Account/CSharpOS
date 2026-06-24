using CSharpOS;
using CSharpOSConsole;
using CSharpOSConsole.Visualization;
using OperatingSystem = CSharpOS.OperatingSystem;

// When relaunched as a per-process terminal window, run that loop and exit.
if (args.Length >= 2 && args[0] == "--terminal")
{
    string terminalTitle = "process";
    if (args.Length >= 3)
    {
        terminalTitle = args[2];
    }
    TerminalHost.Run(args[1], terminalTitle);
    return;
}

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

const int MemorySize = 32768;
const int RequiredMemory = 128;
const int RequiredStackSize = 64;
const int StepDelayMs = 250;

RegisterName[] registers = Enum.GetValues<RegisterName>();
string programDir = Path.Combine(Path.GetTempPath(), "CSharpOSPrograms");
Directory.CreateDirectory(programDir);

string counterPath = WriteProgram("counter", Programs.CounterToTen());
string averagePath = WriteProgram("average", Programs.AverageOfList());
string guessPath = WriteProgram("guess", Programs.GuessingGame());

// Short, non-interactive, self-terminating jobs of varied lifetimes, used by the
// churn modes to keep the buddy allocator / memory map busy.
string[] busyPaths =
{
    WriteProgram("busy_s", Programs.BusyThenHalt(80, 1)),
    WriteProgram("busy_m", Programs.BusyThenHalt(160, 2)),
    WriteProgram("busy_l", Programs.BusyThenHalt(240, 3))
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
    Console.WriteLine("  q) Quit");
    Console.WriteLine("  (during a run: 'a' auto, 's' single-step, left/right arrows step back/forward, 'o' toggle program I/O, 'q' quit run)");
    Console.WriteLine("  (modes 1-5 give each process its own I/O window; the churn modes 6-8 mirror I/O in the dashboard instead)");
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
            RunWindowed(new List<string> { counterPath }, PromptMode());
            break;
        case "2":
            RunWindowed(new List<string> { averagePath }, PromptMode());
            break;
        case "3":
            RunWindowed(new List<string> { guessPath }, PromptMode());
            break;
        case "4":
            RunWindowed(new List<string> { counterPath, averagePath }, PromptMode());
            break;
        case "5":
            RunWindowed(new List<string> { counterPath, averagePath, guessPath }, PromptMode());
            break;
        case "6":
            Run(Churn(3, 0), Churn(15, 3), 60, false, PromptMode());
            break;
        case "7":
            Run(Churn(8, 0), new List<Process>(), 0, false, PromptMode());
            break;
        case "8":
        {
            List<Process> initial = new List<Process>
            {
                new Process(counterPath, RequiredMemory, RequiredStackSize),
                new Process(averagePath, RequiredMemory, RequiredStackSize)
            };
            Run(initial, Churn(10, 0), 80, false, PromptMode());
            break;
        }
        default:
            Console.WriteLine("Unknown option.");
            break;
    }
}

// Builds `count` busy churn processes, cycling through the busy programs and request
// sizes (offset shifts the cycle so successive batches differ).
List<Process> Churn(int count, int offset)
{
    List<Process> list = new List<Process>();
    for (int i = 0; i < count; i++)
    {
        string path = busyPaths[(i + offset) % busyPaths.Length];
        int mem = churnSizes[(i + offset) % churnSizes.Length];
        list.Add(new Process(path, mem, RequiredStackSize));
    }
    return list;
}

// Asks which detail level to render at; defaults to Normal on blank/unknown input.
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

string WriteProgram(string name, byte[] bytes)
{
    string path = Path.Combine(programDir, name + ".bin");
    File.WriteAllBytes(path, bytes);
    return path;
}

// Windowed run: each program gets its own per-process I/O window (modes 1-5).
void RunWindowed(List<string> programPaths, VisualizerMode mode)
{
    List<Process> processes = new List<Process>();
    foreach (string path in programPaths)
    {
        processes.Add(new Process(path, RequiredMemory, RequiredStackSize));
    }
    Run(processes, new List<Process>(), 0, true, mode);
}

// Generalized run. `initial` processes load up front; `staggered` processes load one at
// a time during the run (every `interval` instructions) for memory/scheduler churn. With
// `useWindows`, each initial process gets its own I/O window; otherwise program I/O is
// mirrored into the dashboard.
void Run(List<Process> initial, List<Process> staggered, int interval, bool useWindows, VisualizerMode mode)
{
    Console.WriteLine();
    OperatingSystem os = OsPluginLoader.Load(pluginPath, Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // One terminal window per initial process, keyed by device id (== process-table
    // index). The churn modes pass no windows; their I/O is mirrored in the dashboard.
    Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal>();
    if (useWindows)
    {
        for (int i = 0; i < initial.Count; i++)
        {
            string title = Path.GetFileNameWithoutExtension(initial[i].ProgramFilePath);
            terminals[i] = new ConsoleWindowTerminal(title);
        }
    }

    ProcessIoRouter router = new ProcessIoRouter(hw, terminals);
    SpectreDashboard dashboard = new SpectreDashboard(hw, os, mode, StepDelayMs, showProgramIo: !useWindows);

    foreach (Process process in initial)
    {
        os.LoadProcess(process);
    }
    if (staggered.Count > 0)
    {
        dashboard.ScheduleStaggeredLoads(staggered, interval);
    }

    // The dashboard owns the run loop: it steps the emulator (which never blocks on
    // input — that arrives asynchronously from the per-process terminal windows),
    // redraws every step, and lets the user pace/scrub with the keyboard until every
    // process has finished.
    dashboard.Run();

    foreach (IProcessTerminal terminal in terminals.Values)
    {
        terminal.Close();
    }

    Console.WriteLine();
    Console.WriteLine("--- run finished ---");
}
