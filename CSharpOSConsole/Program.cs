using CSharpOS;
using CSharpOSConsole;
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

const int MemorySize = 16384;
const int RequiredMemory = 128;
const int RequiredStackSize = 64;
const int StepDelayMs = 250;

RegisterName[] registers = Enum.GetValues<RegisterName>();
string programDir = Path.Combine(Path.GetTempPath(), "CSharpOSPrograms");
Directory.CreateDirectory(programDir);

string counterPath = WriteProgram("counter", Programs.CounterToTen());
string averagePath = WriteProgram("average", Programs.AverageOfList());
string guessPath = WriteProgram("guess", Programs.GuessingGame());

while (true)
{
    Console.WriteLine();
    Console.WriteLine("=== CSharpOS Visualizer ===");
    Console.WriteLine("  1) Counter to ten");
    Console.WriteLine("  2) Average of a list");
    Console.WriteLine("  3) Guessing game (interactive, secret = 42)");
    Console.WriteLine("  4) Counter + Average together (round-robin scheduling)");
    Console.WriteLine("  5) All three together");
    Console.WriteLine("  q) Quit");
    Console.WriteLine("  (during a run: 's' single-step, 'a' resume auto, 'o' toggle program I/O in this window)");
    Console.WriteLine("  (each process gets its own window for input/output; it closes when the process ends)");
    Console.Write("Select: ");

    string? choice = Console.ReadLine();
    if (choice == null)
    {
        return; // end of input (e.g. piped stdin) - quit instead of looping
    }
    List<string>? programs = null;
    switch (choice.Trim())
    {
        case "1":
            programs = new List<string> { counterPath };
            break;
        case "2":
            programs = new List<string> { averagePath };
            break;
        case "3":
            programs = new List<string> { guessPath };
            break;
        case "4":
            programs = new List<string> { counterPath, averagePath };
            break;
        case "5":
            programs = new List<string> { counterPath, averagePath, guessPath };
            break;
        case "q":
        case "Q":
            return;
        default:
            Console.WriteLine("Unknown option.");
            break;
    }

    if (programs != null)
    {
        VisualizerMode mode = PromptMode();
        Run(programs, mode);
    }
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

void Run(List<string> programPaths, VisualizerMode mode)
{
    Console.WriteLine();
    BasicOS os = new BasicOS(Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // One terminal window per process, keyed by device id. Processes are loaded in
    // order, so the i-th process gets process-table index i == device i. Building
    // the terminals before loading keeps that mapping aligned.
    Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal>();
    for (int i = 0; i < programPaths.Count; i++)
    {
        string title = Path.GetFileNameWithoutExtension(programPaths[i]);
        terminals[i] = new ConsoleWindowTerminal(title);
    }

    // The router moves each process's I/O to its own window; this main window shows
    // only the OS/Hardware activity.
    ProcessIoRouter router = new ProcessIoRouter(hw, terminals);
    ConsoleVisualizer visualizer = new ConsoleVisualizer(hw, os, StepDelayMs, mode: mode);

    foreach (string path in programPaths)
    {
        os.LoadProcess(new Process(path, RequiredMemory, RequiredStackSize));
    }

    // The emulator runs continuously and never blocks on input: input arrives
    // asynchronously from the terminal windows (raising per-device interrupts), so
    // while one process waits the scheduler keeps running the others. When every
    // process is blocked the CPU is genuinely idle, so yield briefly to avoid a
    // busy-spin until a window delivers input.
    while (os.HasProcesses)
    {
        hw.Run();
        if (os.HasProcesses && !os.HasRunningProcess)
        {
            Thread.Sleep(15);
        }
    }

    foreach (IProcessTerminal terminal in terminals.Values)
    {
        terminal.Close();
    }

    Console.WriteLine();
    Console.WriteLine("--- all processes finished ---");
}
