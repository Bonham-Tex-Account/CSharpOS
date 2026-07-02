using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the ISA directory layer (OsRoutines fs_hash / fs_root_dir /
/// fs_dir_lookup / fs_dir_insert / fs_dir_remove via IvtFsOp): root-dir creation, name
/// hashing, insert+lookup, entry fields, duplicate rejection, name verification beyond the
/// hash, removal + slot reuse, multi-block directory chaining, and the on-disk effect.
/// Names are written word-per-char, null-padded, into the machine's heap above the OS region.
/// </summary>
public class FsDirectoryTests
{
    // Name buffers live in the heap above the OS region; each is NameMaxChars words.
    private static int NameBuf(int index)
    {
        return OsLayout.TotalSize + index * (FsLayout.NameMaxChars * 4);
    }

    private static Hardware NewFsHardware()
    {
        Hardware hw = Test.NewHardware(Test.MinMachineSize, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        FsOp(hw, Hardware.FsOpFormat, 0, 0, 0, 0);
        return hw;
    }

    private static void WriteName(Hardware hw, int addr, string name)
    {
        for (int i = 0; i < FsLayout.NameMaxChars; i++)
        {
            int ch = i < name.Length ? name[i] : 0;
            Test.WriteWord(hw, addr + i * 4, ch);
        }
    }

    // Writes `name` into buffer `index` and returns its address.
    private static int Name(Hardware hw, int index, string name)
    {
        int addr = NameBuf(index);
        WriteName(hw, addr, name);
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
    private static int Hash(Hardware hw, int nameAddr) => FsOp(hw, Hardware.FsOpHash, nameAddr, 0, 0, 0);
    private static int Lookup(Hardware hw, int dir, int nameAddr) => FsOp(hw, Hardware.FsOpLookup, dir, nameAddr, 0, 0);
    private static int Insert(Hardware hw, int dir, int nameAddr, int type, int first) => FsOp(hw, Hardware.FsOpInsert, dir, nameAddr, type, first);
    private static int Remove(Hardware hw, int dir, int nameAddr) => FsOp(hw, Hardware.FsOpRemove, dir, nameAddr, 0, 0);
    private static int ChainNext(Hardware hw, int block) => FsOp(hw, Hardware.FsOpChainNext, block, 0, 0, 0);
    private static void Flush(Hardware hw)
    {
        hw.WriteRegister(RegisterName.EBX, 0);
        hw.RunOsRoutineSynchronously(Hardware.IvtCacheOp, Hardware.CacheOpFlush);
    }

    // ---- root directory + hashing ----------------------------------------

    [Fact]
    public void RootDir_IsBlockTwo()
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(FsLayout.FirstDataBlock, RootDir(hw));
    }

    [Fact]
    public void Hash_IsDeterministicAndDistinguishesNames()
    {
        Hardware hw = NewFsHardware();
        int foo = Name(hw, 0, "foo");
        int bar = Name(hw, 1, "bar");

        Assert.Equal(Hash(hw, foo), Hash(hw, foo));
        Assert.NotEqual(Hash(hw, foo), Hash(hw, bar));
    }

    // ---- insert / lookup -------------------------------------------------

    [Fact]
    public void Insert_ThenLookup_FindsTheEntryWithItsFields()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int foo = Name(hw, 0, "foo");

        int inserted = Insert(hw, root, foo, FsLayout.DirTypeFile, 42);
        Assert.True(inserted > 0);

        int found = Lookup(hw, root, foo);
        Assert.Equal(inserted, found);
        Assert.Equal(FsLayout.DirTypeFile, Test.ReadWord(hw, found + FsLayout.DirEntryType));
        Assert.Equal(42, Test.ReadWord(hw, found + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Lookup_MissingName_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int ghost = Name(hw, 0, "ghost");
        Assert.Equal(-1, Lookup(hw, root, ghost));
    }

    [Fact]
    public void Insert_DuplicateName_IsRejected()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int foo = Name(hw, 0, "foo");

        Assert.True(Insert(hw, root, foo, FsLayout.DirTypeFile, 10) > 0);
        Assert.Equal(-1, Insert(hw, root, foo, FsLayout.DirTypeFile, 11)); // duplicate
    }

    [Fact]
    public void Lookup_DistinguishesSimilarNames_NotJustHashes()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int a = Name(hw, 0, "a");
        int ab = Name(hw, 1, "ab");

        int ea = Insert(hw, root, a, FsLayout.DirTypeFile, 100);
        int eab = Insert(hw, root, ab, FsLayout.DirTypeFile, 200);
        Assert.NotEqual(ea, eab);

        Assert.Equal(100, Test.ReadWord(hw, Lookup(hw, root, a) + FsLayout.DirEntryFirstBlock));
        Assert.Equal(200, Test.ReadWord(hw, Lookup(hw, root, ab) + FsLayout.DirEntryFirstBlock));
    }

    // ---- remove ----------------------------------------------------------

    [Fact]
    public void Remove_ThenLookup_IsGone_AndSlotIsReusable()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int foo = Name(hw, 0, "foo");

        Insert(hw, root, foo, FsLayout.DirTypeFile, 10);
        Assert.Equal(0, Remove(hw, root, foo));
        Assert.Equal(-1, Lookup(hw, root, foo));

        // The freed entry slot is reusable by a fresh insert.
        Assert.True(Insert(hw, root, foo, FsLayout.DirTypeFile, 20) > 0);
        Assert.Equal(20, Test.ReadWord(hw, Lookup(hw, root, foo) + FsLayout.DirEntryFirstBlock));
    }

    [Fact]
    public void Remove_MissingName_ReturnsMinusOne()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int ghost = Name(hw, 0, "ghost");
        Assert.Equal(-1, Remove(hw, root, ghost));
    }

    // ---- multi-block directory (chain extension) -------------------------

    [Fact]
    public void Insert_BeyondOneBlock_ExtendsTheDirectoryChain_AllStillFound()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);

        // DirEntriesPerBlock entries fill the root block; the next insert extends the chain.
        int count = FsLayout.DirEntriesPerBlock + 2;
        for (int i = 0; i < count; i++)
        {
            int n = Name(hw, i, "file" + i);
            Assert.True(Insert(hw, root, n, FsLayout.DirTypeFile, 100 + i) > 0);
        }

        Assert.NotEqual(FsLayout.EndOfChain, ChainNext(hw, root)); // chain grew past one block

        for (int i = 0; i < count; i++)
        {
            int n = Name(hw, i, "file" + i);
            int found = Lookup(hw, root, n);
            Assert.True(found > 0, $"file{i} should be found");
            Assert.Equal(100 + i, Test.ReadWord(hw, found + FsLayout.DirEntryFirstBlock));
        }
    }

    // ---- on-disk effect --------------------------------------------------

    [Fact]
    public void Insert_AfterFlush_WritesTheEntryToTheDirectoryBlockOnDisk()
    {
        Hardware hw = NewFsHardware();
        int root = RootDir(hw);
        int foo = Name(hw, 0, "foo");
        Insert(hw, root, foo, FsLayout.DirTypeFile, 7);
        Flush(hw);

        byte[] block = hw.Disk.ReadFileBlock(root);
        // Entry 0: type then (skipping hash/first/size) the name at DirEntryName.
        int type = block[FsLayout.DirEntryType] | (block[FsLayout.DirEntryType + 1] << 8);
        Assert.Equal(FsLayout.DirTypeFile, type);
        Assert.Equal((int)'f', block[FsLayout.DirEntryName]);
        Assert.Equal((int)'o', block[FsLayout.DirEntryName + 4]);
        Assert.Equal((int)'o', block[FsLayout.DirEntryName + 8]);
        Assert.Equal(0, block[FsLayout.DirEntryName + 12]); // null-padded
    }
}
