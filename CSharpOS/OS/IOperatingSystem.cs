namespace CSharpOS;

/// <summary>
/// The contract the hardware drives an operating system through: boot the OS image,
/// load programs, and answer the liveness queries the run loop polls.
/// </summary>
public interface IOperatingSystem
{
    /// <summary>
    /// The assembled syscall functions copied into each process's kernel section. Its
    /// length sizes that section (empty when the OS ships no syscall library).
    /// </summary>
    byte[] KernelImage { get; }

    /// <summary>
    /// Size of the OS's own private memory region (IVT, assembled OS routines, and OS
    /// data structures). Hardware reserves this many bytes at address 0; 0 means the OS
    /// keeps no in-memory image.
    /// </summary>
    int OsMemorySize { get; }

    /// <summary>Builds the OS image (IVT + code + zeroed data) to be written at <paramref name="osMemoryBase"/>.</summary>
    byte[] BuildOsImage(int osMemoryBase);

    /// <summary>Binds this OS to its hardware, reserving and seeding the OS memory region.</summary>
    void AttachHardware(Hardware hw);

    /// <summary>
    /// Loads a program: allocates memory via the OS allocator and seeds a process-table
    /// entry. The ISA scheduler in the OS image then runs the process.
    /// </summary>
    void LoadProcess(Process process);

    /// <summary>
    /// True while any process is still loaded (not terminated). The run loop stops when
    /// this becomes false.
    /// </summary>
    bool HasProcesses { get; }

    /// <summary>
    /// True while a process is currently scheduled on the CPU. False when every process
    /// is blocked on I/O (the CPU idles until an interrupt arrives).
    /// </summary>
    bool HasRunningProcess { get; }
}
