using CSharpOSConsole.Visualization;

namespace OSTests;

/// <summary>
/// Tests for how the visualizer labels processes (PlainTextRenderer.ProcessLabel +
/// FsDiskView.NameByFirstBlock). Boot programs carry an OS-registered name; a forked/exec'd
/// process has none, so its label is resolved from the program file it is running via the
/// process entry's FirstBlock and the FS directory snapshot — that is what turns the process
/// panels from bare "p0/p1" numbers into program names like "shell" and "snake".
/// </summary>
public class ProcessNamingTests
{
    private static FsDiskView.Snapshot DiskWith(params (string Name, int FirstBlock)[] files)
    {
        FsDiskView.DiskNode root = new FsDiskView.DiskNode { Name = "/", IsDir = true, FirstBlock = 2 };
        FsDiskView.DiskNode bin = new FsDiskView.DiskNode { Name = "bin", IsDir = true, FirstBlock = 3 };
        root.Children.Add(bin);
        foreach ((string name, int first) in files)
        {
            bin.Children.Add(new FsDiskView.DiskNode { Name = name, IsDir = false, FirstBlock = first, Size = 100 });
        }
        return new FsDiskView.Snapshot { Formatted = true, Root = root };
    }

    [Fact]
    public void ProcessLabel_UsesRegisteredName_WhenPresent()
    {
        FsDiskView.Snapshot disk = DiskWith(("snake", 7));
        // The OS-registered name (e.g. the boot shell's DisplayName) wins over FS resolution.
        BuddyHeapView.ProcessRow row = new BuddyHeapView.ProcessRow { Index = 0, Path = "shell", FirstBlock = 4 };

        Assert.Equal("shell", PlainTextRenderer.ProcessLabel(row, disk));
    }

    [Fact]
    public void ProcessLabel_ResolvesExecdProgram_FromFirstBlock()
    {
        FsDiskView.Snapshot disk = DiskWith(("snake", 7), ("ls", 9));
        // A forked/exec'd process has no registered name; its FirstBlock identifies the file it runs.
        BuddyHeapView.ProcessRow snake = new BuddyHeapView.ProcessRow { Index = 1, Path = null, FirstBlock = 7 };
        BuddyHeapView.ProcessRow ls = new BuddyHeapView.ProcessRow { Index = 2, Path = null, FirstBlock = 9 };

        Assert.Equal("snake", PlainTextRenderer.ProcessLabel(snake, disk));
        Assert.Equal("ls", PlainTextRenderer.ProcessLabel(ls, disk));
    }

    [Fact]
    public void ProcessLabel_FallsBackToSlotLabel_WhenNameUnknown()
    {
        FsDiskView.Snapshot disk = DiskWith(("snake", 7));
        // No registered name and a FirstBlock not in the FS (e.g. a fork not yet exec'd into a file).
        BuddyHeapView.ProcessRow row = new BuddyHeapView.ProcessRow { Index = 3, Path = null, FirstBlock = 99 };

        Assert.Equal("p3", PlainTextRenderer.ProcessLabel(row, disk));
    }

    [Fact]
    public void ProcessLabel_FallsBackToSlotLabel_WhenSlotBackedAndNoDisk()
    {
        BuddyHeapView.ProcessRow row = new BuddyHeapView.ProcessRow { Index = 5, Path = null, FirstBlock = -1 };

        Assert.Equal("p5", PlainTextRenderer.ProcessLabel(row, null));
    }

    [Fact]
    public void NameByFirstBlock_MapsFiles_NotDirectories()
    {
        FsDiskView.Snapshot disk = DiskWith(("snake", 7), ("cat", 8));
        Dictionary<int, string> map = FsDiskView.NameByFirstBlock(disk);

        Assert.Equal("snake", map[7]);
        Assert.Equal("cat", map[8]);
        Assert.False(map.ContainsKey(2)); // root dir
        Assert.False(map.ContainsKey(3)); // /bin dir
    }
}
