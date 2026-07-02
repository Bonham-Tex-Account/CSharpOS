using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the Phase-1 filesystem maintenance layer (via the IvtFsOp selectors):
/// fs_unlink (frees a file's whole block chain — the old fs_dir_remove leaked it), fs_mkdir_path,
/// fs_readdir, the superblock FreeCount bookkeeping (fs_alloc_block/fs_free_block), the
/// single-open policy (fs_open_core rejects a second open), and the superblock/bitmap cache pin.
/// </summary>
public class FsMaintTests
{
    private const int Proc = 0;
    private const int Create = Hardware.FsysCreateFlag;
    private static int Memory => Test.MachineWithHeap(8192);
    private static int Buf(int index) => OsLayout.TotalSize + index * 1024;

    private static Hardware NewFsHardware()
    {
        Hardware hw = Test.NewHardware(Memory, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        FsOp(hw, Hardware.FsOpFormat, 0, 0, 0, 0);
        return hw;
    }

    private static int Path(Hardware hw, int index, string path)
    {
        int addr = Buf(index);
        for (int i = 0; i < path.Length; i++)
        {
            Test.WriteWord(hw, addr + i * 4, path[i]);
        }
        Test.WriteWord(hw, addr + path.Length * 4, 0);
        return addr;
    }

    private static int FsOp(Hardware hw, int op, int a1, int a2, int a3, int a4)
    {
        hw.WriteRegister(RegisterName.EBX, a1);
        hw.WriteRegister(RegisterName.ECX, a2);
        hw.WriteRegister(RegisterName.EDX, a3);
        hw.WriteRegister(RegisterName.ESI, a4);
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, op);
        return Test.ReadWord(hw, OsLayout.FsResultOffset);
    }

    private static int Open(Hardware hw, string path, int flags) => FsOp(hw, Hardware.FsOpOpen, Path(hw, 5, path), flags, Proc, 0);
    private static int Close(Hardware hw, int fd) => FsOp(hw, Hardware.FsOpClose, fd, Proc, 0, 0);
    private static int Write(Hardware hw, int fd, int buf, int count) => FsOp(hw, Hardware.FsOpWrite, fd, buf, count, Proc);
    private static int Unlink(Hardware hw, string path) => FsOp(hw, Hardware.FsOpUnlink, Path(hw, 6, path), 0, 0, 0);
    private static int MkdirPath(Hardware hw, string path) => FsOp(hw, Hardware.FsOpMkdirPath, Path(hw, 6, path), 0, 0, 0);
    private static int AllocBlock(Hardware hw) => FsOp(hw, Hardware.FsOpAllocBlock, 0, 0, 0, 0);
    private static int RootDir(Hardware hw) => FsOp(hw, Hardware.FsOpRootDir, 0, 0, 0, 0);

    // The superblock free count, read through the cache Get op.
    private static int FreeCount(Hardware hw)
    {
        hw.WriteRegister(RegisterName.EBX, FsLayout.SuperBlock);
        hw.RunOsRoutineSynchronously(Hardware.IvtCacheOp, Hardware.CacheOpGet);
        int data = Test.ReadWord(hw, OsLayout.CacheResultOffset);
        return Test.ReadWord(hw, data + FsLayout.SuperFreeCountOffset);
    }

    // ReadDir the n-th in-use entry of `dirBlock` into Buf(4); returns the entry type (or -1).
    private static int ReadDir(Hardware hw, int dirBlock, int index)
    {
        return FsOp(hw, Hardware.FsOpReadDir, dirBlock, index, Buf(4), 0);
    }

    private static string EntryName(Hardware hw)
    {
        char[] chars = new char[FsLayout.NameMaxChars];
        for (int i = 0; i < FsLayout.NameMaxChars; i++)
        {
            chars[i] = (char)Test.ReadWord(hw, Buf(4) + FsLayout.DirEntryName + i * 4);
        }
        return new string(chars).TrimEnd('\0');
    }

    // ---- FreeCount bookkeeping -------------------------------------------

    [Fact]
    public void Format_SetsFreeCountToAllDataBlocksMinusTheRootDir()
    {
        Hardware hw = NewFsHardware();
        // blocks 0 (super), 1 (bitmap), 2 (root dir) are used; the rest are free.
        Assert.Equal(FsLayout.BlockCount - FsLayout.FirstDataBlock - 1, FreeCount(hw));
    }

    [Fact]
    public void AllocThenFree_RestoresFreeCount()
    {
        Hardware hw = NewFsHardware();
        int before = FreeCount(hw);
        int block = AllocBlock(hw);
        Assert.True(block >= FsLayout.FirstDataBlock);
        Assert.Equal(before - 1, FreeCount(hw));
        FsOp(hw, Hardware.FsOpFreeBlock, block, 0, 0, 0);
        Assert.Equal(before, FreeCount(hw));
    }

    // ---- unlink ----------------------------------------------------------

    [Fact]
    public void Unlink_FreesTheFilesBlock_AndRestoresFreeCount()
    {
        Hardware hw = NewFsHardware();
        int free0 = FreeCount(hw);
        int fd = Open(hw, "/f", Create);   // allocates one block for the file
        Assert.True(fd >= 2);
        Close(hw, fd);
        Assert.Equal(free0 - 1, FreeCount(hw));

        Assert.Equal(0, Unlink(hw, "/f"));
        Assert.Equal(free0, FreeCount(hw));            // the block returned to the pool
        Assert.Equal(-1, Open(hw, "/f", 0));           // the file is gone
    }

    [Fact]
    public void Unlink_MultiBlockFile_FreesEveryBlock()
    {
        Hardware hw = NewFsHardware();
        int free0 = FreeCount(hw);
        int fd = Open(hw, "/big", Create);
        // > CharsPerBlock (63) words forces the chain to grow past one block.
        int count = FsLayout.CharsPerBlock + 10;
        for (int i = 0; i < count; i++)
        {
            Test.WriteWord(hw, Buf(0) + i * 4, 'x');
        }
        Assert.Equal(count, Write(hw, fd, Buf(0), count));
        Close(hw, fd);
        Assert.Equal(free0 - 2, FreeCount(hw));        // two blocks consumed

        Assert.Equal(0, Unlink(hw, "/big"));
        Assert.Equal(free0, FreeCount(hw));            // both freed
    }

    [Fact]
    public void Unlink_ThenAlloc_ReusesTheFreedBlock()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, "/f", Create);
        int fileBlock = Test.ReadWord(hw, OsLayout.OftAddress(0) + OsLayout.OftFirstBlock);
        Close(hw, fd);
        Unlink(hw, "/f");
        Assert.Equal(fileBlock, AllocBlock(hw));       // the freed block is first free again
    }

    [Fact]
    public void Unlink_MissingFile_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(-1, Unlink(hw, "/nope"));
    }

    [Fact]
    public void Unlink_Directory_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Assert.True(MkdirPath(hw, "/d") >= FsLayout.FirstDataBlock);
        Assert.Equal(-1, Unlink(hw, "/d"));            // only files can be unlinked
    }

    [Fact]
    public void Unlink_OpenFile_ReturnsMinusOne_AndKeepsIt()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, "/f", Create);
        Assert.Equal(-1, Unlink(hw, "/f"));            // refuse while open
        Close(hw, fd);
        Assert.Equal(0, Unlink(hw, "/f"));             // now it works
    }

    // ---- single-open policy ----------------------------------------------

    [Fact]
    public void Open_SameFileTwice_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, "/f", Create);
        Assert.True(fd >= 2);
        Assert.Equal(-1, Open(hw, "/f", 0));           // already open
        Close(hw, fd);
        Assert.True(Open(hw, "/f", 0) >= 2);           // fine after close
    }

    // ---- mkdir by path ---------------------------------------------------

    [Fact]
    public void MkdirPath_CreatesADirectoryReachableByPath()
    {
        Hardware hw = NewFsHardware();
        int block = MkdirPath(hw, "/sub");
        Assert.True(block >= FsLayout.FirstDataBlock);
        // a file can now be created inside it
        Assert.True(Open(hw, "/sub/f", Create) >= 2);
    }

    [Fact]
    public void MkdirPath_Nested_Works()
    {
        Hardware hw = NewFsHardware();
        Assert.True(MkdirPath(hw, "/a") >= FsLayout.FirstDataBlock);
        Assert.True(MkdirPath(hw, "/a/b") >= FsLayout.FirstDataBlock);
        Assert.True(Open(hw, "/a/b/f", Create) >= 2);
    }

    [Fact]
    public void MkdirPath_DuplicateName_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Assert.True(MkdirPath(hw, "/a") >= FsLayout.FirstDataBlock);
        Assert.Equal(-1, MkdirPath(hw, "/a"));
    }

    // ---- readdir ---------------------------------------------------------

    [Fact]
    public void ReadDir_EnumeratesEntries_ThenReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        Close(hw, Open(hw, "/a", Create));
        MkdirPath(hw, "/b");

        // Entry 0
        int t0 = ReadDir(hw, root, 0);
        string n0 = EntryName(hw);
        // Entry 1
        int t1 = ReadDir(hw, root, 1);
        string n1 = EntryName(hw);

        Assert.Contains(FsLayout.DirTypeFile, new[] { t0, t1 });
        Assert.Contains(FsLayout.DirTypeDir, new[] { t0, t1 });
        Assert.Contains("a", new[] { n0, n1 });
        Assert.Contains("b", new[] { n0, n1 });

        Assert.Equal(-1, ReadDir(hw, root, 2));        // only two entries
    }

    [Fact]
    public void ReadDir_EmptyDirectory_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int dir = MkdirPath(hw, "/empty");
        Assert.Equal(-1, ReadDir(hw, dir, 0));
    }

    [Fact]
    public void ReadDir_SkipsRemovedEntries()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        Close(hw, Open(hw, "/keep", Create));
        Close(hw, Open(hw, "/gone", Create));
        Unlink(hw, "/gone");

        Assert.Equal(FsLayout.DirTypeFile, ReadDir(hw, root, 0));
        Assert.Equal("keep", EntryName(hw));
        Assert.Equal(-1, ReadDir(hw, root, 1));        // the removed entry is not enumerated
    }

    // ---- cache pin -------------------------------------------------------

    [Fact]
    public void Superblock_SurvivesCachePressure_BecausePinned()
    {
        Hardware hw = NewFsHardware();
        // Allocate far more distinct blocks than the cache has slots, touching each so the LRU
        // clock would evict the superblock/bitmap were they not pinned.
        for (int i = 0; i < OsLayout.CacheSlotCount * 3; i++)
        {
            int b = AllocBlock(hw);
            Assert.True(b >= FsLayout.FirstDataBlock);
        }
        // The pinned superblock is still coherent: its FreeCount reflects every allocation.
        int expected = (FsLayout.BlockCount - FsLayout.FirstDataBlock - 1) - OsLayout.CacheSlotCount * 3;
        Assert.Equal(expected, FreeCount(hw));
    }
}
