namespace CSharpOS;

/// <summary>
/// What a Blocked process is waiting for, so the matching device interrupt wakes it.
/// </summary>
public enum WaitReason
{
    None,
    Input,
    Output,
    // Blocked in wait(pid), waiting for a child process to terminate.
    ChildProcess
}
