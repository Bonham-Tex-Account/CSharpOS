namespace CSharpOS;

/// <summary>
/// Scheduling state of a process. The running process and all schedulable
/// processes are Ready; a process waiting on an I/O device is Blocked and is
/// skipped by the scheduler until an interrupt wakes it; a Terminated entry is a
/// free process-table slot (its process has halted) that the scheduler ignores.
/// </summary>
public enum ProcessState
{
    Ready,
    Blocked,
    Terminated,
    /// <summary>
    /// Terminated but not yet reaped: the entry is retained (holding Pid/ParentPid/
    /// ExitStatus) until the parent's wait collects the exit status.
    /// </summary>
    Zombie
}
