namespace CSharpOS;

/// <summary>
/// Raised on every memory write, carrying the target address and the bytes written.
/// </summary>
public class MemoryWrittenArgs : EventArgs
{
    public int Address { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
