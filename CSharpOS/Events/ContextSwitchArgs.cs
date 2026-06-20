namespace CSharpOS;

public class ContextSwitchArgs : EventArgs
{
    public string? FromProcess { get; init; }
    public string ToProcess { get; init; } = "";

    // Program base addresses of the outgoing/incoming process. Hardware fires the
    // event with these (it has no process names); a consumer maps base -> name.
    public int FromProgramBase { get; init; } = -1;
    public int ToProgramBase { get; init; } = -1;
}
