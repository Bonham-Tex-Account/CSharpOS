using CSharpOS;
using CSharpOSConsole;
using OperatingSystem = CSharpOS.OperatingSystem;

const int MemorySize = 4096;
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
    Console.WriteLine("  (during a run: press 's' to single-step, 'a' to resume auto)");
    Console.Write("Select: ");

    string? choice = Console.ReadLine();
    if (choice == null)
    {
        return; // end of input (e.g. piped stdin) — quit instead of looping
    }
    switch (choice.Trim())
    {
        case "1":
            Run(new List<string> { counterPath });
            break;
        case "2":
            Run(new List<string> { averagePath });
            break;
        case "3":
            Run(new List<string> { guessPath });
            break;
        case "4":
            Run(new List<string> { counterPath, averagePath });
            break;
        case "5":
            Run(new List<string> { counterPath, averagePath, guessPath });
            break;
        case "q":
        case "Q":
            return;
        default:
            Console.WriteLine("Unknown option.");
            break;
    }
}

string WriteProgram(string name, byte[] bytes)
{
    string path = Path.Combine(programDir, name + ".bin");
    File.WriteAllBytes(path, bytes);
    return path;
}

void Run(List<string> programPaths)
{
    Console.WriteLine();
    BasicOS os = new BasicOS(Console.Out);
    Hardware hw = new Hardware(MemorySize, registers, os);

    // Output device: the console transfers instantly, so signal completion right away.
    hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { hw.RaiseOutputComplete(); };

    ConsoleVisualizer visualizer = new ConsoleVisualizer(hw, os, StepDelayMs);

    foreach (string path in programPaths)
    {
        os.LoadProcess(new Process(path, RequiredMemory, RequiredStackSize));
    }

    // Boot: drain the pending queue and make the first process current,
    // avoiding a spurious trap on un-loaded memory.
    os.ContextSwitch(hw);

    while (os.HasProcesses)
    {
        hw.Run();

        // The CPU only idles when every (remaining) process is blocked on I/O.
        // Other Ready processes have already run; now read a value and raise an input
        // interrupt to wake a waiter. Reading synchronously here is fine — nothing
        // else can run. (Skip if all processes have finished.)
        if (os.HasProcesses && !os.HasRunningProcess)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("      ◆ enter a guess: ");
            Console.ResetColor();
            string? line = Console.ReadLine();
            if (line == null)
            {
                break;
            }
            if (int.TryParse(line, out int value))
            {
                hw.RaiseInputInterrupt(value);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- all processes finished ---");
}
