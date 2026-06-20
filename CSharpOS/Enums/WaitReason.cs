namespace CSharpOS;

/// <summary>
/// What a Blocked process is waiting for, so the matching device interrupt wakes it.
/// </summary>
public enum WaitReason
{
    None,
    Input,
    Output
}
