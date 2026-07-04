using CSharpOS;
using CSharpOSConsole;
using CSharpOSConsole.Visualization;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers SETFOCUS (map a PID to the foreground process) and the shell program (Shell §2): the
/// shell prompts, reads a command line (INS), FORKs, the child exec-by-paths the typed command with
/// its args (FSYS Exec), and the parent SETFOCUSes the child + WAITs, then loops. Commands are
/// absolute paths; a command that does not resolve prints "?".
/// </summary>
public class ShellTests : IDisposable
{
    private static int Memory => Test.MachineWithHeap(16384);
    private readonly List<string> tempFiles = new List<string>();

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void SetFocus_MapsPidToTheForegroundProcess()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(1)), 64, 64)); // PID 1, slot 0
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(2)), 64, 64)); // PID 2, slot 1

        Assert.Equal(-1, hw.GetActiveProcess());

        hw.SetFocus(2); // focus the process with PID 2 (slot 1)
        Assert.Equal(1, hw.GetActiveProcess());

        hw.SetFocus(1); // focus PID 1 (slot 0)
        Assert.Equal(0, hw.GetActiveProcess());

        hw.SetFocus(99); // unknown PID: focus unchanged
        Assert.Equal(0, hw.GetActiveProcess());
    }

    private static (List<int> ints, List<string?> strings) CaptureAll(Hardware hw)
    {
        List<int> ints = new List<int>();
        List<string?> strings = new List<string?>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (e.StringValue != null)
            {
                strings.Add(e.StringValue);
            }
            else
            {
                ints.Add(e.Value);
            }
            hw.RaiseOutputComplete(e.Device);
        };
        return (ints, strings);
    }

    [Fact]
    public void Shell_RunsATypedCommand_ByPath()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, List<string?> strings) = CaptureAll(hw);

        // Install a command program in /bin, then load the shell.
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/p", PrintThenHalt(55));
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        // Let the (focused) shell reach its INS prompt, then type the command line; the shell
        // exec-by-paths it, becoming /bin/p, which prints 55.
        for (int i = 0; i < 3000; i++)
        {
            hw.Run();
        }
        hw.RaiseStringInputInterrupt("/bin/p");
        for (int i = 0; i < 60000 && !ints.Contains(55); i++)
        {
            hw.Run();
        }

        Assert.Contains(55, ints);      // the typed command ran via the shell's fork + exec-by-path
        Assert.True(os.HasProcesses);   // the shell itself looped, still alive
        _ = strings;
    }

    [Fact]
    public void Shell_LoopsAcrossMultipleCommands()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, _) = CaptureAll(hw);

        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/p", PrintThenHalt(55));
        FsImage.WriteFile(hw, "/bin/q", PrintThenHalt(66));
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        // First command.
        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.RaiseStringInputInterrupt("/bin/p");
        for (int i = 0; i < 60000 && !ints.Contains(55); i++) { hw.Run(); }
        Assert.Contains(55, ints);

        // Let the shell settle back to its prompt (the first child fully exits, the shell's own
        // SetFocus/WAIT completes, and it re-blocks on INS), THEN refocus it — exactly as the
        // dashboard's EnsureFocus refocuses the shell when the child terminates. Refocusing before
        // the shell has run its own SetFocus(child) would just be clobbered by it.
        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("/bin/q");
        for (int i = 0; i < 60000 && !ints.Contains(66); i++) { hw.Run(); }

        Assert.Contains(66, ints);      // the second command ran in the same shell
        Assert.True(os.HasProcesses);   // still looping
    }

    // Finds a child directory node by name under a reconstructed FS tree.
    private static FsDiskView.DiskNode? FindChild(FsDiskView.DiskNode node, string name)
    {
        foreach (FsDiskView.DiskNode child in node.Children)
        {
            if (child.Name == name)
            {
                return child;
            }
        }
        return null;
    }

    /// <summary>
    /// Reproduces the console visualizer's Shell (mode 9) path end-to-end: a machine sized like
    /// the real host (<see cref="OsLayout.TotalSize"/> + a 32 KB heap), the full /bin install, the
    /// disk-view reconstruction the dashboard runs on every FS routine, and a real typed command
    /// with arguments. Regression for the Shell §2 out-of-range crash: TotalSize grew past the
    /// host's old hardcoded 32768-byte machine, so the buddy heap started past the end of memory
    /// and the /bin install ran off the end of memory[]. Nothing here may throw.
    /// </summary>
    [Fact]
    public void Shell_InVisualizerSetup_InstallsBinRunsCommandAndRebuildsDiskView()
    {
        BasicOS os = new BasicOS(new StringWriter());
        // Mirror CSharpOSConsole Program.cs: machine memory = OS region + a 32 KB buddy heap.
        Hardware hw = new Hardware(OsLayout.TotalSize + 32768, Test.AllRegisters(), os);
        (List<int> ints, List<string?> strings) = CaptureAll(hw);

        // Exactly the RunShell install set — 9 programs + a text file — so the memory pressure
        // (and the boot-time FsImage staging near the top of the OS region) matches the host.
        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/ls", Programs.Ls());
        FsImage.WriteFile(hw, "/bin/cat", Programs.Cat());
        FsImage.WriteFile(hw, "/bin/rm", Programs.Rm());
        FsImage.WriteFile(hw, "/bin/mkdir", Programs.Mkdir());
        FsImage.WriteFile(hw, "/bin/echo", Programs.Echo());
        FsImage.WriteFile(hw, "/bin/help", Programs.Help());
        FsImage.WriteFile(hw, "/bin/counter", Programs.CounterToTen());
        FsImage.WriteFile(hw, "/bin/average", Programs.AverageOfList());
        FsImage.WriteFile(hw, "/bin/guess", Programs.GuessingGame());
        string noteText = "hello from the filesystem";
        byte[] note = new byte[noteText.Length * 4];
        for (int n = 0; n < noteText.Length; n++)
        {
            note[n * 4] = (byte)noteText[n];
        }
        FsImage.WriteFile(hw, "/note", note);

        // The dashboard's HardwareEventBridge reconstructs the disk view at boot; do the same and
        // confirm it sees the installed tree (this reconstruction path is visualizer-only, so the
        // other ShellTests never exercise it).
        FsDiskView.Snapshot? boot = FsDiskView.ReadDisk(hw);
        Assert.NotNull(boot);
        Assert.True(boot!.Formatted);
        FsDiskView.DiskNode? binDir = FindChild(boot.Root, "bin");
        Assert.NotNull(binDir);
        Assert.NotNull(FindChild(binDir!, "echo"));

        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128)); // shell = slot 0
        hw.SetActiveProcess(0);

        // Reach the prompt, type a command with arguments, and drive it to completion — rebuilding
        // the disk view along the way exactly as the bridge does on FS routines. echo prints each
        // argv[k] (k>=1) as a string, so "hi"/"there" appear in the output.
        for (int i = 0; i < 3000; i++)
        {
            hw.Run();
            FsDiskView.ReadDisk(hw);
        }
        hw.RaiseStringInputInterrupt("/bin/echo hi there");
        for (int i = 0; i < 80000 && !strings.Contains("there"); i++)
        {
            hw.Run();
            if ((i % 200) == 0)
            {
                FsDiskView.ReadDisk(hw);
            }
        }

        Assert.Contains("hi", strings);      // argv[1]
        Assert.Contains("there", strings);   // argv[2] — parsed argv survived exec
        Assert.True(os.HasProcesses);        // the shell looped and is still alive
        _ = ints;
    }

    [Fact]
    public void Shell_BackgroundJob_RunsWithoutBlockingTheShell_ThenReapsItAtTheNextPrompt()
    {
        // Job control (JC-A): a command ending in " &" runs in the background — the shell does NOT
        // WAIT for it, so it returns to the prompt immediately and stays responsive. The background
        // job still runs (prints 55), and the shell reaps it (announcing "done ") on its next loop.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, List<string?> strings) = CaptureAll(hw);

        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/p", PrintThenHalt(55));
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        // Background a job. A background job never steals focus, so the shell stays the foreground
        // process (active 0) the whole time — no refocus dance is needed between commands.
        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.RaiseStringInputInterrupt("/bin/p &");
        for (int i = 0; i < 60000 && !ints.Contains(55); i++) { hw.Run(); }
        Assert.Contains(55, ints);        // the background job ran even though the shell didn't wait

        // The shell stayed responsive: send a second command. Its next loop's REAP drain collects the
        // finished first job and prints "done ".
        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("/bin/p &");
        for (int i = 0; i < 60000 && !strings.Contains("done "); i++) { hw.Run(); }

        Assert.Contains("done ", strings); // the finished background job was reaped and announced
        Assert.True(os.HasProcesses);      // the shell is still alive and looping
    }

    [Fact]
    public void Shell_UnknownCommand_PrintsErrorAndSurvives()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (_, List<string?> strings) = CaptureAll(hw);

        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));
        hw.SetActiveProcess(0);

        for (int i = 0; i < 3000; i++)
        {
            hw.Run();
        }
        hw.RaiseStringInputInterrupt("/bin/nope");
        for (int i = 0; i < 60000 && !strings.Contains("?"); i++)
        {
            hw.Run();
        }

        Assert.Contains("?", strings);   // the child reported the failed exec
        Assert.True(os.HasProcesses);    // the shell survived and looped
    }
}
