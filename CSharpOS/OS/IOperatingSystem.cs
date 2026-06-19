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
}
