namespace CSharpOS;

/// <summary>
/// A contiguous span of memory (a start address and a byte length). Used to describe
/// the regions a process may legally address for bounds checking.
/// </summary>
public struct MemoryRange
{
    /// <summary>Start address of the range.</summary>
    public int Start;
    /// <summary>Length of the range in bytes.</summary>
    public int Size;
}
