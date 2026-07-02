using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for byte-level file read/write (Increment 5b): fs_read_core /
/// fs_write_core via the IvtFsOp FsOpRead/FsOpWrite selectors. Covers write-then-read round
/// trips, size growth, reads clamped at EOF, empty-file reads, multi-block files (chain grow
/// + traversal past CharsPerBlock), sequential appends, mid-file overwrite, and the count=0 /
/// invalid-fd edges. File content is word-per-char; buffers live in the heap above the OS.
/// </summary>
public class FsReadWriteTests
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

    // Fills a buffer with `s` word-per-char and returns its address.
    private static int Data(Hardware hw, int index, string s)
    {
        int addr = Buf(index);
        for (int i = 0; i < s.Length; i++)
        {
            Test.WriteWord(hw, addr + i * 4, s[i]);
        }
        return addr;
    }

    private static string ReadChars(Hardware hw, int addr, int n)
    {
        char[] chars = new char[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = (char)Test.ReadWord(hw, addr + i * 4);
        }
        return new string(chars);
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
    private static int Write(Hardware hw, int fd, int buf, int count) => FsOp(hw, Hardware.FsOpWrite, fd, buf, count, Proc);
    private static int Read(Hardware hw, int fd, int buf, int count) => FsOp(hw, Hardware.FsOpRead, fd, buf, count, Proc);
    private static int OftSize(Hardware hw, int oft) => Test.ReadWord(hw, OsLayout.OftAddress(oft) + OsLayout.OftSize);

    // ---- basic round trip -------------------------------------------------

    [Fact]
    public void Write_ThenReopenAndRead_RoundTripsTheData()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Assert.Equal(5, Write(hw, fd, Data(hw, 0, "hello"), 5));
        Assert.Equal(5, OftSize(hw, 0));
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/f"), 0);
        Assert.Equal(5, Read(hw, fd, Buf(1), 5));
        Assert.Equal("hello", ReadChars(hw, Buf(1), 5));
    }

    [Fact]
    public void Read_PastEndOfFile_ReturnsOnlyWhatIsAvailable()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Write(hw, fd, Data(hw, 0, "abc"), 3);
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/f"), 0);
        Assert.Equal(3, Read(hw, fd, Buf(1), 10));   // asked for 10, only 3 exist
        Assert.Equal("abc", ReadChars(hw, Buf(1), 3));
    }

    [Fact]
    public void Read_FromEmptyFile_ReturnsZero()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Assert.Equal(0, Read(hw, fd, Buf(1), 8));
    }

    // ---- multi-block ------------------------------------------------------

    [Fact]
    public void WriteAndRead_SpanningMultipleBlocks()
    {
        Hardware hw = NewFsHardware();
        // 100 chars > CharsPerBlock (63): forces the chain to grow to two blocks.
        string big = new string('x', 40) + new string('y', 60);
        Assert.True(big.Length > FsLayout.CharsPerBlock);

        int fd = Open(hw, Path(hw, 5, "/big"), Create);
        Assert.Equal(big.Length, Write(hw, fd, Data(hw, 0, big), big.Length));
        Assert.Equal(big.Length, OftSize(hw, 0));
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/big"), 0);
        Assert.Equal(big.Length, Read(hw, fd, Buf(1), big.Length));
        Assert.Equal(big, ReadChars(hw, Buf(1), big.Length));
    }

    // ---- append + overwrite ----------------------------------------------

    [Fact]
    public void SequentialWrites_OnTheSameFd_Append()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Assert.Equal(2, Write(hw, fd, Data(hw, 0, "ab"), 2));  // offset 0 → 2
        Assert.Equal(2, Write(hw, fd, Data(hw, 1, "cd"), 2));  // offset 2 → 4
        Assert.Equal(4, OftSize(hw, 0));
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/f"), 0);
        Assert.Equal(4, Read(hw, fd, Buf(2), 4));
        Assert.Equal("abcd", ReadChars(hw, Buf(2), 4));
    }

    [Fact]
    public void Overwrite_FromStart_ReplacesBytes_KeepsSize()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Write(hw, fd, Data(hw, 0, "abcde"), 5);
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/f"), 0);          // reopen resets offset to 0
        Assert.Equal(2, Write(hw, fd, Data(hw, 1, "XY"), 2));  // overwrite first two
        Assert.Equal(5, OftSize(hw, 0));              // size unchanged
        Close(hw, fd);

        fd = Open(hw, Path(hw, 5, "/f"), 0);
        Assert.Equal(5, Read(hw, fd, Buf(2), 5));
        Assert.Equal("XYcde", ReadChars(hw, Buf(2), 5));
    }

    // ---- edges ------------------------------------------------------------

    [Fact]
    public void ReadOrWrite_ZeroCount_ReturnsZero()
    {
        Hardware hw = NewFsHardware();
        int fd = Open(hw, Path(hw, 5, "/f"), Create);
        Assert.Equal(0, Write(hw, fd, Buf(0), 0));
        Assert.Equal(0, Read(hw, fd, Buf(1), 0));
    }

    [Theory]
    [InlineData(0)]  // stdin
    [InlineData(1)]  // stdout
    [InlineData(8)]  // >= FdCount
    [InlineData(3)]  // in range but not open
    public void ReadOrWrite_InvalidFd_ReturnsMinusOne(int fd)
    {
        Hardware hw = NewFsHardware();
        Assert.Equal(-1, Read(hw, fd, Buf(1), 4));
        Assert.Equal(-1, Write(hw, fd, Buf(0), 4));
    }
}
