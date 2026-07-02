using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for nested directories (Increment 4b): fs_mkdir and fs_path_resolve
/// (via IvtFsOp). Covers subdirectory creation, single- and multi-level path resolution,
/// descending only through directories, directory-entry resolution, trailing slashes, and
/// the failure paths (missing component, file-in-the-middle, duplicate mkdir, empty path).
/// </summary>
public class FsPathTests
{
    private static int Buf(int index)
    {
        return OsLayout.TotalSize + index * 64;
    }

    private static Hardware NewFsHardware()
    {
        Hardware hw = Test.NewHardware(Test.MinMachineSize, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        FsOp(hw, Hardware.FsOpFormat, 0, 0, 0, 0);
        return hw;
    }

    // A name null-padded to NameMaxChars words (for dir entries).
    private static int Name(Hardware hw, int index, string name)
    {
        int addr = Buf(index);
        for (int i = 0; i < FsLayout.NameMaxChars; i++)
        {
            Test.WriteWord(hw, addr + i * 4, i < name.Length ? name[i] : 0);
        }
        return addr;
    }

    // A null-terminated path string (word-per-char), any length.
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

    private static int RootDir(Hardware hw) => FsOp(hw, Hardware.FsOpRootDir, 0, 0, 0, 0);
    private static int Lookup(Hardware hw, int dir, int name) => FsOp(hw, Hardware.FsOpLookup, dir, name, 0, 0);
    private static int Insert(Hardware hw, int dir, int name, int type, int first) => FsOp(hw, Hardware.FsOpInsert, dir, name, type, first);
    private static int Mkdir(Hardware hw, int dir, int name) => FsOp(hw, Hardware.FsOpMkdir, dir, name, 0, 0);
    private static int Resolve(Hardware hw, int path) => FsOp(hw, Hardware.FsOpPathResolve, path, 0, 0, 0);

    // ---- mkdir -----------------------------------------------------------

    [Fact]
    public void Mkdir_CreatesADirectoryEntryAndBlock()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int a = Name(hw, 0, "a");

        int dirBlock = Mkdir(hw, root, a);
        Assert.True(dirBlock >= FsLayout.FirstDataBlock);

        int entry = Lookup(hw, root, a);
        Assert.True(entry > 0);
        Assert.Equal(FsLayout.DirTypeDir, Test.ReadWord(hw, entry + FsLayout.DirEntryType));
        Assert.Equal(dirBlock, Test.ReadWord(hw, entry + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Mkdir_DuplicateName_IsRejected()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int a = Name(hw, 0, "a");

        Assert.True(Mkdir(hw, root, a) >= 0);
        Assert.Equal(-1, Mkdir(hw, root, a));
    }

    // ---- path resolution -------------------------------------------------

    [Fact]
    public void Resolve_TopLevelFile()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int fooName = Name(hw, 0, "foo");
        int entry = Insert(hw, root, fooName, FsLayout.DirTypeFile, 55);

        Assert.Equal(entry, Resolve(hw, Path(hw, 1, "/foo")));
        Assert.Equal(55, Test.ReadWord(hw, Resolve(hw, Path(hw, 1, "/foo")) + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Resolve_NestedPath_DescendsThroughDirectories()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);

        int dirA = Mkdir(hw, root, Name(hw, 0, "a"));
        int dirB = Mkdir(hw, dirA, Name(hw, 1, "b"));
        Insert(hw, dirB, Name(hw, 2, "c"), FsLayout.DirTypeFile, 99);

        int entry = Resolve(hw, Path(hw, 3, "/a/b/c"));
        Assert.True(entry > 0);
        Assert.Equal(FsLayout.DirTypeFile, Test.ReadWord(hw, entry + FsLayout.DirEntryType));
        Assert.Equal(99, Test.ReadWord(hw, entry + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Resolve_DirectoryEntry_ReturnsTheDirectory()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int dirA = Mkdir(hw, root, Name(hw, 0, "a"));

        int entry = Resolve(hw, Path(hw, 1, "/a"));
        Assert.Equal(FsLayout.DirTypeDir, Test.ReadWord(hw, entry + FsLayout.DirEntryType));
        Assert.Equal(dirA, Test.ReadWord(hw, entry + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Resolve_TrailingSlash_ResolvesTheLastComponent()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        Mkdir(hw, root, Name(hw, 0, "a"));

        int direct = Resolve(hw, Path(hw, 1, "/a"));
        int trailing = Resolve(hw, Path(hw, 2, "/a/"));
        Assert.True(direct > 0);
        Assert.Equal(direct, trailing);
    }

    // ---- failure paths ---------------------------------------------------

    [Fact]
    public void Resolve_MissingComponent_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        Mkdir(hw, root, Name(hw, 0, "a"));

        Assert.Equal(-1, Resolve(hw, Path(hw, 1, "/a/nope")));
        Assert.Equal(-1, Resolve(hw, Path(hw, 2, "/ghost")));
    }

    [Fact]
    public void Resolve_ComponentInMiddleIsAFile_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        // "f" is a file, so "/f/x" cannot descend into it.
        Insert(hw, root, Name(hw, 0, "f"), FsLayout.DirTypeFile, 5);

        Assert.Equal(-1, Resolve(hw, Path(hw, 1, "/f/x")));
    }

    [Fact]
    public void Resolve_EmptyOrRootPath_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(-1, Resolve(hw, Path(hw, 0, "/")));
        Assert.Equal(-1, Resolve(hw, Path(hw, 1, "")));
    }
}
