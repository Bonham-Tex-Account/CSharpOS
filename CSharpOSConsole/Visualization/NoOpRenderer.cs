using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// A renderer that draws nothing. Used with the live Spectre dashboard, which reads the
/// <see cref="VisualizerModel"/> directly each frame rather than reacting to individual
/// events — so the bridge only needs to keep the model up to date.
/// </summary>
public sealed class NoOpRenderer : IVisualizerRenderer
{
    public void OsRoutineEntered(string name) { }
    public void ContextSwitched(VisualizerModel model) { }
    public void InstructionExecuted(InstructionStep step, VisualizerModel model) { }
    public void MemoryWritten(int address, int value) { }
    public void ProgramOutput(int value) { }
    public void InvalidInstruction(byte opcode, string reason, string process) { }
    public void PrivilegeChanged(PrivilegeTransition transition, VisualizerModel model) { }
    public void ProcessBlocked(string process, WaitReason reason) { }
    public void ProcessWoken(WaitReason reason, int value, bool showValue) { }
    public void ProgramIoToggled(bool on) { }
}
