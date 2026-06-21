namespace CSharpOS;

public class ProgramOutputArgs : EventArgs
{
    public int Value { get; init; }
    // The device (== owning process's table index) the output came from, so a
    // multi-window host can route it to that process's terminal.
    public int Device { get; init; }
}
