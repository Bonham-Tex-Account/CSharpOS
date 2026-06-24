using CSharpOS;
using CSharpOSConsole.Visualization;

namespace OSTests;

/// <summary>
/// Deterministic tests for BuddyHeapView's reconstruction of the buddy tree and the
/// free-block list from hand-seeded kernel state (bitmap + heap params + process
/// table). No allocator routine is run and no Spectre dependency is involved — the
/// reader is exercised directly against known memory.
///
/// Scenario uses a 2-level tree (4 leaves): heapSize = 4 * minBlock.
///   node 1 (L0, whole heap)  ->  nodes 2,3 (L1, half)  ->  nodes 4,5,6,7 (L2, leaf)
/// Bitmap bit = node-1; bit set means that whole subtree is FREE.
/// </summary>
public class BuddyHeapViewTests
{
    private const int MinBlock = OsLayout.BuddyDefaultMinBlock; // 256
    private const int Levels = 2;
    private const int HeapSize = MinBlock * 4;                  // 1024 → 4 leaves
    private static readonly int HeapStart = OsLayout.TotalSize;

    private static Hardware NewHeapHardware()
    {
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        WriteWord(hw, OsLayout.BuddyHeapStartOffset, HeapStart);
        WriteWord(hw, OsLayout.BuddyHeapSizeOffset, HeapSize);
        WriteWord(hw, OsLayout.BuddyMinBlockOffset, MinBlock);
        WriteWord(hw, OsLayout.BuddyLevelsOffset, Levels);
        for (int w = 0; w < OsLayout.BuddyBitmapWords; w++)
        {
            WriteWord(hw, OsLayout.BuddyBitmapOffset + w * 4, 0);
        }
        return hw;
    }

    // Sets the free bit for a 1-indexed tree node.
    private static void SetNodeFree(Hardware hw, int node)
    {
        int bitPos = node - 1;
        int wordIndex = bitPos / 32;
        int bitInWord = bitPos % 32;
        int addr = OsLayout.BuddyBitmapOffset + wordIndex * 4;
        int word = ReadWord(hw, addr);
        word |= 1 << bitInWord;
        WriteWord(hw, addr, word);
    }

    // Adds a process-table entry that owns an allocated block at the given base.
    private static void SeedProcess(Hardware hw, int index, int programAddress, int totalSize)
    {
        WriteWord(hw, OsLayout.ProcessCountOffset, index + 1);
        int entry = OsLayout.ProcessEntryAddress(index);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, programAddress);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, totalSize);
    }

    [Fact]
    public void FreshHeap_RootFree_IsOneFreeBlockCoveringWholeHeap()
    {
        Hardware hw = NewHeapHardware();
        SetNodeFree(hw, 1); // only the root is free

        BuddyHeapView.BuddyNode? root = BuddyHeapView.ReadTree(hw, _ => null);
        Assert.NotNull(root);
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Free, root!.Kind);
        Assert.Equal(HeapStart, root.Base);
        Assert.Equal(HeapSize, root.Size);
        Assert.Empty(root.Children);

        // Free-block list reports the whole heap (the old leaf-walk reported nothing here).
        List<BuddyHeapView.FreeBlock>? free = BuddyHeapView.ReadFreeBlocks(hw);
        Assert.NotNull(free);
        Assert.Single(free!);
        Assert.Equal(HeapStart, free![0].Start);
        Assert.Equal(HeapSize, free[0].Size);
    }

    [Fact]
    public void LeftmostLeafAllocated_ProducesSplitTreeWithLabeledLeafAndFreeBuddies()
    {
        // State after allocating the leftmost leaf (node 4):
        //   node1 split, node2 split, node4 allocated, node5 free (buddy), node3 free (buddy)
        Hardware hw = NewHeapHardware();
        SetNodeFree(hw, 3); // right child of root, free at level 1 (size 512)
        SetNodeFree(hw, 5); // right child of node 2, free at leaf level (size 256)
        SeedProcess(hw, 0, HeapStart, MinBlock); // owns node 4 (leftmost leaf)

        BuddyHeapView.BuddyNode? root = BuddyHeapView.ReadTree(hw, addr => "proc.bin");
        Assert.NotNull(root);
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Split, root!.Kind);

        BuddyHeapView.BuddyNode left = root.Children[0];  // node 2 (split)
        BuddyHeapView.BuddyNode right = root.Children[1]; // node 3 (free, 512)
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Split, left.Kind);
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Free, right.Kind);
        Assert.Equal(HeapSize / 2, right.Size);

        BuddyHeapView.BuddyNode leaf0 = left.Children[0]; // node 4 (allocated)
        BuddyHeapView.BuddyNode leaf1 = left.Children[1]; // node 5 (free)
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Allocated, leaf0.Kind);
        Assert.Equal("proc.bin", leaf0.OwnerPath);
        Assert.Equal(HeapStart, leaf0.Base);
        Assert.Equal(MinBlock, leaf0.Size);
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Free, leaf1.Kind);
        Assert.Equal(HeapStart + MinBlock, leaf1.Base);
    }

    [Fact]
    public void FreeBlocks_CoalesceAddressAdjacentFreeNodesOfDifferentSizes()
    {
        // node5 free (leaf, 256 @ heapStart+256) is adjacent to node3 free (512 @ heapStart+512):
        // they must coalesce into a single [heapStart+256 + 768] run.
        Hardware hw = NewHeapHardware();
        SetNodeFree(hw, 3);
        SetNodeFree(hw, 5);
        SeedProcess(hw, 0, HeapStart, MinBlock); // node 4 allocated

        List<BuddyHeapView.FreeBlock>? free = BuddyHeapView.ReadFreeBlocks(hw);
        Assert.NotNull(free);
        Assert.Single(free!);
        Assert.Equal(HeapStart + MinBlock, free![0].Start);
        Assert.Equal(MinBlock + HeapSize / 2, free[0].Size); // 256 + 512 = 768
    }

    [Fact]
    public void Flatten_YieldsAddressOrderedSegmentsAcrossWholeHeap()
    {
        Hardware hw = NewHeapHardware();
        SetNodeFree(hw, 3); // free 512 @ heapStart+512
        SetNodeFree(hw, 5); // free 256 @ heapStart+256
        SeedProcess(hw, 0, HeapStart, MinBlock); // alloc 256 @ heapStart

        BuddyHeapView.BuddyNode? root = BuddyHeapView.ReadTree(hw, addr => "proc.bin");
        List<BuddyHeapView.Segment> segments = BuddyHeapView.Flatten(root);

        Assert.Equal(3, segments.Count);
        Assert.Equal(BuddyHeapView.BuddyNodeKind.Allocated, segments[0].Kind);
        Assert.Equal(HeapStart, segments[0].Base);
        Assert.Equal(MinBlock, segments[0].Size);

        Assert.Equal(BuddyHeapView.BuddyNodeKind.Free, segments[1].Kind);
        Assert.Equal(HeapStart + MinBlock, segments[1].Base);

        Assert.Equal(BuddyHeapView.BuddyNodeKind.Free, segments[2].Kind);
        Assert.Equal(HeapStart + HeapSize / 2, segments[2].Base);
        Assert.Equal(HeapSize / 2, segments[2].Size);

        // Segments cover the whole heap with no gaps.
        int covered = 0;
        foreach (BuddyHeapView.Segment s in segments)
        {
            covered += s.Size;
        }
        Assert.Equal(HeapSize, covered);
    }

    [Fact]
    public void NoOsRegion_ReturnsNull()
    {
        Hardware hw = Test.NewHardware(512, new FakeOS()); // no OS reserved
        Assert.Null(BuddyHeapView.ReadTree(hw, _ => null));
        Assert.Null(BuddyHeapView.ReadFreeBlocks(hw));
    }

    private static int ReadWord(Hardware hw, int address)
    {
        return Test.ReadWord(hw, address);
    }

    private static void WriteWord(Hardware hw, int address, int value)
    {
        Test.WriteWord(hw, address, value);
    }
}
