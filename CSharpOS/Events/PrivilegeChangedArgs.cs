namespace CSharpOS;

// Fired when the CPU's privilege level actually changes: a syscall trap
// (User -> Kernel), an OS-routine dispatch (-> Privileged), or a return to a
// process (-> User/Kernel). Lets a visualizer surface the otherwise invisible
// transitions between user code, kernel syscall handlers, and OS routines.
public class PrivilegeChangedArgs : EventArgs
{
    public PrivilegeLevel From { get; init; }
    public PrivilegeLevel To { get; init; }
}
