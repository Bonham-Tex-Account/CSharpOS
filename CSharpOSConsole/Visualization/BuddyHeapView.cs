using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// Reads the OS's externally-stored state out of hardware memory and turns it into
/// structured, render-agnostic data: the process table, the buddy free-block list,
/// and the reconstructed buddy allocation tree. All offsets come from the public
/// <see cref="OsLayout"/> / <see cref="Hardware"/> constants, so there are no magic
/// numbers and the reader stays in lock-step with the kernel layout.
///
/// Buddy bitmap semantics (see BasicOSPlugin/OsRoutines.cs): one bit per tree node,
/// bit = 1 means the whole subtree is FREE, bit = 0 means used or split. Node i
/// (1-indexed) lives at bit i-1. Because a cleared bit alone cannot tell an
/// allocated whole-block from a split node, allocated nodes are pinned precisely
/// from the process table (ProgramAddress + block size), which also yields the
/// owning process name for each allocated block.
/// </summary>
public static class BuddyHeapView
{
    public enum BuddyNodeKind
    {
        Free,
        Allocated,
        Split
    }

    public sealed class ProcessRow
    {
        public int Index { get; init; }
        public int Pid { get; init; }
        public int ParentPid { get; init; }
        public int ProgramAddress { get; init; }
        public string? Path { get; init; }
        public ProcessState State { get; init; }
        public WaitReason Wait { get; init; }
        public int Priority { get; init; }
        public int TicksUsed { get; init; }
    }

    public sealed class FreeBlock
    {
        public int Start { get; init; }
        public int Size { get; init; }
    }

    public sealed class BuddyNode
    {
        public int Level { get; init; }
        public int Node { get; init; }     // 1-indexed tree node
        public int Base { get; init; }     // physical heap address
        public int Size { get; init; }     // bytes
        public BuddyNodeKind Kind { get; set; }
        public string? OwnerPath { get; set; }
        public List<BuddyNode> Children { get; } = new List<BuddyNode>();
    }

    /// <summary>One contiguous heap segment in address order: a free run or an
    /// allocated block (with its owner). The linear memory map is a list of these.</summary>
    public sealed class Segment
    {
        public int Base { get; init; }
        public int Size { get; init; }
        public BuddyNodeKind Kind { get; init; }
        public string? OwnerPath { get; init; }
    }

    public static bool HasOs(Hardware hw)
    {
        return hw.GetOsMemorySize() != 0;
    }

    /// <summary>
    /// Flattens the buddy tree into address-ordered terminal segments (the free runs and
    /// allocated blocks), i.e. the leaves of the allocation structure. A left-to-right
    /// walk yields them in ascending base order, which is exactly the linear memory map.
    /// </summary>
    public static List<Segment> Flatten(BuddyNode? root)
    {
        List<Segment> segments = new List<Segment>();
        if (root != null)
        {
            FlattenInto(root, segments);
        }
        return segments;
    }

    private static void FlattenInto(BuddyNode node, List<Segment> segments)
    {
        if (node.Kind == BuddyNodeKind.Split)
        {
            foreach (BuddyNode child in node.Children)
            {
                FlattenInto(child, segments);
            }
            return;
        }
        segments.Add(new Segment
        {
            Base = node.Base,
            Size = node.Size,
            Kind = node.Kind,
            OwnerPath = node.OwnerPath
        });
    }

    public static int ProcessCount(Hardware hw)
    {
        return ReadWord(hw, OsLayout.ProcessCountOffset);
    }

    public static int CurrentIndex(Hardware hw)
    {
        return ReadWord(hw, OsLayout.CurrentIndexOffset);
    }

    /// <summary>
    /// Reads the active process-table slots, including the MLFQ priority/ticks fields.
    /// </summary>
    public static List<ProcessRow> ReadProcessTable(Hardware hw, Func<int, string?> nameForBase)
    {
        List<ProcessRow> rows = new List<ProcessRow>();
        int count = ProcessCount(hw);
        for (int i = 0; i < count; i++)
        {
            int entry = OsLayout.ProcessEntryAddress(i);
            int programAddress = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
            ProcessRow row = new ProcessRow
            {
                Index = i,
                Pid = ReadWord(hw, entry + Hardware.ProcessEntryPid),
                ParentPid = ReadWord(hw, entry + Hardware.ProcessEntryParentPid),
                ProgramAddress = programAddress,
                Path = nameForBase(programAddress),
                State = (ProcessState)ReadWord(hw, entry + Hardware.ProcessEntryState),
                Wait = (WaitReason)ReadWord(hw, entry + Hardware.ProcessEntryWaitReason),
                Priority = ReadWord(hw, entry + Hardware.ProcessEntryPriority),
                TicksUsed = ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed)
            };
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Returns the heap's contiguous free runs for the "free memory:" display.
    /// Derived from the reconstructed tree (not a leaf-only scan), so a block freed at
    /// an internal level — which leaves no free leaf bits set — is still reported, and
    /// address-adjacent free blocks are coalesced. Returns null when the heap is not
    /// configured yet.
    /// </summary>
    public static List<FreeBlock>? ReadFreeBlocks(Hardware hw)
    {
        BuddyNode? root = ReadTree(hw, _ => null);
        if (root == null)
        {
            return null;
        }

        List<FreeBlock> free = new List<FreeBlock>();
        CollectFree(root, free);
        free.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Coalesce address-adjacent free runs into single blocks.
        List<FreeBlock> coalesced = new List<FreeBlock>();
        foreach (FreeBlock block in free)
        {
            if (coalesced.Count > 0)
            {
                FreeBlock last = coalesced[coalesced.Count - 1];
                if (last.Start + last.Size == block.Start)
                {
                    coalesced[coalesced.Count - 1] = new FreeBlock { Start = last.Start, Size = last.Size + block.Size };
                    continue;
                }
            }
            coalesced.Add(block);
        }
        return coalesced;
    }

    private static void CollectFree(BuddyNode node, List<FreeBlock> free)
    {
        if (node.Kind == BuddyNodeKind.Free)
        {
            free.Add(new FreeBlock { Start = node.Base, Size = node.Size });
            return;
        }
        foreach (BuddyNode child in node.Children)
        {
            CollectFree(child, free);
        }
    }

    /// <summary>
    /// Reconstructs the buddy allocation tree from the bitmap (free subtrees) and the
    /// process table (allocated blocks, labeled by owner). Returns null when the heap
    /// is not configured yet.
    /// </summary>
    public static BuddyNode? ReadTree(Hardware hw, Func<int, string?> nameForBase)
    {
        if (!HasOs(hw))
        {
            return null;
        }
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int heapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int levels = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int minBlock = ReadWord(hw, OsLayout.BuddyMinBlockOffset);
        if (heapSize == 0 || levels == 0 || minBlock == 0)
        {
            return null;
        }

        Dictionary<(int Base, int Size), string?> allocated = BuildAllocatedBlocks(hw, heapStart, heapSize, minBlock, nameForBase);
        return BuildNode(hw, 1, 0, heapStart, heapSize, levels, allocated);
    }

    // Maps each allocated block (base, size) -> owner name, derived from the process
    // table. The block size is the smallest power-of-two >= the entry's requested
    // TotalSize (and >= minBlock), matching how BuddyAlloc rounds an allocation up to a
    // block. Keying by (base, size) — not base alone — disambiguates the root and the
    // leftmost node at every level, which all share the heap's start address.
    private static Dictionary<(int Base, int Size), string?> BuildAllocatedBlocks(Hardware hw,
        int heapStart, int heapSize, int minBlock, Func<int, string?> nameForBase)
    {
        Dictionary<(int, int), string?> blocks = new Dictionary<(int, int), string?>();
        int count = ProcessCount(hw);
        for (int i = 0; i < count; i++)
        {
            int entry = OsLayout.ProcessEntryAddress(i);
            ProcessState state = (ProcessState)ReadWord(hw, entry + Hardware.ProcessEntryState);
            if (state == ProcessState.Terminated)
            {
                continue;
            }
            int totalSize = ReadWord(hw, entry + Hardware.ProcessEntryTotalSize);
            int programAddress = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
            if (totalSize <= 0 || programAddress < heapStart || programAddress >= heapStart + heapSize)
            {
                continue;
            }
            int blockSize = RoundUpPowerOfTwo(Math.Max(totalSize, minBlock));
            if (blockSize > heapSize)
            {
                blockSize = heapSize;
            }
            blocks[(programAddress, blockSize)] = nameForBase(programAddress);
        }
        return blocks;
    }

    private static BuddyNode BuildNode(Hardware hw, int node, int level, int heapStart,
        int heapSize, int levels, Dictionary<(int Base, int Size), string?> allocated)
    {
        int size = heapSize >> level;
        int firstNodeAtLevel = 1 << level;
        int baseAddress = heapStart + (node - firstNodeAtLevel) * size;

        BuddyNode result = new BuddyNode
        {
            Level = level,
            Node = node,
            Base = baseAddress,
            Size = size
        };

        if (IsNodeFree(hw, node))
        {
            result.Kind = BuddyNodeKind.Free;
            return result;
        }

        // Bit clear: allocated whole-block here, or split into children. A process
        // entry pinning this exact (base, size) means it is allocated at this level;
        // a leaf with no free bit is allocated; otherwise it must be split.
        if (allocated.TryGetValue((baseAddress, size), out string? owner) || level == levels)
        {
            result.Kind = BuddyNodeKind.Allocated;
            result.OwnerPath = owner;
            return result;
        }

        result.Kind = BuddyNodeKind.Split;
        result.Children.Add(BuildNode(hw, node * 2, level + 1, heapStart, heapSize, levels, allocated));
        result.Children.Add(BuildNode(hw, node * 2 + 1, level + 1, heapStart, heapSize, levels, allocated));
        return result;
    }

    private static int RoundUpPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n)
        {
            p <<= 1;
        }
        return p;
    }

    // Reads bit (node-1) of the buddy bitmap; set means the node is FREE.
    private static bool IsNodeFree(Hardware hw, int node)
    {
        int bitPos = node - 1;
        int wordIndex = bitPos / 32;
        int bitInWord = bitPos % 32;
        int word = ReadWord(hw, OsLayout.BuddyBitmapOffset + wordIndex * 4);
        return ((word >> bitInWord) & 1) != 0;
    }

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }
}
