namespace CSharpOS;

/// <summary>
/// Raised when a process is torn down (via HLT or an invalid-instruction fault), so a
/// host can release per-process resources such as its terminal window. Device is the
/// terminating process's device id (== its process-table index).
/// </summary>
public class ProcessTerminatedArgs : EventArgs
{
    public int Device { get; init; }
}
