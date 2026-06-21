using CSharpOS;
using CSharpOSConsole;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the per-process I/O router with fake terminals: output is shown in the
/// owning process's terminal, and input entered in a terminal wakes only that
/// process — verified end-to-end through BasicOS so the per-device hardware routing
/// is exercised too.
/// </summary>
public class ProcessIoRouterTests : IDisposable
{
    private const int Memory = 16384;
    private readonly List<string> tempFiles = new List<string>();

    private sealed class FakeTerminal : IProcessTerminal
    {
        public readonly List<int> Outputs = new List<int>();
        public bool Closed;
        public event Action<int>? InputEntered;
        public void WriteOutput(int value) { Outputs.Add(value); }
        public void Close() { Closed = true; }
        public void EnterInput(int value) { InputEntered?.Invoke(value); }
    }

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

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static byte[] EchoInput()
    {
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void RoutesOutputToTheOwningTerminal()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        FakeTerminal t0 = new FakeTerminal();
        FakeTerminal t1 = new FakeTerminal();
        Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal> { { 0, t0 }, { 1, t1 } };
        ProcessIoRouter router = new ProcessIoRouter(hw, terminals);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(11)), 128, 64)); // device 0
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(22)), 128, 64)); // device 1

        RunSteps(os, hw, 4000);

        Assert.Equal(new List<int> { 11 }, t0.Outputs); // each output landed in its own terminal
        Assert.Equal(new List<int> { 22 }, t1.Outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void TerminalInput_WakesOnlyItsOwnProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        FakeTerminal t0 = new FakeTerminal();
        FakeTerminal t1 = new FakeTerminal();
        Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal> { { 0, t0 }, { 1, t1 } };
        ProcessIoRouter router = new ProcessIoRouter(hw, terminals);

        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // device 0
        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // device 1

        RunSteps(os, hw, 4000); // both block waiting on their own input
        Assert.False(os.HasRunningProcess);

        t1.EnterInput(99); // input only in terminal 1
        RunSteps(os, hw, 4000);

        Assert.Empty(t0.Outputs);                       // device-0 process still blocked
        Assert.Equal(new List<int> { 99 }, t1.Outputs); // device-1 process echoed its input
        Assert.True(os.HasProcesses);                   // device-0 process remains

        t0.EnterInput(42);
        RunSteps(os, hw, 4000);

        Assert.Equal(new List<int> { 42 }, t0.Outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void TerminalClosesWhenItsProcessFinishes()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        FakeTerminal t0 = new FakeTerminal();
        FakeTerminal t1 = new FakeTerminal();
        Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal> { { 0, t0 }, { 1, t1 } };
        ProcessIoRouter router = new ProcessIoRouter(hw, terminals);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 128, 64)); // device 0, halts
        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64));      // device 1, blocks on input

        RunSteps(os, hw, 4000);

        Assert.True(t0.Closed);   // its process halted -> window closed
        Assert.False(t1.Closed);  // still waiting on input -> window stays open
    }

    [Fact]
    public void TerminalClosesWhenItsProcessFaults()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        FakeTerminal t0 = new FakeTerminal();
        Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal> { { 0, t0 } };
        ProcessIoRouter router = new ProcessIoRouter(hw, terminals);

        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0xFF, 0, 0, 0 }), 128, 64)); // invalid opcode

        RunSteps(os, hw, 4000);

        Assert.True(t0.Closed); // faulting process tears down -> window closed
    }

    [Fact]
    public void OutputForUnmappedDevice_DoesNotThrow_AndStillCompletes()
    {
        // A process with no terminal registered should not crash the router; output
        // is simply dropped but the device is still completed so it can proceed.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Dictionary<int, IProcessTerminal> terminals = new Dictionary<int, IProcessTerminal>(); // empty
        ProcessIoRouter router = new ProcessIoRouter(hw, terminals);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(5)), 128, 64));

        RunSteps(os, hw, 4000);

        Assert.False(os.HasProcesses); // ran to completion despite no terminal
    }
}
