using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the ISA filesystem write-back cache (OsRoutines cache_* subroutines,
/// driven through the IvtCacheOp control interface). Covers miss-load, hit-without-reload,
/// LRU eviction, dirty write-back on eviction, discard (drop without write-back), pin/unpin,
/// write-through, whole-cache flush, the periodic ContextSwitch flush, and the all-pinned
/// eviction-failure edge. Results and cache state are read from memory / the disk, because
/// RunOsRoutineSynchronously restores registers around the dispatch.
/// </summary>
public class CacheManagerTests
{
    private const int BlockSize = Hardware.DefaultFileBlockSize;

    private static Hardware NewCacheHardware()
    {
        Hardware hw = Test.NewHardware(Test.MinMachineSize, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

    // Dispatches one cache op (block in EBX, op in EAX) and returns the parked result.
    private static int CacheOp(Hardware hw, int op, int block)
    {
        hw.WriteRegister(RegisterName.EBX, block);
        hw.RunOsRoutineSynchronously(Hardware.IvtCacheOp, op);
        return Test.ReadWord(hw, OsLayout.CacheResultOffset);
    }

    // A distinct 256-byte block payload per seed: byte i = seed + i.
    private static byte[] Block(int seed)
    {
        byte[] data = new byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            data[i] = (byte)(seed + i);
        }
        return data;
    }

    private static int SlotField(Hardware hw, int slot, int field)
    {
        return Test.ReadWord(hw, OsLayout.CacheSlotAddress(slot) + field);
    }

    private static int SlotHolding(Hardware hw, int block)
    {
        for (int i = 0; i < OsLayout.CacheSlotCount; i++)
        {
            if (SlotField(hw, i, OsLayout.CacheValidField) == 1 &&
                SlotField(hw, i, OsLayout.CacheBlockField) == block)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool Resident(Hardware hw, int block)
    {
        return SlotHolding(hw, block) >= 0;
    }

    private static byte[] ReadRam(Hardware hw, int address, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = hw.ReadBytes(address + i)[0];
        }
        return result;
    }

    // ---- miss / hit ------------------------------------------------------

    [Fact]
    public void Get_Miss_LoadsBlockIntoASlotAndReturnsItsDataAddress()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(5, Block(10));

        int dataAddr = CacheOp(hw, Hardware.CacheOpGet, 5);

        int slot = SlotHolding(hw, 5);
        Assert.True(slot >= 0, "block 5 should be resident after a get");
        Assert.Equal(OsLayout.CacheSlotAddress(slot) + OsLayout.CacheDataField, dataAddr);
        Assert.Equal(Block(10), ReadRam(hw, dataAddr, BlockSize));
        Assert.Equal(0, SlotField(hw, slot, OsLayout.CacheDirtyField));
    }

    [Fact]
    public void Get_Hit_ReturnsSameSlotWithoutReloadingFromDisk()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(5, Block(10));

        int firstAddr = CacheOp(hw, Hardware.CacheOpGet, 5);
        int firstSlot = SlotHolding(hw, 5);

        // Change the on-disk block; a hit must NOT observe the new content.
        hw.Disk.WriteFileBlock(5, Block(200));
        int secondAddr = CacheOp(hw, Hardware.CacheOpGet, 5);

        Assert.Equal(firstAddr, secondAddr);
        Assert.Equal(firstSlot, SlotHolding(hw, 5));
        Assert.Equal(Block(10), ReadRam(hw, secondAddr, BlockSize)); // still the cached copy
    }

    // ---- eviction --------------------------------------------------------

    [Fact]
    public void Get_FillsEverySlotThenEvictsTheLeastRecentlyUsed()
    {
        Hardware hw = NewCacheHardware();
        for (int b = 0; b < OsLayout.CacheSlotCount; b++)
        {
            hw.Disk.WriteFileBlock(b, Block(b));
            CacheOp(hw, Hardware.CacheOpGet, b);
        }
        // All slots full; block 0 was accessed first (lowest stamp).
        Assert.True(Resident(hw, 0));

        int victimSlot = SlotHolding(hw, 0);
        hw.Disk.WriteFileBlock(99, Block(99));
        int newAddr = CacheOp(hw, Hardware.CacheOpGet, 99);

        Assert.False(Resident(hw, 0), "the LRU block (0) should have been evicted");
        Assert.True(Resident(hw, 99));
        Assert.Equal(victimSlot, SlotHolding(hw, 99)); // reused the LRU slot
        Assert.Equal(Block(99), ReadRam(hw, newAddr, BlockSize));
    }

    [Fact]
    public void Eviction_OfADirtyBlock_WritesItBackToDisk()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(3, Block(30));
        int addr = CacheOp(hw, Hardware.CacheOpGet, 3);

        // Simulate a write: modify the cached copy and mark it dirty.
        hw.WriteBytes(addr, new byte[] { 0xAA, 0xBB, 0xCC });
        CacheOp(hw, Hardware.CacheOpDirty, 3);

        // Force block 3 out by touching a full set of other blocks (3 is LRU after this).
        for (int b = 10; b < 10 + OsLayout.CacheSlotCount; b++)
        {
            hw.Disk.WriteFileBlock(b, Block(b));
            CacheOp(hw, Hardware.CacheOpGet, b);
        }

        Assert.False(Resident(hw, 3), "block 3 should have been evicted");
        byte[] onDisk = hw.Disk.ReadFileBlock(3);
        Assert.Equal(0xAA, onDisk[0]);
        Assert.Equal(0xBB, onDisk[1]);
        Assert.Equal(0xCC, onDisk[2]);
    }

    [Fact]
    public void Discard_DropsBlockWithoutWritingBack()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(3, Block(30));
        int addr = CacheOp(hw, Hardware.CacheOpGet, 3);

        hw.WriteBytes(addr, new byte[] { 0xAA, 0xBB, 0xCC });
        CacheOp(hw, Hardware.CacheOpDirty, 3);
        CacheOp(hw, Hardware.CacheOpDiscard, 3);

        Assert.False(Resident(hw, 3), "discard should invalidate the slot");
        // The dirty modification must NOT have reached the disk.
        Assert.Equal(Block(30), hw.Disk.ReadFileBlock(3));

        // Re-getting reloads the original disk content, not the discarded modification.
        int reAddr = CacheOp(hw, Hardware.CacheOpGet, 3);
        Assert.Equal(Block(30), ReadRam(hw, reAddr, BlockSize));
    }

    [Fact]
    public void Discard_OfNonResidentBlock_IsANoOp()
    {
        Hardware hw = NewCacheHardware();
        int result = CacheOp(hw, Hardware.CacheOpDiscard, 42); // never loaded
        Assert.Equal(0, result);
        Assert.False(Resident(hw, 42));
    }

    // ---- pin / unpin -----------------------------------------------------

    [Fact]
    public void Pin_PreventsEvictionWhileOtherBlocksCycleThrough()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(0, Block(0));
        CacheOp(hw, Hardware.CacheOpGet, 0);
        CacheOp(hw, Hardware.CacheOpPin, 0);
        Assert.Equal(1, SlotField(hw, SlotHolding(hw, 0), OsLayout.CachePinField));

        // Load many more distinct blocks, forcing repeated eviction among the other slots.
        for (int b = 1; b <= OsLayout.CacheSlotCount + 6; b++)
        {
            hw.Disk.WriteFileBlock(b, Block(b));
            CacheOp(hw, Hardware.CacheOpGet, b);
        }

        Assert.True(Resident(hw, 0), "a pinned block must never be evicted");
    }

    [Fact]
    public void Unpin_AllowsThePreviouslyPinnedBlockToBeEvictedAgain()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(0, Block(0));
        CacheOp(hw, Hardware.CacheOpGet, 0);
        CacheOp(hw, Hardware.CacheOpPin, 0);
        CacheOp(hw, Hardware.CacheOpUnpin, 0);
        Assert.Equal(0, SlotField(hw, SlotHolding(hw, 0), OsLayout.CachePinField));

        // Block 0 is now the LRU unpinned slot; the next full cycle should reclaim it.
        for (int b = 1; b <= OsLayout.CacheSlotCount; b++)
        {
            hw.Disk.WriteFileBlock(b, Block(b));
            CacheOp(hw, Hardware.CacheOpGet, b);
        }

        Assert.False(Resident(hw, 0), "after unpin the block is evictable again");
    }

    [Fact]
    public void AllSlotsPinned_GetOfANewBlockFailsGracefully()
    {
        Hardware hw = NewCacheHardware();
        for (int b = 0; b < OsLayout.CacheSlotCount; b++)
        {
            hw.Disk.WriteFileBlock(b, Block(b));
            CacheOp(hw, Hardware.CacheOpGet, b);
            CacheOp(hw, Hardware.CacheOpPin, b);
        }

        hw.Disk.WriteFileBlock(99, Block(99));
        int result = CacheOp(hw, Hardware.CacheOpGet, 99);

        Assert.Equal(-1, result); // no evictable slot
        Assert.False(Resident(hw, 99));
    }

    // ---- write-through / flush -------------------------------------------

    [Fact]
    public void WriteThrough_FlushesImmediatelyAndClearsDirty()
    {
        Hardware hw = NewCacheHardware();
        hw.Disk.WriteFileBlock(7, Block(70));
        int addr = CacheOp(hw, Hardware.CacheOpGet, 7);

        hw.WriteBytes(addr, new byte[] { 0x11, 0x22 });
        CacheOp(hw, Hardware.CacheOpWriteThrough, 7);

        byte[] onDisk = hw.Disk.ReadFileBlock(7);
        Assert.Equal(0x11, onDisk[0]);
        Assert.Equal(0x22, onDisk[1]);
        Assert.Equal(0, SlotField(hw, SlotHolding(hw, 7), OsLayout.CacheDirtyField));
    }

    [Fact]
    public void Flush_WritesBackEveryDirtyUnpinnedSlotAndLeavesCleanOnesAlone()
    {
        Hardware hw = NewCacheHardware();
        int[] blocks = { 1, 2, 3 };
        int[] addrs = new int[blocks.Length];
        for (int i = 0; i < blocks.Length; i++)
        {
            hw.Disk.WriteFileBlock(blocks[i], Block(blocks[i]));
            addrs[i] = CacheOp(hw, Hardware.CacheOpGet, blocks[i]);
        }

        // Dirty 1 and 3; leave 2 clean.
        hw.WriteBytes(addrs[0], new byte[] { 0x91 });
        CacheOp(hw, Hardware.CacheOpDirty, 1);
        hw.WriteBytes(addrs[2], new byte[] { 0x93 });
        CacheOp(hw, Hardware.CacheOpDirty, 3);

        CacheOp(hw, Hardware.CacheOpFlush, 0);

        Assert.Equal(0x91, hw.Disk.ReadFileBlock(1)[0]);
        Assert.Equal(Block(2), hw.Disk.ReadFileBlock(2)); // untouched (was clean)
        Assert.Equal(0x93, hw.Disk.ReadFileBlock(3)[0]);
        Assert.Equal(0, SlotField(hw, SlotHolding(hw, 1), OsLayout.CacheDirtyField));
        Assert.Equal(0, SlotField(hw, SlotHolding(hw, 3), OsLayout.CacheDirtyField));
    }

    // ---- periodic flush via ContextSwitch --------------------------------

    [Fact]
    public void ContextSwitch_PeriodicFlush_WritesBackDirtyBlocksWhenTimerExpires()
    {
        Hardware hw = NewCacheHardware();
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, -1);        // idle: ContextSwitch → cs_skip
        Test.WriteWord(hw, OsLayout.CacheFlushTimerOffset, 2);      // flush on the 2nd tick

        hw.Disk.WriteFileBlock(5, Block(50));
        int addr = CacheOp(hw, Hardware.CacheOpGet, 5);
        hw.WriteBytes(addr, new byte[] { 0x77 });
        CacheOp(hw, Hardware.CacheOpDirty, 5);

        hw.RunOsRoutineSynchronously(Hardware.IvtContextSwitch, 0); // timer 2 → 1, no flush
        Assert.Equal(Block(50), hw.Disk.ReadFileBlock(5));
        Assert.Equal(1, Test.ReadWord(hw, OsLayout.CacheFlushTimerOffset));

        hw.RunOsRoutineSynchronously(Hardware.IvtContextSwitch, 0); // timer 1 → 0 → flush
        Assert.Equal(0x77, hw.Disk.ReadFileBlock(5)[0]);
        Assert.Equal(OsLayout.CacheFlushInterval, Test.ReadWord(hw, OsLayout.CacheFlushTimerOffset));
    }
}
