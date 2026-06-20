namespace CSharpOS;

/// <summary>
/// Scheduling state of a process. The running process and all schedulable
/// processes are Ready; a process waiting on an I/O device is Blocked and is
/// skipped by the scheduler until an interrupt wakes it.
/// </summary>
public enum ProcessState
{
    Ready,
    Blocked
}
