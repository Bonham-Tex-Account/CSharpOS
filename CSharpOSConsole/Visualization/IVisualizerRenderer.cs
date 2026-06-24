using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// Consumes the semantic events the <see cref="HardwareEventBridge"/> derives from
/// Hardware and renders them. Implementations decide how (a streaming text log, a
/// live Spectre dashboard, ...) and which tiers to show. The bridge keeps the
/// <see cref="VisualizerModel"/> up to date and passes it in for context.
/// </summary>
public interface IVisualizerRenderer
{
    void OsRoutineEntered(string name);
    void ContextSwitched(VisualizerModel model);
    void InstructionExecuted(InstructionStep step, VisualizerModel model);
    void MemoryWritten(int address, int value);
    void ProgramOutput(int value);
    void InvalidInstruction(byte opcode, string reason, string process);
    void PrivilegeChanged(PrivilegeTransition transition, VisualizerModel model);
    void ProcessBlocked(string process, WaitReason reason);
    void ProcessWoken(WaitReason reason, int value, bool showValue);
    void ProgramIoToggled(bool on);
}
