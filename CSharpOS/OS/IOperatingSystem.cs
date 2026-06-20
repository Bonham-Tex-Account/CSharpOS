namespace CSharpOS;

public interface IOperatingSystem
{
    // The kernel image: the assembled syscall functions that are copied into each
    // process's kernel section. Its length sizes that section (empty for now).
    byte[] KernelImage { get; }

    // The OS's own private memory region: an IVT, the assembled OS routines, and
    // the OS data structures (process table, free list, scheduler state). Hardware
    // reserves this many bytes at address 0; 0 means the OS keeps no in-memory image.
    int OsMemorySize { get; }

    // Builds the OS image (IVT + code + zeroed data) to be written at osMemoryBase.
    byte[] BuildOsImage(int osMemoryBase);

    void AttachHardware(Hardware hw);

    // Loads a program: allocates memory via the OS allocator and seeds a process
    // table entry. The scheduler (ISA, in the OS image) runs the process.
    void LoadProcess(Process process);

    // True while any process is still loaded (not terminated). The run loop stops
    // when this becomes false.
    bool HasProcesses { get; }

    // True while a process is currently scheduled on the CPU. False when every
    // process is blocked on I/O (the CPU idles until an interrupt arrives).
    bool HasRunningProcess { get; }
}
