namespace CSharpOS;

// Fired when Hardware dispatches an OS routine through the IVT, naming which one
// (ContextSwitch, Halt, Schedule, Block, Wake, InvalidInstruction, LoadProcess)
// so a visualizer can mark exactly which OS function is running.
public class OsRoutineArgs : EventArgs
{
    public int Slot { get; init; }
    public string Name { get; init; } = "";
}
