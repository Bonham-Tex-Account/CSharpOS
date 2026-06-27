namespace CSharpOS;

/// <summary>
/// Raised when the CPU's privilege level actually changes: a syscall trap
/// (User to Kernel), an OS-routine dispatch (to Privileged), or a return to a process
/// (to User/Kernel). Surfaces the otherwise invisible transitions between user code,
/// kernel syscall handlers, and OS routines.
/// </summary>
public class PrivilegeChangedArgs : EventArgs
{
    public PrivilegeLevel From { get; init; }
    public PrivilegeLevel To { get; init; }
}
