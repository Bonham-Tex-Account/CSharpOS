using CSharpOS;
using CSharpOSConsole;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers SETFOCUS (map a PID to the foreground process) and the shell program, which
/// ties the whole spawning family together: read a command, FORK, child EXECs it, parent
/// SETFOCUSes the child and WAITs for it, then loops.
/// </summary>
public class ShellTests : IDisposable
{
    private static int Memory => Test.MachineWithHeap(16384);
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

    private static List<int> CaptureOutputs(Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            outputs.Add(e.Value);
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void SetFocus_MapsPidToTheForegroundProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 64, 64)); // PID 1, slot 0
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 64, 64)); // PID 2, slot 1

        Assert.Equal(-1, hw.GetActiveProcess());

        hw.SetFocus(2); // focus the process with PID 2 (slot 1)
        Assert.Equal(1, hw.GetActiveProcess());

        hw.SetFocus(1); // focus PID 1 (slot 0)
        Assert.Equal(0, hw.GetActiveProcess());

        hw.SetFocus(99); // unknown PID: focus unchanged
        Assert.Equal(0, hw.GetActiveProcess());
    }

    [Fact]
    public void Shell_ForksAndExecsACommand_FromTypedInput()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os); // ctor stages kernel image
        List<int> outputs = CaptureOutputs(hw);

        int commandSlot = hw.Disk.Store(PrintThenHalt(55)); // the program the shell will run
        int shellSlot = hw.Disk.Store(Programs.Shell());
        os.LoadProcess(new Process(shellSlot, 256, 64));     // the shell, slot 0

        // Focus the shell and type the command id (the command program's disk slot).
        hw.SetActiveProcess(0);
        hw.RaiseInputInterrupt(commandSlot);

        // Run until the shell has forked + execed the command, which prints 55.
        for (int i = 0; i < 60000 && !outputs.Contains(55); i++)
        {
            hw.Run();
        }

        Assert.Contains(55, outputs); // the command ran via the shell's fork/exec
        Assert.True(os.HasProcesses); // the shell itself loops, still alive
    }
}
