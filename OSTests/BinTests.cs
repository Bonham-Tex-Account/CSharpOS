using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers the Bin flat block store: store/load round trips, the directory's true
/// content length, free-slot reuse, defensive copies, and every failure/edge path
/// (full disk, oversized data, free-slot and out-of-range access, constructor
/// validation).
/// </summary>
public class BinTests
{
    [Fact]
    public void StoreThenLoad_RoundTripsTheData()
    {
        Bin bin = new Bin(4, 16);
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        int slot = bin.Store(data);

        Assert.Equal(0, slot);
        Assert.True(bin.IsOccupied(slot));
        Assert.Equal(data, bin.Load(slot));
    }

    [Fact]
    public void Store_PutsDataInTheFirstFreeSlot()
    {
        Bin bin = new Bin(4, 16);
        Assert.Equal(0, bin.Store(new byte[] { 1 }));
        Assert.Equal(1, bin.Store(new byte[] { 2 }));
        Assert.Equal(2, bin.Store(new byte[] { 3 }));
    }

    [Fact]
    public void Load_ReturnsContentAtItsTrueLength_NotTheSlotSize()
    {
        Bin bin = new Bin(2, 32);
        byte[] data = new byte[] { 9, 8, 7 };
        int slot = bin.Store(data);

        byte[] loaded = bin.Load(slot);
        Assert.Equal(3, loaded.Length);
        Assert.Equal(data, loaded);
    }

    [Fact]
    public void Store_EmptyBlob_OccupiesSlotWithZeroLength()
    {
        Bin bin = new Bin(2, 16);
        int slot = bin.Store(Array.Empty<byte>());

        Assert.True(bin.IsOccupied(slot));
        Assert.Equal(0, bin.GetLength(slot));
        Assert.Equal(Array.Empty<byte>(), bin.Load(slot));
    }

    [Fact]
    public void Store_FullDisk_ReturnsMinusOne()
    {
        Bin bin = new Bin(2, 8);
        bin.Store(new byte[] { 1 });
        bin.Store(new byte[] { 2 });

        Assert.Equal(-1, bin.Store(new byte[] { 3 }));
    }

    [Fact]
    public void Store_OversizedData_Throws()
    {
        Bin bin = new Bin(2, 4);
        Assert.Throws<ArgumentException>(() => bin.Store(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void StoreIntoSlot_OversizedData_Throws()
    {
        Bin bin = new Bin(2, 4);
        Assert.Throws<ArgumentException>(() => bin.Store(0, new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Fact]
    public void Load_ReturnsACopy_MutatingItDoesNotAffectStorage()
    {
        Bin bin = new Bin(2, 16);
        int slot = bin.Store(new byte[] { 1, 2, 3 });

        byte[] first = bin.Load(slot);
        first[0] = 99;

        byte[] second = bin.Load(slot);
        Assert.Equal(new byte[] { 1, 2, 3 }, second);
    }

    [Fact]
    public void Load_FreeSlot_Throws()
    {
        Bin bin = new Bin(2, 16);
        Assert.Throws<InvalidOperationException>(() => bin.Load(0));
    }

    [Fact]
    public void GetLength_FreeSlot_Throws()
    {
        Bin bin = new Bin(2, 16);
        Assert.Throws<InvalidOperationException>(() => bin.GetLength(1));
    }

    [Fact]
    public void Free_IsIdempotent_OnAnAlreadyFreeSlot()
    {
        Bin bin = new Bin(2, 16);
        bin.Free(0); // never stored
        bin.Free(0); // again
        Assert.False(bin.IsOccupied(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void OutOfRangeSlot_ThrowsOnEveryAccessor(int slot)
    {
        Bin bin = new Bin(2, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.Load(slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.Store(slot, new byte[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.GetLength(slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.Free(slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.IsOccupied(slot));
    }

    [Fact]
    public void StoreIntoSlot_OverwritesExistingContent()
    {
        Bin bin = new Bin(2, 16);
        bin.Store(0, new byte[] { 1, 2, 3, 4 });
        bin.Store(0, new byte[] { 9, 9 });

        Assert.Equal(new byte[] { 9, 9 }, bin.Load(0));
        Assert.Equal(2, bin.GetLength(0));
    }

    [Fact]
    public void Free_MakesSlotReusableAsTheFirstFreeSlot()
    {
        Bin bin = new Bin(3, 16);
        bin.Store(new byte[] { 1 }); // slot 0
        bin.Store(new byte[] { 2 }); // slot 1
        bin.Free(0);

        Assert.Equal(0, bin.Store(new byte[] { 3 }));
    }

    [Fact]
    public void Overwrite_WithShorterData_LeavesNoStaleTail()
    {
        Bin bin = new Bin(1, 16);
        bin.Store(0, new byte[] { 1, 2, 3, 4, 5, 6 });
        bin.Store(0, new byte[] { 7, 8 });

        // Only the new content is visible; nothing leaks from the longer first write.
        Assert.Equal(new byte[] { 7, 8 }, bin.Load(0));
    }

    [Fact]
    public void FreeSlotCount_TracksStoresAndFrees()
    {
        Bin bin = new Bin(3, 16);
        Assert.Equal(3, bin.FreeSlotCount);

        bin.Store(new byte[] { 1 });
        bin.Store(new byte[] { 2 });
        Assert.Equal(1, bin.FreeSlotCount);

        bin.Free(0);
        Assert.Equal(2, bin.FreeSlotCount);
    }

    [Fact]
    public void GetLength_ReturnsStoredContentLength()
    {
        Bin bin = new Bin(2, 32);
        int slot = bin.Store(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
        Assert.Equal(7, bin.GetLength(slot));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(-1, 16)]
    [InlineData(4, 0)]
    [InlineData(4, -2)]
    public void Constructor_RejectsNonPositiveGeometry(int slotCount, int slotSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bin(slotCount, slotSize));
    }

    [Fact]
    public void Constructor_ExposesGeometry()
    {
        Bin bin = new Bin(8, 256);
        Assert.Equal(8, bin.SlotCount);
        Assert.Equal(256, bin.SlotSize);
    }

    // ---- file-block region -----------------------------------------------

    [Fact]
    public void Constructor_ExposesFileBlockGeometry()
    {
        Bin bin = new Bin(8, 256, 16, 64);
        Assert.Equal(16, bin.FileBlockCount);
        Assert.Equal(64, bin.FileBlockSize);
    }

    [Fact]
    public void SlotOnlyBin_HasNoFileBlockRegion()
    {
        Bin bin = new Bin(4, 16);
        Assert.Equal(0, bin.FileBlockCount);
        Assert.Equal(0, bin.FileBlockSize);
    }

    [Fact]
    public void WriteFileBlockThenReadFileBlock_RoundTrips()
    {
        Bin bin = new Bin(2, 16, 4, 8);
        byte[] block = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        bin.WriteFileBlock(2, block);

        Assert.Equal(block, bin.ReadFileBlock(2));
    }

    [Fact]
    public void ReadFileBlock_NeverWritten_ReturnsZeros_DoesNotThrow()
    {
        Bin bin = new Bin(2, 16, 4, 8);
        Assert.Equal(new byte[8], bin.ReadFileBlock(1));
    }

    [Fact]
    public void ReadFileBlock_ReturnsACopy_MutatingItDoesNotAffectStorage()
    {
        Bin bin = new Bin(2, 16, 4, 8);
        bin.WriteFileBlock(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        byte[] first = bin.ReadFileBlock(0);
        first[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, bin.ReadFileBlock(0));
    }

    [Fact]
    public void WriteFileBlock_OverwritesWholeBlock()
    {
        Bin bin = new Bin(2, 16, 4, 4);
        bin.WriteFileBlock(0, new byte[] { 1, 2, 3, 4 });
        bin.WriteFileBlock(0, new byte[] { 9, 8, 7, 6 });
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, bin.ReadFileBlock(0));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void WriteFileBlock_WrongSize_Throws(int length)
    {
        Bin bin = new Bin(2, 16, 4, 4);
        Assert.Throws<ArgumentException>(() => bin.WriteFileBlock(0, new byte[length]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void FileBlock_OutOfRange_Throws(int block)
    {
        Bin bin = new Bin(2, 16, 4, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.ReadFileBlock(block));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.WriteFileBlock(block, new byte[8]));
    }

    [Fact]
    public void FileBlockAccess_OnSlotOnlyBin_Throws()
    {
        Bin bin = new Bin(4, 16);
        Assert.Throws<InvalidOperationException>(() => bin.ReadFileBlock(0));
        Assert.Throws<InvalidOperationException>(() => bin.WriteFileBlock(0, new byte[0]));
    }

    [Fact]
    public void Constructor_NegativeFileBlockCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bin(4, 16, -1, 8));
    }

    [Fact]
    public void Constructor_PositiveFileBlockCountWithNonPositiveSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bin(4, 16, 2, 0));
    }

    [Fact]
    public void FileBlockRegion_IsIndependentOfSlotStorage()
    {
        Bin bin = new Bin(2, 16, 2, 8);
        bin.Store(0, new byte[] { 1, 2, 3 });
        bin.WriteFileBlock(0, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

        // Neither region disturbs the other.
        Assert.Equal(new byte[] { 1, 2, 3 }, bin.Load(0));
        Assert.Equal(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, bin.ReadFileBlock(0));
    }

    // ---- persistence -----------------------------------------------------

    [Fact]
    public void SaveThenLoad_RestoresFileBlocksIntoAFreshBin()
    {
        string path = Path.GetTempFileName();
        try
        {
            Bin source = new Bin(2, 16, 4, 8);
            source.WriteFileBlock(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            source.WriteFileBlock(3, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
            source.Save(path);

            Bin restored = new Bin(2, 16, 4, 8);
            restored.Load(path);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, restored.ReadFileBlock(0));
            Assert.Equal(new byte[8], restored.ReadFileBlock(1)); // untouched block stays zero
            Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, restored.ReadFileBlock(3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MismatchedGeometry_Throws()
    {
        string path = Path.GetTempFileName();
        try
        {
            new Bin(2, 16, 4, 8).Save(path);
            Assert.Throws<InvalidDataException>(() => new Bin(2, 16, 8, 8).Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BadMagic_Throws()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });
            Assert.Throws<InvalidDataException>(() => new Bin(2, 16, 4, 8).Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveOrLoad_OnSlotOnlyBin_Throws()
    {
        string path = Path.GetTempFileName();
        try
        {
            Bin bin = new Bin(4, 16);
            Assert.Throws<InvalidOperationException>(() => bin.Save(path));
            Assert.Throws<InvalidOperationException>(() => bin.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
