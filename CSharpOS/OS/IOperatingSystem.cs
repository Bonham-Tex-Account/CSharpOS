namespace CSharpOS;

public interface IOperatingSystem
{
    void AttachHardware(Hardware hw);
    void ContextSwitch(Hardware hw);
    void HandleInvalidInstruction(Hardware hw, byte opcode, byte b1, byte b2, byte b3);
    void HandleHalt(Hardware hw);
}
