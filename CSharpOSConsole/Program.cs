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

const int MemorySize = 32768;
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
void RunShell(VisualizerMode mode, DetailLevel detail)
{
    Console.WriteLine();
    OperatingSystem os = OsPluginLoader.Load(pluginPath, Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // Demo programs live on disk as shell commands (not loaded as processes). The shell
    // reads a disk-slot number and forks/execs it.
    int counterCmd = hw.Disk.Store(Programs.CounterToTen());
    int averageCmd = hw.Disk.Store(Programs.AverageOfList());
    int guessCmd = hw.Disk.Store(Programs.GuessingGame());
    int shellSlot = hw.Disk.Store(Programs.Shell());

    Console.WriteLine("  Shell: focus it (Tab), type a command id then Enter to run it:");
    Console.WriteLine($"    {counterCmd} = counter,  {averageCmd} = average,  {guessCmd} = guessing game (secret 42)");
    Console.WriteLine();

    SpectreDashboard dashboard = new SpectreDashboard(hw, os, mode, StepDelayMs, detail);
    // The shell needs enough memory for the largest program it execs into.
    os.LoadProcess(new Process(shellSlot, 512, RequiredStackSize));
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

