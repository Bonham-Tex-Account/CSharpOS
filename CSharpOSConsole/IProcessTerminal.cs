namespace CSharpOSConsole;

/// <summary>
/// A per-process terminal: the window/stream that shows one process's output and
/// collects its input. Abstracted so the routing logic can be tested with a fake,
/// while the real implementation drives a separate console window over IPC.
/// </summary>
public interface IProcessTerminal
{
    // Show a value the process emitted via OUT.
    void WriteOutput(int value);

    // Raised when the user enters a value in this terminal (on a background thread
    // for the real implementation).
    event Action<int>? InputEntered;

    // Close the terminal (and its window, for the real implementation).
    void Close();
}
