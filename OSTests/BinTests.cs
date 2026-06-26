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
}
