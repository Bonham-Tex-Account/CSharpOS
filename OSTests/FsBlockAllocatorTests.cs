using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the ISA filesystem block layer (OsRoutines fs_* subroutines via the
/// IvtFsOp interface): format, bitmap allocation order, reserved blocks, free + reuse, the
/// full-disk edge, free-chaining links, and the on-disk effect after a cache flush.
/// </summary>
public class FsBlockAllocatorTests
{
    private static Hardware NewFsHardware()
    {
        Hardware hw = Test.NewHardware(Test.MinMachineSize, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        // Format the fresh disk before every scenario.
        FsOp(hw, Hardware.FsOpFormat, 0, 0);
        return hw;
    }

    // Dispatches one filesystem op (arg1 in EBX, arg2 in ECX, op in EAX); returns FsResult.
    private static int FsOp(Hardware hw, int op, int arg1, int arg2)
    {
        hw.WriteRegister(RegisterName.EBX, arg1);
        hw.WriteRegister(RegisterName.ECX, arg2);
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, op);
        return Test.ReadWord(hw, OsLayout.FsResultOffset);
    }

    private static int Alloc(Hardware hw)
    {
        return FsOp(hw, Hardware.FsOpAllocBlock, 0, 0);
    }

    private static void Free(Hardware hw, int block)
    {
        FsOp(hw, Hardware.FsOpFreeBlock, block, 0);
    }

    private static int Next(Hardware hw, int block)
    {
        return FsOp(hw, Hardware.FsOpChainNext, block, 0);
    }

    private static void SetNext(Hardware hw, int block, int next)
    {
        FsOp(hw, Hardware.FsOpChainSetNext, block, next);
    }

    private static void Flush(Hardware hw)
    {
        hw.WriteRegister(RegisterName.EBX, 0);
        hw.RunOsRoutineSynchronously(Hardware.IvtCacheOp, Hardware.CacheOpFlush);
    }

    private static int DiskWord(Hardware hw, int block, int offset)
    {
        byte[] data = hw.Disk.ReadFileBlock(block);
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    // ---- allocation ------------------------------------------------------

    [Fact]
    public void Alloc_FirstBlock_IsTheFirstDataBlock()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(FsLayout.FirstDataBlock, Alloc(hw)); // blocks 0,1 reserved → first free is 2
    }

    [Fact]
    public void Alloc_HandsOutConsecutiveBlocksInBitmapOrder()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(2, Alloc(hw));
        Assert.Equal(3, Alloc(hw));
        Assert.Equal(4, Alloc(hw));
    }

    [Fact]
    public void Alloc_NewBlockStartsAsAnEndOfChain()
    {
        Hardware hw = NewFsHardware();
        int block = Alloc(hw);
        Assert.Equal(FsLayout.EndOfChain, Next(hw, block));
    }

    [Fact]
    public void Free_ThenAlloc_ReusesTheFreedBlock()
    {
        Hardware hw = NewFsHardware();
        Alloc(hw);              // 2
        int b3 = Alloc(hw);     // 3
        Alloc(hw);              // 4
        Free(hw, b3);

        Assert.Equal(b3, Alloc(hw)); // lowest free bit is 3 again
    }

    [Fact]
    public void Alloc_UntilFull_ThenReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int allocatable = FsLayout.BlockCount - FsLayout.FirstDataBlock; // 254
        int last = 0;
        for (int i = 0; i < allocatable; i++)
        {
            last = Alloc(hw);
            Assert.True(last >= FsLayout.FirstDataBlock);
        }
        Assert.Equal(FsLayout.BlockCount - 1, last);   // last allocatable block
        Assert.Equal(-1, Alloc(hw));                   // disk full
    }

    // ---- free-chaining ---------------------------------------------------

    [Fact]
    public void ChainSetNext_ThenChainNext_RoundTrips()
    {
        Hardware hw = NewFsHardware();
        int a = Alloc(hw);
        int b = Alloc(hw);
        SetNext(hw, a, b);
        Assert.Equal(b, Next(hw, a));
    }

    [Fact]
    public void Chain_CanFormAndTraverseAMultiBlockFile()
    {
        Hardware hw = NewFsHardware();
        int a = Alloc(hw);
        int b = Alloc(hw);
        int c = Alloc(hw);
        SetNext(hw, a, b);
        SetNext(hw, b, c);
        // c keeps its end-of-chain marker from allocation.

        Assert.Equal(b, Next(hw, a));
        Assert.Equal(c, Next(hw, b));
        Assert.Equal(FsLayout.EndOfChain, Next(hw, c));
    }

    // ---- on-disk effect --------------------------------------------------

    [Fact]
    public void Format_AfterFlush_WritesTheSuperblockAndBitmapToDisk()
    {
        Hardware hw = NewFsHardware();
        Flush(hw);

        Assert.Equal(FsLayout.SuperMagic, DiskWord(hw, FsLayout.SuperBlock, FsLayout.SuperMagicOffset));
        Assert.Equal(FsLayout.BlockCount, DiskWord(hw, FsLayout.SuperBlock, FsLayout.SuperBlockCountOffset));
        Assert.Equal(FsLayout.BlockCount - FsLayout.FirstDataBlock, DiskWord(hw, FsLayout.SuperBlock, FsLayout.SuperFreeCountOffset));
        Assert.Equal(0b11, DiskWord(hw, FsLayout.BitmapBlock, 0)); // blocks 0,1 marked used
    }

    [Fact]
    public void Alloc_AfterFlush_ShowsTheBitmapBitSetOnDisk()
    {
        Hardware hw = NewFsHardware();
        int block = Alloc(hw); // 2
        Flush(hw);

        // Bit `block` of the bitmap word 0 should now be set (blocks 0,1,2 → 0b111).
        int expected = (1 << 0) | (1 << 1) | (1 << block);
        Assert.Equal(expected, DiskWord(hw, FsLayout.BitmapBlock, 0));
    }

    [Fact]
    public void FreedBlock_AfterFlush_IsClearedInTheOnDiskBitmap()
    {
        Hardware hw = NewFsHardware();
        int block = Alloc(hw); // 2
        Free(hw, block);
        Flush(hw);

        Assert.Equal(0b11, DiskWord(hw, FsLayout.BitmapBlock, 0)); // back to just blocks 0,1
    }
}
