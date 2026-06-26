namespace CSharpOS;

public class ProgramOutputArgs : EventArgs
{
    public int Value { get; init; }
    // The stdout device the output went to (resolved through the process's fd 1).
    public int Device { get; init; }
    // The process-table index that produced the output, so the host can append it to
    // that process's own screen buffer regardless of how its fds are bound.
    public int SourceProcess { get; init; }
}
