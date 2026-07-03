using CSharpOS;
using CSharpOSConsole.Visualization;

namespace OSTests;

/// <summary>
/// Deterministic tests for FsDiskView's host-side reconstruction of the filesystem
/// (superblock stats, per-block allocation map, and the directory tree) from a hand-seeded
/// FS driven through the IvtFsOp selectors — no Spectre dependency. Because the FS routines
/// are write-back cached and nothing is flushed in an isolation run, the seeded entries live
/// dirty in the OS cache and never reach the Bin; a passing reconstruction therefore proves
/// the reader's cache-first path (a Bin-only reader would see an empty disk here).
/// </summary>
public class FsDiskViewTests
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

    private static int Data(Hardware hw, int index, string s)
    {
        int addr = Buf(index);
        for (int i = 0; i < s.Length; i++)
        {
            Test.WriteWord(hw, addr + i * 4, s[i]);
        }
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
    private static int MkdirPath(Hardware hw, string path) => FsOp(hw, Hardware.FsOpMkdirPath, Path(hw, 6, path), 0, 0, 0);

    // Creates a file at `path` and writes `content` (word-per-char), then closes it.
    private static void WriteFile(Hardware hw, string path, string content)
    {
        int fd = Open(hw, path, Create);
        Assert.True(fd >= 0);
        Write(hw, fd, Data(hw, 0, content), content.Length);
        Close(hw, fd);
    }

    private static FsDiskView.DiskNode? Child(FsDiskView.DiskNode parent, string name)
    {
        foreach (FsDiskView.DiskNode child in parent.Children)
        {
            if (child.Name == name)
            {
                return child;
            }
        }
        return null;
    }

    // ---- guards -----------------------------------------------------------

    [Fact]
    public void NoOsImage_ReturnsNull()
    {
        Hardware hw = Test.NewHardware(Memory, new FakeOS()); // no ReserveOsMemory → no OS
        Assert.Null(FsDiskView.ReadDisk(hw));
    }

    [Fact]
    public void UnformattedDisk_YieldsAnUnformattedSnapshotWithAnEmptyRoot()
    {
        Hardware hw = Test.NewHardware(Memory, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize); // OS present, but the FS is never formatted

        FsDiskView.Snapshot? disk = FsDiskView.ReadDisk(hw);

        Assert.NotNull(disk);
        Assert.False(disk!.Formatted);
        Assert.Empty(disk.Root.Children);
        Assert.Empty(disk.BlockRoles);
    }

    // ---- superblock stats + block map -------------------------------------

    [Fact]
    public void FreshFormat_StatsAndReservedBlocks_AreCorrect()
    {
        Hardware hw = NewFsHardware();

        FsDiskView.Snapshot? disk = FsDiskView.ReadDisk(hw);

        Assert.NotNull(disk);
        Assert.True(disk!.Formatted);
        Assert.Equal(FsLayout.BlockCount, disk.BlockCount);
        Assert.Equal(FsLayout.FirstDataBlock, disk.RootBlock);
        Assert.Equal(FsLayout.BlockCount, disk.BlockRoles.Length);
        Assert.Equal(FsDiskView.BlockRole.Super, disk.BlockRoles[FsLayout.SuperBlock]);
        Assert.Equal(FsDiskView.BlockRole.Bitmap, disk.BlockRoles[FsLayout.BitmapBlock]);
        // Only the root dir is allocated in the data region on a fresh format.
        Assert.Equal(FsDiskView.BlockRole.Used, disk.BlockRoles[FsLayout.FirstDataBlock]);
        Assert.Equal(1, disk.UsedCount);
        // free-data accounting: (data blocks) − (used data blocks).
        Assert.Equal(FsLayout.BlockCount - FsLayout.FirstDataBlock - disk.UsedCount, disk.FreeCount);
    }

    // ---- directory tree ---------------------------------------------------

    [Fact]
    public void ReconstructsNestedTree_WithFileSizes_FromUnflushedCacheBlocks()
    {
        Hardware hw = NewFsHardware();
        MkdirPath(hw, "/bin");
        WriteFile(hw, "/note", "hi");        // 2 chars → 1 data block
        WriteFile(hw, "/bin/app", "prog");   // 4 chars → 1 data block

        FsDiskView.Snapshot? disk = FsDiskView.ReadDisk(hw);

        Assert.NotNull(disk);
        Assert.Equal("/", disk!.Root.Name);
        Assert.True(disk.Root.IsDir);

        FsDiskView.DiskNode? bin = Child(disk.Root, "bin");
        Assert.NotNull(bin);
        Assert.True(bin!.IsDir);

        FsDiskView.DiskNode? note = Child(disk.Root, "note");
        Assert.NotNull(note);
        Assert.False(note!.IsDir);
        Assert.Equal(2, note.Size);

        FsDiskView.DiskNode? app = Child(bin, "app");
        Assert.NotNull(app);
        Assert.False(app!.IsDir);
        Assert.Equal(4, app.Size);

        // root dir + /bin dir block + /note block + /bin/app block = 4 allocated data blocks.
        Assert.Equal(4, disk.UsedCount);
    }

    [Fact]
    public void FlattenTree_YieldsDepthFirstRows_RootFirst()
    {
        Hardware hw = NewFsHardware();
        MkdirPath(hw, "/bin");
        WriteFile(hw, "/bin/app", "x");

        FsDiskView.Snapshot? disk = FsDiskView.ReadDisk(hw);
        List<FsDiskView.TreeRow> rows = FsDiskView.FlattenTree(disk);

        Assert.Equal(0, rows[0].Depth);
        Assert.Equal("/", rows[0].Node.Name);
        // /bin appears at depth 1 and its child app at depth 2 (directly after its parent).
        int binIndex = rows.FindIndex(r => r.Node.Name == "bin");
        Assert.True(binIndex > 0);
        Assert.Equal(1, rows[binIndex].Depth);
        Assert.Equal("app", rows[binIndex + 1].Node.Name);
        Assert.Equal(2, rows[binIndex + 1].Depth);
    }
}
