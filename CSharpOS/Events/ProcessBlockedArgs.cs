namespace CSharpOS;

// Fired when the running process blocks on I/O (a kernel IN with no buffered
// input, or a kernel OUT while the device is busy) and the OS switches away.
public class ProcessBlockedArgs : EventArgs
{
    public WaitReason Reason { get; init; }
}
