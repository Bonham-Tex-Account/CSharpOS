namespace CSharpOS;

/// <summary>
/// Top-level wiring of a running machine: pairs an <see cref="OperatingSystem"/> with
/// a <see cref="Hardware"/> instance, loads the starter processes, and drives the
/// hardware run loop on a background thread.
/// </summary>
public class Computer
{
    private Hardware hardware;
    private OperatingSystem os;

    /// <summary>
    /// Builds the machine: constructs the hardware (which attaches the OS), then loads
    /// each starter process through the OS allocator.
    /// </summary>
    public Computer(OperatingSystem os, int memorySize, RegisterName[] registerNames, List<Process> starterProcesses)
    {
        this.os = os;
        hardware = new Hardware(memorySize, registerNames, os);

        foreach (Process process in starterProcesses)
        {
            os.LoadProcess(process);
        }
    }

    /// <summary>Loads an additional process into the running machine.</summary>
    public void LoadProcess(Process process)
    {
        os.LoadProcess(process);
    }

    /// <summary>
    /// Starts the machine on a background thread, stepping the hardware until no
    /// process remains. When every process is blocked on I/O the loop sleeps briefly
    /// to wait for an interrupt rather than busy-spinning.
    /// </summary>
    public void Run()
    {
        Thread runThread = new Thread(() =>
        {
            while (os.HasProcesses)
            {
                hardware.Run();
                if (!os.HasRunningProcess)
                {
                    // Every process is blocked on I/O; wait briefly for an interrupt
                    // instead of busy-spinning.
                    Thread.Sleep(1);
                }
            }
        });
        runThread.IsBackground = true;
        runThread.Start();
    }
}
