using CSharpOS;

namespace CSharpOSConsole;

/// <summary>
/// Wires each process's I/O device to its terminal window. A process's device id
/// is its process-table index, so the router maps device id -> terminal:
///   - hardware output for device N is shown in terminal N, and the output device
///     is immediately marked complete (a console transfers instantly);
///   - input entered in terminal N raises an input interrupt for device N, which
///     wakes only that process.
/// This keeps the emulator running other processes while any one waits on input.
/// </summary>
public sealed class ProcessIoRouter
{
    private readonly Hardware hw;
    private readonly IReadOnlyDictionary<int, IProcessTerminal> terminals;

    public ProcessIoRouter(Hardware hw, IReadOnlyDictionary<int, IProcessTerminal> terminals)
    {
        this.hw = hw;
        this.terminals = terminals;

        hw.ProgramOutput += OnProgramOutput;
        hw.ProcessTerminated += OnProcessTerminated;
        foreach (KeyValuePair<int, IProcessTerminal> entry in terminals)
        {
            int device = entry.Key;
            entry.Value.InputEntered += value => hw.RaiseInputInterrupt(value, device);
        }
    }

    // Close a process's terminal window as soon as the process finishes.
    private void OnProcessTerminated(object? sender, ProcessTerminatedArgs e)
    {
        if (terminals.TryGetValue(e.Device, out IProcessTerminal? terminal))
        {
            terminal.Close();
        }
    }

    private void OnProgramOutput(object? sender, ProgramOutputArgs e)
    {
        if (terminals.TryGetValue(e.Device, out IProcessTerminal? terminal))
        {
            terminal.WriteOutput(e.Value);
        }
        // The console device transfers instantly; signal completion for this device
        // so the producing process can continue (or unblock if it was waiting).
        hw.RaiseOutputComplete(e.Device);
    }
}
