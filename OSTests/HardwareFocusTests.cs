using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the foreground/focus model that replaced the per-process I/O windows: the
/// focused (active) process owns the live keyboard, so RaiseInputInterrupt with no
/// device id routes input to the focused process's stdin device. Verified end-to-end
/// through BasicOS so the fd resolution and the wake path are exercised too.
/// </summary>
public class HardwareFocusTests : IDisposable
{
    // Sized relative to the OS region so growing the OS never outgrows these tests.
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

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    private static byte[] EchoInput()
    {
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // Captures each process's output by source-process index, completing output so the
    // producing process continues (the console transfers instantly).
    private static Dictionary<int, List<int>> CaptureOutputs(Hardware hw)
    {
        Dictionary<int, List<int>> outputs = new Dictionary<int, List<int>>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (!outputs.TryGetValue(e.SourceProcess, out List<int>? list))
            {
                list = new List<int>();
                outputs[e.SourceProcess] = list;
            }
            list.Add(e.Value);
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    [Fact]
    public void SetActiveProcess_RoundTrips()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        Assert.Equal(-1, hw.GetActiveProcess()); // nothing focused initially
        hw.SetActiveProcess(2);
        Assert.Equal(2, hw.GetActiveProcess());
    }

    [Fact]
    public void FocusedInput_WakesOnlyTheFocusedProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Dictionary<int, List<int>> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // index 0
        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // index 1

        RunSteps(os, hw, 4000); // both block waiting on their own input
        Assert.False(os.HasRunningProcess);

        // Focus process 1: keyboard input must reach only it.
        hw.SetActiveProcess(1);
        hw.RaiseInputInterrupt(99);
        RunSteps(os, hw, 4000);

        Assert.False(outputs.ContainsKey(0));            // process 0 still blocked, no output
        Assert.Equal(new List<int> { 99 }, outputs[1]);  // process 1 echoed the focused input
        Assert.True(os.HasProcesses);                    // process 0 remains

        // Switch focus to process 0 and feed it.
        hw.SetActiveProcess(0);
        hw.RaiseInputInterrupt(42);
        RunSteps(os, hw, 4000);

        Assert.Equal(new List<int> { 42 }, outputs[0]);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void NonFocusedProcess_BlockedOnInput_StaysBlockedUntilFocused()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Dictionary<int, List<int>> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // index 0
        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // index 1

        RunSteps(os, hw, 4000);
        Assert.False(os.HasRunningProcess);

        // Focus is process 0; input typed now must not reach the unfocused process 1.
        hw.SetActiveProcess(0);
        hw.RaiseInputInterrupt(7);
        RunSteps(os, hw, 4000);

        Assert.Equal(new List<int> { 7 }, outputs[0]);
        Assert.False(outputs.ContainsKey(1)); // process 1 still blocked, never received input
        Assert.True(os.HasProcesses);
    }

    [Fact]
    public void NoFocus_InputFallsBackToDeviceZero()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Dictionary<int, List<int>> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(CreateProgramFile(EchoInput()), 128, 64)); // index 0 == device 0

        RunSteps(os, hw, 4000);
        Assert.False(os.HasRunningProcess);

        // With nothing focused (activeProcess == -1), keyboard input goes to device 0.
        Assert.Equal(-1, hw.GetActiveProcess());
        hw.RaiseInputInterrupt(13);
        RunSteps(os, hw, 4000);

        Assert.Equal(new List<int> { 13 }, outputs[0]);
        Assert.False(os.HasProcesses);
    }
}
