namespace CSharpOS;

/// <summary>
/// Raised when an OS routine resumes a different process than was last running.
/// Hardware fires it with program base addresses (it has no process names); a
/// consumer maps base to name.
/// </summary>
public class ContextSwitchArgs : EventArgs
{
    public string? FromProcess { get; init; }
    public string ToProcess { get; init; } = "";

    /// <summary>Program base address of the outgoing process.</summary>
    public int FromProgramBase { get; init; } = -1;
    /// <summary>Program base address of the incoming process.</summary>
    public int ToProgramBase { get; init; } = -1;
}
