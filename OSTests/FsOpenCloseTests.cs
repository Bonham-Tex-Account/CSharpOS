using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the file-syscall cores (Increment 5a): fs_open_core / fs_close_core
/// via the IvtFsOp FsOpOpen/FsOpClose selectors (which take an absolute path + explicit
/// process index). Covers create-on-open, opening an existing file, the open-file-table and
/// fd-table bookkeeping, close + reuse, distinct fds, directory rejection, nested-path open,
/// and the fd/OFT exhaustion + invalid-fd edges.
/// </summary>
public class FsOpenCloseTests
{
    private const int Proc = 0;
    private const int Create = Hardware.FsysCreateFlag;

    private static int Buf(int index) => OsLayout.TotalSize + index * 64;

    private static Hardware NewFsHardware()
    {
        Hardware hw = Test.NewHardware(Test.MinMachineSize, new FakeOS());
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

    private static int Name(Hardware hw, int index, string name)
    {
        int addr = Buf(index);
        for (int i = 0; i < FsLayout.NameMaxChars; i++)
        {
            Test.WriteWord(hw, addr + i * 4, i < name.Length ? name[i] : 0);
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

    private static int Open(Hardware hw, int path, int flags) => FsOp(hw, Hardware.FsOpOpen, path, flags, Proc, 0);
    private static int Close(Hardware hw, int fd) => FsOp(hw, Hardware.FsOpClose, fd, Proc, 0, 0);
    private static int Resolve(Hardware hw, int path) => FsOp(hw, Hardware.FsOpPathResolve, path, 0, 0, 0);
    private static int RootDir(Hardware hw) => FsOp(hw, Hardware.FsOpRootDir, 0, 0, 0, 0);
    private static int Mkdir(Hardware hw, int dir, int name) => FsOp(hw, Hardware.FsOpMkdir, dir, name, 0, 0);

    private static int Oft(Hardware hw, int i, int field) => Test.ReadWord(hw, OsLayout.OftAddress(i) + field);
    private static int FdSlot(Hardware hw, int k) => Test.ReadWord(hw, OsLayout.ProcessEntryAddress(Proc) + Hardware.ProcessEntryFdTable + k * 4);

    // ---- create-on-open --------------------------------------------------

    [Fact]
    public void Open_Create_MakesTheFileAndReturnsFdTwo()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 0, "/foo"), Create);

        Assert.Equal(2, fd);                       // first file fd
        Assert.Equal(1, FdSlot(hw, 2));            // fd[2] = OFT index 0 + 1
        Assert.Equal(1, Oft(hw, 0, OsLayout.OftInUse));
        Assert.Equal(0, Oft(hw, 0, OsLayout.OftOffset));
        Assert.Equal(0, Oft(hw, 0, OsLayout.OftSize));
        Assert.True(Oft(hw, 0, OsLayout.OftFirstBlock) >= FsLayout.FirstDataBlock);

        // The file now exists as a file entry.
        int entry = Resolve(hw, Path(hw, 1, "/foo"));
        Assert.True(entry > 0);
        Assert.Equal(FsLayout.DirTypeFile, Test.ReadWord(hw, entry + FsLayout.DirEntryType));
        Assert.Equal(Oft(hw, 0, OsLayout.OftFirstBlock), Test.ReadWord(hw, entry + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Open_ExistingFile_Succeeds_WithoutCreateFlag()
    {
        Hardware hw = NewFsHardware();
        Open(hw, Path(hw, 0, "/foo"), Create);
        Close(hw, 2);

        int fd = Open(hw, Path(hw, 1, "/foo"), 0);  // no create flag; file already exists
        Assert.Equal(2, fd);
    }

    [Fact]
    public void Open_Missing_WithoutCreate_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(-1, Open(hw, Path(hw, 0, "/ghost"), 0));
    }

    [Fact]
    public void Open_CreateOnExisting_JustOpensIt()
    {
        Hardware hw = NewFsHardware();
        Open(hw, Path(hw, 0, "/foo"), Create);
        Close(hw, 2);
        Assert.Equal(2, Open(hw, Path(hw, 1, "/foo"), Create)); // no duplicate error
    }

    [Fact]
    public void Open_ADirectory_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Mkdir(hw, RootDir(hw), Name(hw, 0, "d"));
        Assert.Equal(-1, Open(hw, Path(hw, 1, "/d"), Create));
    }

    [Fact]
    public void Open_CreateInNestedDirectory()
    {
        Hardware hw = NewFsHardware();
        Mkdir(hw, RootDir(hw), Name(hw, 0, "a"));

        int fd = Open(hw, Path(hw, 1, "/a/foo"), Create);
        Assert.Equal(2, fd);
        Assert.True(Resolve(hw, Path(hw, 2, "/a/foo")) > 0);
    }

    // ---- close + reuse ---------------------------------------------------

    [Fact]
    public void Close_FreesTheFdAndOftSlot_AndReopenReuses()
    {
        Hardware hw = NewFsHardware();
        Open(hw, Path(hw, 0, "/foo"), Create);

        Assert.Equal(0, Close(hw, 2));
        Assert.Equal(0, FdSlot(hw, 2));                 // fd slot cleared
        Assert.Equal(0, Oft(hw, 0, OsLayout.OftInUse)); // OFT slot freed

        Assert.Equal(2, Open(hw, Path(hw, 1, "/foo"), 0)); // reuses fd 2 + OFT 0
        Assert.Equal(1, Oft(hw, 0, OsLayout.OftInUse));
    }

    [Fact]
    public void Open_MultipleFiles_GetDistinctFdsAndOftSlots()
    {
        Hardware hw = NewFsHardware();
        int f1 = Open(hw, Path(hw, 0, "/a"), Create);
        int f2 = Open(hw, Path(hw, 1, "/b"), Create);
        int f3 = Open(hw, Path(hw, 2, "/c"), Create);

        Assert.Equal(2, f1);
        Assert.Equal(3, f2);
        Assert.Equal(4, f3);
        // fd[k] holds OFT index + 1, and they are distinct.
        Assert.NotEqual(FdSlot(hw, 2), FdSlot(hw, 3));
        Assert.NotEqual(FdSlot(hw, 3), FdSlot(hw, 4));
    }

    // ---- exhaustion + invalid fd -----------------------------------------

    [Fact]
    public void Open_ExhaustsFileDescriptors_ThenReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        // fds 2..7 = 6 available slots.
        for (int i = 0; i < Hardware.FdCount - 2; i++)
        {
            int fd = Open(hw, Path(hw, i, "/f" + i), Create);
            Assert.True(fd >= 2, $"open {i} should succeed");
        }
        Assert.Equal(-1, Open(hw, Path(hw, 7, "/extra"), Create)); // no free fd
    }

    [Theory]
    [InlineData(0)]  // stdin
    [InlineData(1)]  // stdout
    [InlineData(8)]  // >= FdCount
    [InlineData(2)]  // in range but not open
    public void Close_InvalidOrUnopenedFd_ReturnsMinusOne(int fd)
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(-1, Close(hw, fd));
    }
}
