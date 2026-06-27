namespace CSharpOS;

/// <summary>
/// CPU privilege levels, saved per process and restored on context switch. Two levels,
/// like real hardware: User (normal process code, restricted) and Kernel (the shared
/// syscall handler and the OS routines — unrestricted, addressing the OS region
/// absolutely). Atomicity of OS routines is not a privilege level: it is the hardware
/// interrupt-enable flag (see <see cref="Hardware.InterruptsEnabled"/>). OS routines run
/// with interrupts masked (atomic); the syscall handler runs with them enabled (preemptible).
/// </summary>
public enum PrivilegeLevel
{
    User,
    Kernel
}
