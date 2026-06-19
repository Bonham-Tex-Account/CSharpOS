namespace CSharpOS;

public interface IOperatingSystem
{
    // The kernel image: the assembled syscall functions that are copied into each
    // process's kernel section. Its length sizes that section (empty for now).
    byte[] KernelImage { get; }
    void AttachHardware(Hardware hw);
    void ContextSwitch(Hardware hw);
    void HandleInvalidInstruction(Hardware hw, byte opcode, byte b1, byte b2, byte b3);
    void HandleHalt(Hardware hw);

    // Non-blocking I/O: the hardware blocks the running process when a device is
    // not ready, wakes a waiter when an interrupt arrives, and (when idle) asks the
    // OS to schedule a Ready process. HasRunningProcess is false when all processes
    // are blocked (the CPU idles until an interrupt).
    bool HasRunningProcess { get; }
    void BlockCurrentProcess(Hardware hw, WaitReason reason);
    void Wake(WaitReason reason);
    void Schedule(Hardware hw);
}
