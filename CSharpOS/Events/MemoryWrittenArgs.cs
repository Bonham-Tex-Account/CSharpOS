namespace CSharpOS;

public class MemoryWrittenArgs : EventArgs
{
    public int Address { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
}
