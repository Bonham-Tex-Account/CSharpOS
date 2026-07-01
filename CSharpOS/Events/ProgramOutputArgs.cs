namespace CSharpOS;

/// <summary>
/// Raised when a process emits an output value (a kernel OUT). Carries the value, the
/// stdout device it went to, and the producing process so a host can route it.
/// </summary>
public class ProgramOutputArgs : EventArgs
{
    public int Value { get; init; }
    /// <summary>Non-null when this output came from OUTS (string output); null for OUT (int output).</summary>
    public string? StringValue { get; init; }
    /// <summary>The stdout device the output went to (resolved through the process's fd 1).</summary>
    public int Device { get; init; }
    /// <summary>
    /// The process-table index that produced the output, so the host can append it to
    /// that process's own screen buffer regardless of how its fds are bound.
    /// </summary>
    public int SourceProcess { get; init; }
}
