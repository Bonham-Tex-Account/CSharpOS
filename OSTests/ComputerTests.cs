using CSharpOS;

namespace OSTests;

public class ComputerTests : IDisposable
{
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

    [Fact]
    public void Constructor_EnqueuesStarterProcesses()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        List<Process> starters = new List<Process> { process };

        Computer computer = new Computer(os, 1024, Test.AllRegisters(), starters);

        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void LoadProcess_AddsToOs()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Computer computer = new Computer(os, 1024, Test.AllRegisters(), new List<Process>());
        Assert.False(os.HasProcesses);

        computer.LoadProcess(new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16));

        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void Run_SelfTerminatingProgram_EmptiesProcessList()
    {
        BasicOS os = new BasicOS(new StringWriter());
        // opcode 0x00 is not in the table, so every instruction traps and the
        // single process is torn down, eventually leaving the OS with no work.
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        List<Process> starters = new List<Process> { process };
        Computer computer = new Computer(os, 1024, Test.AllRegisters(), starters);

        computer.Run();

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (os.HasProcesses && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
        }

        Assert.False(os.HasProcesses);
    }
}
