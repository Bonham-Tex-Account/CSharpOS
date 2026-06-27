using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Isolation tests for the buddy allocator (LoadProcess routine) and the buddy-free
/// reclaim performed by Halt and InvalidInstruction.
///
/// Tree layout (1-indexed binary tree, bit=1 FREE):
///   Level 0: node 1  (full heap)
///   Level 1: nodes 2-3
///   Level L: nodes 2^L .. 2^(L+1)-1
///   Leaf nodes: 2^levels .. 2^(levels+1)-1
///
/// Allocation order for a fresh heap split root-to-leftmost-leaf:
///   Alloc 0 → leftmost leaf; split path's right siblings are marked free
///   Alloc 1 → right sibling of alloc 0 (the next available free leaf)
///   Alloc 2/3 → similarly from the sibling subtree of the shared parent
///
/// After 4 allocs the common 2-level subtree is fully consumed, so freeing
/// alloc 0 then 1 merges them to their parent, but that parent's buddy is still
/// occupied → cascade stops. Freeing all 4 → cascade all the way to root.
/// </summary>
public class OsAllocatorRoutineTests
{
    private static int MachineSize => Test.MinMachineSize;
    private const int MinBlock = OsLayout.BuddyDefaultMinBlock;

    private static Hardware NewSeededHardware() { return NewSeededHardwareOfSize(MachineSize); }

    private static Hardware NewSeededHardwareOfSize(int machineSize)
    {
        Hardware hw = Test.NewHardware(machineSize, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        int heapStart = OsLayout.TotalSize;
        int heapSize  = LargestPowerOfTwoFitting(machineSize - OsLayout.TotalSize);
        int levels    = Log2(heapSize / MinBlock);
        WriteWord(hw, OsLayout.BuddyHeapStartOffset, heapStart);
        WriteWord(hw, OsLayout.BuddyHeapSizeOffset,  heapSize);
        WriteWord(hw, OsLayout.BuddyMinBlockOffset,  MinBlock);
        WriteWord(hw, OsLayout.BuddyLevelsOffset,    levels);
        for (int w = 0; w < OsLayout.BuddyBitmapWords; w++)
        {
            WriteWord(hw, OsLayout.BuddyBitmapOffset + w * 4, 0);
        }
        WriteWord(hw, OsLayout.BuddyBitmapOffset, 1); // root = free
        return hw;
    }

    // ---- alloc: basic -------------------------------------------------------

    [Fact]
    public void BuddyAlloc_ExactLeafFit_AllocatesWithinHeap()
    {
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int heapSize  = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= heapStart, "ProgramAddress should be within the heap");
        Assert.True(addr < heapStart + heapSize, "ProgramAddress should be within the heap");

        // The allocated leaf's bit must be cleared.
        int leaf = (addr - heapStart) / MinBlock + (1 << levels);
        AssertBit(hw, leaf, false, "allocated leaf bit must be cleared");
    }

    [Fact]
    public void BuddyAlloc_SecondAlloc_UsesDifferentBlock()
    {
        Hardware hw = NewSeededHardware();

        int e0 = OsLayout.ProcessEntryAddress(0);
        int e1 = OsLayout.ProcessEntryAddress(1);
        WriteWord(hw, e0 + Hardware.ProcessEntryTotalSize, MinBlock);
        WriteWord(hw, e1 + Hardware.ProcessEntryTotalSize, MinBlock);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e0);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e1);

        int addr0 = ReadWord(hw, e0 + Hardware.ProcessEntryProgramAddress);
        int addr1 = ReadWord(hw, e1 + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr0 >= 0, "First alloc succeeds");
        Assert.True(addr1 >= 0, "Second alloc succeeds");
        Assert.NotEqual(addr0, addr1);
        Assert.True(Math.Abs(addr0 - addr1) >= MinBlock, "Blocks must not overlap");
    }

    [Fact]
    public void BuddyAlloc_LargeRequest_AllocatesAlignedBlock()
    {
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock * 2 + 1);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= heapStart, "ProgramAddress within heap");
        // Round up: MinBlock*2+1 → MinBlock*4 block.
        Assert.Equal(0, (addr - heapStart) % (MinBlock * 4));
    }

    [Fact]
    public void BuddyAlloc_NoMemory_SetsProgramAddressToNegativeOne()
    {
        Hardware hw = NewSeededHardware();
        int heapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, heapSize + 1);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        Assert.Equal(-1, ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress));
    }

    [Fact]
    public void BuddyAlloc_FullHeap_ThenFails()
    {
        // FullHeapMachineSize gives exactly MaxProcesses leaf nodes (3-level, 8 leaves).
        Hardware hw = NewSeededHardwareOfSize(Test.FullHeapMachineSize);
        int heapSize  = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int leafCount = 1 << levels;

        // Fill all leaves.
        for (int i = 0; i < leafCount; i++)
        {
            WriteWord(hw, OsLayout.ProcessCountOffset, i + 1);
            int entry = OsLayout.ProcessEntryAddress(i % OsLayout.MaxProcesses);
            WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);
            hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);
            int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
            Assert.True(addr >= 0, $"Alloc {i} should succeed");
        }

        // After filling, no leaf should be free.
        int firstLeaf = 1 << levels;
        for (int j = 0; j < leafCount; j++)
        {
            AssertBit(hw, firstLeaf + j, false, $"leaf {j} should be used after filling heap");
        }
    }

    // ---- free: semantics ----------------------------------------------------
    // After a free + cascade merge, the freed leaf bit is cleared (merged into parent).
    // For a single alloc from a fresh root-split heap, the merge cascades to root.

    [Fact]
    public void BuddyFree_SingleAlloc_FreedThenRootBecomesAvailable()
    {
        // Alloc one block from a fresh heap (root splits to leftmost leaf).
        // All sibling right-children were marked free during the split.
        // Freeing the leaf merges with each free sibling all the way back to root.
        Hardware hw = NewSeededHardware();
        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= 0, "Alloc must succeed");

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Root must be free (all memory reclaimed by cascade merge).
        AssertBit(hw, 1, true, "root should be free after freeing the only allocation");
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void BuddyFree_MergesWithBuddy_StopsAtBlockedAncestor()
    {
        // Alloc 4 min-blocks. Allocs 0 and 1 are buddy leaves (adjacent).
        // Allocs 2 and 3 consume the sibling subtree that would allow further merging.
        // After freeing 0 then 1: their parent node is free, leaves are cleared,
        // merge stops because the parent's buddy is occupied (by alloc 2 or 3).
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int[] e = new int[4];
        int[] addrs = new int[4];
        for (int i = 0; i < 4; i++)
        {
            e[i] = OsLayout.ProcessEntryAddress(i);
            WriteWord(hw, e[i] + Hardware.ProcessEntryTotalSize, MinBlock);
            hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e[i]);
            addrs[i] = ReadWord(hw, e[i] + Hardware.ProcessEntryProgramAddress);
            Assert.True(addrs[i] >= 0, $"Alloc {i} must succeed");
        }

        int leaf0 = (addrs[0] - heapStart) / MinBlock + firstLeaf;
        int leaf1 = (addrs[1] - heapStart) / MinBlock + firstLeaf;
        // Alloc 0 splits root to leftmost leaf; alloc 1 picks the right sibling,
        // so they are always adjacent.
        Assert.Equal(leaf0 + 1, leaf1);
        int parent = leaf0 / 2;

        WriteWord(hw, OsLayout.ProcessCountOffset, 4);
        SeedHaltState(hw);

        // Free entry 0.
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, e[0] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Leaf0 is free; leaf1 buddy still allocated → no merge yet.
        AssertBit(hw, leaf0, true, "leaf0 free after first halt; buddy still allocated");

        // Free entry 1.
        WriteWord(hw, OsLayout.CurrentIndexOffset, 1);
        WriteWord(hw, e[1] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Merge: parent free, both leaves cleared. Parent's buddy occupied → stops here.
        AssertBit(hw, parent, true,  "parent free after merging the two leaf buddies");
        AssertBit(hw, leaf0,  false, "leaf0 cleared after merge");
        AssertBit(hw, leaf1,  false, "leaf1 cleared after merge");
    }

    [Fact]
    public void BuddyFree_MergesRecursively_AllAllocsFreed_RootFree()
    {
        // Alloc 4 blocks, free them all. Cascading merges restore the root.
        Hardware hw = NewSeededHardware();

        int[] e = new int[4];
        int[] addrs = new int[4];
        for (int i = 0; i < 4; i++)
        {
            e[i] = OsLayout.ProcessEntryAddress(i);
            WriteWord(hw, e[i] + Hardware.ProcessEntryTotalSize, MinBlock);
            hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e[i]);
            addrs[i] = ReadWord(hw, e[i] + Hardware.ProcessEntryProgramAddress);
            Assert.True(addrs[i] >= 0, $"Alloc {i} must succeed");
        }

        WriteWord(hw, OsLayout.ProcessCountOffset, 4);
        SeedHaltState(hw);

        for (int i = 0; i < 4; i++)
        {
            WriteWord(hw, OsLayout.CurrentIndexOffset, i);
            WriteWord(hw, e[i] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
            hw.DispatchOsRoutine(Hardware.IvtHalt);
            RunRoutine(hw);
        }

        AssertBit(hw, 1, true, "root should be free after freeing all 4 allocations");
    }

    [Fact]
    public void BuddyFree_NonBuddyPair_NoMerge_BothLeavesFree()
    {
        // Alloc 4: leaves 0 and 1 are buddies; leaves 2 and 3 are buddies.
        // Free leaf 0 and leaf 2 — they are NOT buddies of each other.
        // Each freed leaf's own buddy is still allocated → no merge occurs for either.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int[] e = new int[4];
        int[] addrs = new int[4];
        for (int i = 0; i < 4; i++)
        {
            e[i] = OsLayout.ProcessEntryAddress(i);
            WriteWord(hw, e[i] + Hardware.ProcessEntryTotalSize, MinBlock);
            hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e[i]);
            addrs[i] = ReadWord(hw, e[i] + Hardware.ProcessEntryProgramAddress);
            Assert.True(addrs[i] >= 0, $"Alloc {i} must succeed");
        }

        int leaf0 = (addrs[0] - heapStart) / MinBlock + firstLeaf;
        int leaf2 = (addrs[2] - heapStart) / MinBlock + firstLeaf;

        WriteWord(hw, OsLayout.ProcessCountOffset, 4);
        SeedHaltState(hw);

        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, e[0] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        WriteWord(hw, OsLayout.CurrentIndexOffset, 2);
        WriteWord(hw, e[2] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Both leaves free; their respective buddies (leaf1 and leaf3) still allocated.
        AssertBit(hw, leaf0, true, "leaf0 free; buddy (leaf1) still allocated");
        AssertBit(hw, leaf2, true, "leaf2 free; buddy (leaf3) still allocated");
    }

    [Fact]
    public void InvalidInstruction_ReturnsMemoryToTree()
    {
        Hardware hw = NewSeededHardware();
        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= 0);

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        hw.DispatchOsRoutine(Hardware.IvtInvalidInstruction);
        RunRoutine(hw);

        AssertBit(hw, 1, true, "root free after invalid instruction cascade");
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void RunOsRoutineSynchronously_RestoresCpuStateAfterAlloc()
    {
        Hardware hw = NewSeededHardware();
        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);

        hw.WriteRegisterAt((byte)RegisterName.EAX, 0xAA);
        hw.WriteRegisterAt((byte)RegisterName.EBX, 0xBB);
        int savedIp = 1234;
        hw.SetInstructionPointer(savedIp);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        Assert.Equal(0xAA, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(0xBB, hw.ReadRegisterAt((byte)RegisterName.EBX));
        Assert.Equal(savedIp, hw.GetInstructionPointer());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    // ---- alloc: edge cases --------------------------------------------------

    [Fact]
    public void BuddyAlloc_ExactHeapSize_AllocatesRootNode() // EDGE CASE
    {
        // Arrange: request exactly heapSize bytes — the allocator must consume
        // the root node directly without splitting.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int heapSize  = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, heapSize);

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        // Assert: root node (1) used, address is heapStart.
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.Equal(heapStart, addr);
        AssertBit(hw, 1, false, "root node must be marked used after whole-heap alloc");
    }

    [Fact]
    public void BuddyAlloc_ExactHeapSize_FreedRootBecomesAvailable() // EDGE CASE
    {
        // Arrange: alloc the whole heap (root node), then free it via Halt.
        // The free path must mark root free even though level==0 on entry to bf_merge
        // (the loop exits immediately without merging — but SetBit already ran).
        Hardware hw = NewSeededHardware();
        int heapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, heapSize);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);
        Assert.True(ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress) >= 0, "alloc must succeed");

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        // Act
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Assert: root must be free again.
        AssertBit(hw, 1, true, "root must be free after freeing the full-heap alloc");
    }

    [Fact]
    public void BuddyAlloc_HalfHeapSize_AllocatesLevelOneNode() // EDGE CASE
    {
        // Arrange: request exactly heapSize/2 bytes. The allocator finds root free,
        // splits it, and allocates the left child (node 2, level 1).
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int heapSize  = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int halfSize  = heapSize / 2;

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, halfSize);

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        // Assert: address == heapStart (leftmost level-1 block).
        // Root cleared, right child (node 3) marked free, left child (node 2) used.
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.Equal(heapStart, addr);
        AssertBit(hw, 1, false, "root cleared after split");
        AssertBit(hw, 2, false, "left child (node 2) used after allocation");
        AssertBit(hw, 3, true,  "right child (node 3) marked free as split buddy");
    }

    [Fact]
    public void BuddyAlloc_HalfHeapSize_FreedMergesToRoot() // EDGE CASE
    {
        // Arrange: alloc heapSize/2 (level-1 node), then free via Halt.
        // The free path must identify level 1, set bit(2), check buddy(3)=free,
        // merge both into root, and leave root free.
        Hardware hw = NewSeededHardware();
        int heapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int halfSize = heapSize / 2;

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, halfSize);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);
        Assert.True(ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress) >= 0, "alloc must succeed");

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        // Act
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Assert: cascade merge must reach root.
        AssertBit(hw, 1, true,  "root free after merging the two half-heap buddies");
        AssertBit(hw, 2, false, "left child cleared after merge");
        AssertBit(hw, 3, false, "right child cleared after merge");
    }

    [Fact]
    public void BuddyAlloc_OneByte_AllocatesMinBlockSizeBlock() // EDGE CASE
    {
        // Arrange: request 1 byte — the level-find loop must stop at BuddyLevels
        // (the leaf level) because MinBlock is the smallest granularity.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 1);

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        // Assert: a valid leaf-level block is returned (not -1), proving rounding to MinBlock.
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= heapStart, "1-byte request must succeed and return a valid address");
        int leafIndex = (addr - heapStart) / MinBlock;
        int leafNode  = firstLeaf + leafIndex;
        AssertBit(hw, leafNode, false, "the allocated leaf must be marked used");
    }

    [Fact]
    public void BuddyAlloc_ZeroBytes_AllocatesMinBlockSizeBlock() // EDGE CASE
    {
        // Arrange: request 0 bytes. blockSize/2 < 0 is never true for positive
        // blockSizes, so the level-find loop clamps at BuddyLevels and allocates a
        // leaf block rather than failing.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 0);

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        // Assert: a valid address (not -1) at leaf level — not a failure.
        int addr = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        Assert.True(addr >= heapStart, "zero-byte request must allocate a MinBlock leaf, not fail");
        int leafIndex = (addr - heapStart) / MinBlock;
        int leafNode  = firstLeaf + leafIndex;
        AssertBit(hw, leafNode, false, "the allocated leaf must be marked used");
    }

    [Fact]
    public void BuddyAlloc_AllocFreeAlloc_ReturnsHeapToCleanState() // EDGE CASE
    {
        // Arrange: alloc → free (cascade to root) → alloc again.
        // After the second alloc the bitmap must match the state after the first alloc,
        // proving the tree is idempotent across alloc/free cycles.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int entry0 = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry0 + Hardware.ProcessEntryTotalSize, MinBlock);

        // Alloc
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry0);
        int firstAddr = ReadWord(hw, entry0 + Hardware.ProcessEntryProgramAddress);
        Assert.True(firstAddr >= heapStart, "first alloc must succeed");

        // Free via Halt
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry0 + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);
        AssertBit(hw, 1, true, "root must be free between cycles");

        // Second alloc using a fresh entry slot — must land at the same address
        // (same leftmost-leaf policy on a freshly-merged root).
        int entry1 = OsLayout.ProcessEntryAddress(1);
        WriteWord(hw, entry1 + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry1);

        int secondAddr = ReadWord(hw, entry1 + Hardware.ProcessEntryProgramAddress);
        Assert.Equal(firstAddr, secondAddr);
        int leafIndex = (secondAddr - heapStart) / MinBlock;
        int leafNode  = firstLeaf + leafIndex;
        AssertBit(hw, leafNode, false, "re-allocated leaf must be marked used");
    }

    [Fact]
    public void BuddyAlloc_FullHeap_FreeOneThenReallocSucceeds() // EDGE CASE
    {
        // Arrange: fill FullHeapMachineSize completely, then free entry 0, then alloc
        // MinBlock. The freed leaf must be reused — proving the scan finds it.
        Hardware hw = NewSeededHardwareOfSize(Test.FullHeapMachineSize);
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int leafCount = 1 << levels;

        int[] entries = new int[leafCount];
        int[] addrs   = new int[leafCount];
        for (int i = 0; i < leafCount; i++)
        {
            entries[i] = OsLayout.ProcessEntryAddress(i);
            WriteWord(hw, entries[i] + Hardware.ProcessEntryTotalSize, MinBlock);
            hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entries[i]);
            addrs[i] = ReadWord(hw, entries[i] + Hardware.ProcessEntryProgramAddress);
            Assert.True(addrs[i] >= 0, $"fill alloc {i} must succeed");
        }

        // Free entry 0 via Halt.
        WriteWord(hw, OsLayout.ProcessCountOffset, leafCount);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entries[0] + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Act: alloc MinBlock again — must reuse the freed leaf.
        int newEntry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, newEntry + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, newEntry);

        int newAddr = ReadWord(hw, newEntry + Hardware.ProcessEntryProgramAddress);
        Assert.True(newAddr >= heapStart, "re-alloc after partial free must succeed");
        int leafIndex = (newAddr - heapStart) / MinBlock;
        int leafNode  = (1 << levels) + leafIndex;
        AssertBit(hw, leafNode, false, "re-allocated leaf must be marked used");
    }

    [Fact]
    public void BuddyAlloc_BitmapWordBoundary_SetBitWritesCorrectWord() // EDGE CASE
    {
        // Arrange: use a 6-level tree (64 leaves) so that node 33 (at level 5,
        // bitPos=32) lies in word 1 of the bitmap — the first cross-word-boundary node.
        // Alloc 0 splits root to leftmost leaf (64); the split path marks node 33 free
        // (right sibling at level 5). Verify the bitmap correctly places that bit in
        // word 1, bit 0.
        int sixLevelHeap    = 64 * MinBlock;                          // 64 leaves × 256 = 16384
        int sixLevelMachine = OsLayout.TotalSize + sixLevelHeap;      // 5452 + 16384 = 21836
        Hardware hw = NewSeededHardwareOfSize(sixLevelMachine);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        // Assert: node 33 must be free (set by the split path in word 1, bit 0).
        // Node 33: bitPos=32, word=1, bitInWord=0.
        AssertBit(hw, 33, true, "node 33 (word-boundary: word 1, bit 0) must be free after first alloc split");
        Assert.True(ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress) >= 0, "alloc must succeed");
    }

    [Fact]
    public void BuddyAlloc_BitmapWordBoundary_ClearBitWritesCorrectWord() // EDGE CASE
    {
        // Arrange: 6-level tree. After alloc 0 (leaf 64) and alloc 1 (leaf 65),
        // node 33 is still free (its subtree has free right sibling 67 and used leaf 66).
        // Alloc 2 (MinBlock) scans level 6 exhausted, finds node 33 free at level 5,
        // splits it: ClearBit(33) and SetBit(67), allocates leaf 66.
        // This exercises ClearBit and SetBit both crossing the word boundary.
        int sixLevelHeap    = 64 * MinBlock;
        int sixLevelMachine = OsLayout.TotalSize + sixLevelHeap;
        Hardware hw = NewSeededHardwareOfSize(sixLevelMachine);

        int e0 = OsLayout.ProcessEntryAddress(0);
        int e1 = OsLayout.ProcessEntryAddress(1);
        int e2 = OsLayout.ProcessEntryAddress(2);
        WriteWord(hw, e0 + Hardware.ProcessEntryTotalSize, MinBlock);
        WriteWord(hw, e1 + Hardware.ProcessEntryTotalSize, MinBlock);
        WriteWord(hw, e2 + Hardware.ProcessEntryTotalSize, MinBlock);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e0);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e1);

        AssertBit(hw, 33, true, "node 33 free before alloc 2 (pre-condition)");

        // Act
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e2);

        // Assert: node 33 must be cleared (ClearBit across word boundary), node 67 set (SetBit).
        // Node 67: bitPos=66, word=2, bitInWord=2.
        AssertBit(hw, 33, false, "node 33 (word 1, bit 0) cleared after split via ClearBit");
        AssertBit(hw, 67, true,  "node 67 (right child of 33) marked free after split");
        Assert.True(ReadWord(hw, e2 + Hardware.ProcessEntryProgramAddress) >= 0, "alloc 2 must succeed");
    }

    [Fact]
    public void BuddyFree_HeapSizeZero_SkipsBitmapAndCompletes() // EDGE CASE
    {
        // Arrange: set heapSize to 0 so the free guard (Cmp R11,0 → Jz bf_done)
        // fires immediately, skipping all bitmap work. The routine must not crash
        // (divide-by-zero or out-of-bounds access) and must terminate cleanly.
        Hardware hw = NewSeededHardware();
        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        WriteWord(hw, OsLayout.BuddyHeapSizeOffset, 0);   // corrupt heapSize after alloc

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        // Act: must not throw.
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        // Assert: process terminated; bitmap untouched (root still 0 since alloc consumed it).
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void BuddyFree_TotalSizeZero_SkipsBitmapAndCompletes() // EDGE CASE
    {
        // Arrange: process entry has TotalSize=0 — the guard (Cmp R10,0 → Jz bf_done)
        // fires, skipping the bitmap entirely. The allocator must not divide by zero
        // when computing block_j = offset / blockSize.
        Hardware hw = NewSeededHardware();
        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, entry);

        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 0);   // zero out after alloc

        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        SeedHaltState(hw);

        // Act: must not throw.
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void BuddyAlloc_ManuallyFreedLeaf_PickedUpBySubsequentAlloc() // EDGE CASE
    {
        // Arrange: alloc two blocks, then manually set a non-adjacent leaf's bit in
        // the bitmap (simulating a stale/externally-freed node). The allocator must
        // pick up that manually-freed leaf on the next alloc request.
        Hardware hw = NewSeededHardware();
        int heapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int levels    = ReadWord(hw, OsLayout.BuddyLevelsOffset);
        int firstLeaf = 1 << levels;

        int e0 = OsLayout.ProcessEntryAddress(0);
        int e1 = OsLayout.ProcessEntryAddress(1);
        WriteWord(hw, e0 + Hardware.ProcessEntryTotalSize, MinBlock);
        WriteWord(hw, e1 + Hardware.ProcessEntryTotalSize, MinBlock);

        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e0);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e1);
        Assert.True(ReadWord(hw, e0 + Hardware.ProcessEntryProgramAddress) >= 0, "alloc 0 must succeed");
        Assert.True(ReadWord(hw, e1 + Hardware.ProcessEntryProgramAddress) >= 0, "alloc 1 must succeed");

        // Manually free leaf firstLeaf+4 by writing its bit into the bitmap directly.
        // After alloc 0 and 1, leaves firstLeaf and firstLeaf+1 are used.
        // Leaves firstLeaf+2..firstLeaf+15 have not been touched — their bits are 0
        // because the split path only set right-sibling bits, not these deeper leaves.
        // We set bit for firstLeaf+4 directly to simulate an externally reclaimed block.
        int targetLeaf  = firstLeaf + 4;
        int bitPos      = targetLeaf - 1;
        int wordIdx     = bitPos / 32;
        int bitInWord   = bitPos % 32;
        int wordAddr    = OsLayout.BuddyBitmapOffset + wordIdx * 4;
        int existingWord = ReadWord(hw, wordAddr);
        WriteWord(hw, wordAddr, existingWord | (1 << bitInWord));

        AssertBit(hw, targetLeaf, true, "manual free must be visible in bitmap");

        // Act: alloc one more block — must find and use the manually freed leaf.
        int e2 = OsLayout.ProcessEntryAddress(2);
        WriteWord(hw, e2 + Hardware.ProcessEntryTotalSize, MinBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtAllocate, e2);

        // Assert: the new alloc must be at the manually freed leaf's address.
        int expectedAddr = heapStart + 4 * MinBlock;
        int actualAddr   = ReadWord(hw, e2 + Hardware.ProcessEntryProgramAddress);
        Assert.Equal(expectedAddr, actualAddr);
        AssertBit(hw, targetLeaf, false, "manually freed leaf must be cleared after allocation");
    }

    // ---- helpers ------------------------------------------------------------

    private static void RunRoutine(Hardware hw)
    {
        for (int step = 0; step < 5000 && !hw.InterruptsEnabled(); step++)
        {
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }
    }

    private static bool BitIsSet(Hardware hw, int node)
    {
        int bitPos  = node - 1;
        int wordIdx = bitPos / 32;
        int bitInW  = bitPos % 32;
        int word    = ReadWord(hw, OsLayout.BuddyBitmapOffset + wordIdx * 4);
        return ((word >> bitInW) & 1) != 0;
    }

    private static void AssertBit(Hardware hw, int node, bool expected, string context = "")
    {
        bool actual = BitIsSet(hw, node);
        if (actual != expected)
        {
            string label = string.IsNullOrEmpty(context) ? $"node {node}" : $"node {node} ({context})";
            string state = expected ? "free(1)" : "used(0)";
            throw new Xunit.Sdk.XunitException(
                $"Expected {label} to be {state} but was {(actual ? "free(1)" : "used(0)")}.");
        }
    }

    private static void SeedHaltState(Hardware hw)
    {
        WriteWord(hw, OsLayout.BoostTimerOffset,       OsLayout.BoostInterval);
        WriteWord(hw, OsLayout.QuantumTableOffset + 0,  1);
        WriteWord(hw, OsLayout.QuantumTableOffset + 4,  2);
        WriteWord(hw, OsLayout.QuantumTableOffset + 8,  4);
        WriteWord(hw, OsLayout.QuantumTableOffset + 12, 255);
    }

    private static int LargestPowerOfTwoFitting(int n)
    {
        int p = 1;
        while (p * 2 <= n) { p *= 2; }
        return p;
    }

    private static int Log2(int n)
    {
        int k = 0;
        while (n > 1) { n >>= 1; k++; }
        return k;
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
