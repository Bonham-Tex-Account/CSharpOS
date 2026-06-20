namespace CSharpOS;

public class ContextSwitchArgs : EventArgs
{
    public string? FromProcess { get; init; }
    public string ToProcess { get; init; } = "";
}
