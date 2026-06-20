namespace CSharpOS;

/// <summary>
/// CPU privilege levels, saved per process and restored on context switch.
/// User: normal process code. Kernel: I/O syscall handlers (ISA code in the
/// kernel section); preemptible. Privileged: OS/hardware primitives such as
/// termination and the context-switch routine; atomic (not preempted).
/// </summary>
public enum PrivilegeLevel
{
    User,
    Kernel,
    Privileged
}
