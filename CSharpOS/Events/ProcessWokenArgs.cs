namespace CSharpOS;

// Fired when a device interrupt is delivered and the OS wake routine is
// dispatched to make a process waiting on that reason Ready again. Value carries
// the input that arrived (for InputReady interrupts); 0 for output completion.
public class ProcessWokenArgs : EventArgs
{
    public WaitReason Reason { get; init; }
    public int Value { get; init; }
}
