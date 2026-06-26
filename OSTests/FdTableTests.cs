using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers per-process file descriptors and the per-device wait queue: LoadProcess
/// seeds stdin/stdout to the process's own device (the shim), KernelInput resolves
/// fd 0 (so redirecting it reads another device), blocking registers the process on
/// the device's wait queue, and an input interrupt wakes the queued waiter(s).
/// </summary>
public class FdTableTests : IDisposable
{
    private readonly List<string> tempFiles = new List<string>();
    private const int OtherDevice = 50; // an unused character device, not a process slot

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csosfd_" + Guid.NewGuid().ToString("N") + ".bin");
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

    private static byte[] ReadInto(RegisterName reg)
    {
        Assembler asm = new Assembler();
        asm.In(reg);
        asm.Out(reg);
        asm.Hlt();
        return asm.Build();
    }

    private static List<int> BootCollecting(Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            outputs.Add(e.Value);
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    private static void Step(BasicOS os, Hardware hw, int n)
    {
        for (int i = 0; i < n && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    private static int FdAddress(int slot, int fd)
    {
        return OsLayout.ProcessEntryAddress(slot) + Hardware.ProcessEntryFdTable + fd * Test.WordSize;
    }

    [Fact]
    public void LoadProcess_SeedsStdInAndStdOutToOwnDevice()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 0
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 1

        Assert.Equal(0, Test.ReadWord(hw, FdAddress(0, Hardware.StdIn)));
        Assert.Equal(0, Test.ReadWord(hw, FdAddress(0, Hardware.StdOut)));
        Assert.Equal(1, Test.ReadWord(hw, FdAddress(1, Hardware.StdIn)));
        Assert.Equal(1, Test.ReadWord(hw, FdAddress(1, Hardware.StdOut)));
    }

    [Fact]
    public void Input_OnOwnDevice_IsDeliveredViaFd0()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 0, fd0 -> device 0
        List<int> outputs = BootCollecting(hw);
        Step(os, hw, 8000); // blocks on input

        hw.RaiseInputInterrupt(42, 0);
        Step(os, hw, 8000);

        Assert.Equal(new List<int> { 42 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Fd0Redirection_ReadsFromTheRedirectedDevice()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 0
        // Point this process's stdin at OtherDevice instead of its own device.
        Test.WriteWord(hw, FdAddress(0, Hardware.StdIn), OtherDevice);
        List<int> outputs = BootCollecting(hw);
        Step(os, hw, 8000); // runs IN, blocks on OtherDevice

        Assert.False(os.HasRunningProcess);
        // The process is queued on the redirected device, not on its own device 0.
        Assert.Contains(0, hw.GetDevice(OtherDevice).Waiters);
        Assert.DoesNotContain(0, hw.GetDevice(0).Waiters);

        hw.RaiseInputInterrupt(77, OtherDevice);
        Step(os, hw, 8000);

        Assert.Equal(new List<int> { 77 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void InputInterrupt_RemovesTheWokenWaiterFromTheDeviceQueue()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 0
        Test.WriteWord(hw, FdAddress(0, Hardware.StdIn), OtherDevice);
        List<int> outputs = BootCollecting(hw);
        Step(os, hw, 8000);

        Assert.Equal(new List<int> { 0 }, hw.GetDevice(OtherDevice).Waiters);

        hw.RaiseInputInterrupt(9, OtherDevice);
        Step(os, hw, 8000);

        Assert.Empty(hw.GetDevice(OtherDevice).Waiters);
        Assert.Equal(new List<int> { 9 }, outputs);
    }

    [Fact]
    public void MultipleProcessesOnOneDevice_AreWokenBySuccessiveInterrupts()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 0
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // slot 1
        // Both processes share one input device.
        Test.WriteWord(hw, FdAddress(0, Hardware.StdIn), OtherDevice);
        Test.WriteWord(hw, FdAddress(1, Hardware.StdIn), OtherDevice);
        List<int> outputs = BootCollecting(hw);
        Step(os, hw, 8000); // both run IN and block on OtherDevice

        List<int> waiters = hw.GetDevice(OtherDevice).Waiters;
        Assert.Contains(0, waiters);
        Assert.Contains(1, waiters);
        Assert.Equal(2, waiters.Count);

        hw.RaiseInputInterrupt(10, OtherDevice); // wakes the first waiter only
        Step(os, hw, 8000);
        Assert.Equal(new List<int> { 10 }, outputs);
        Assert.True(os.HasProcesses); // the second process is still blocked

        hw.RaiseInputInterrupt(20, OtherDevice); // wakes the second waiter
        Step(os, hw, 8000);
        Assert.Equal(new List<int> { 10, 20 }, outputs);
        Assert.False(os.HasProcesses);
    }
}
