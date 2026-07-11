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

    // Blocks forever on input (no input is ever delivered to it) — a long-running background job
    // that must be killed to terminate. Blocking (rather than busy-spinning) keeps it from starving
    // the shell's CPU time, so the shell stays responsive to `jobs`/`kill`.
    private static byte[] BlockForever()
    {
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);   // blocks: nothing is ever sent to this job's stdin
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
        FsImage.WriteFile(hw, "/bin/edit", Programs.Edit());
        FsImage.WriteFile(hw, "/bin/as", Programs.As());
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
        Assert.NotNull(FindChild(binDir!, "edit"));   // §4.0 toolchain
        Assert.NotNull(FindChild(binDir!, "as"));     // §4.2 toolchain — installs despite its larger image

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

    /// <summary>
    /// The visualizer's auto-shell demos (Program.cs modes 14/15) type a scripted sequence of
    /// commands into the shell with no keyboard. This drives the same
    /// <see cref="SpectreDashboard.RunScriptedHeadless"/> / <c>DriveAutoScript</c> path headlessly and
    /// confirms each scripted command reaches the shell (only injected when it is at its INS prompt),
    /// forks/execs, and produces output.
    /// </summary>
    [Fact]
    public void AutoShell_ScriptedInput_DrivesTheShellHandsFree()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);

        List<int> ints = new List<int>();
        List<string?> strings = new List<string?>();
        // Record only — the dashboard installs its own ProgramOutput handler that acknowledges
        // output completion, so this must not also call RaiseOutputComplete (double-ack).
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (e.StringValue != null) { strings.Add(e.StringValue); } else { ints.Add(e.Value); }
        };

        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/echo", Programs.Echo());
        FsImage.WriteFile(hw, "/bin/counter", Programs.CounterToTen());
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        SpectreDashboard dashboard = new SpectreDashboard(hw, os, VisualizerMode.Normal, 0, DetailLevel.High);
        dashboard.SetAutoInputScript(new List<string> { "/bin/echo auto works", "/bin/counter" });
        dashboard.RunScriptedHeadless(500_000);

        Assert.Contains("auto", strings);    // first scripted command ran: echo argv[1]
        Assert.Contains("works", strings);   // ...and argv[2] (the whole line was delivered)
        Assert.Contains(10, ints);           // second scripted command ran: /bin/counter printed 1..10
        Assert.True(os.HasProcesses);        // the shell is still looping at its prompt
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
    public void Shell_JobsBuiltin_ListsBackgroundJob_AndKillByNumber_TerminatesIt()
    {
        // Job control (JC-B): background a long-running job, list it with the `jobs` builtin (which
        // prints the job's pid), then terminate it with `kill 1` — the killed job is reaped and
        // announced with "done ". A bad job number (`kill 10`) is ignored without harming the shell.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, List<string?> strings) = CaptureAll(hw);

        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/wait", BlockForever());
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        // Background the job (pid 2). Background jobs never steal focus, so the shell stays active.
        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.RaiseStringInputInterrupt("/bin/wait &");
        for (int i = 0; i < 8000; i++) { hw.Run(); }

        // `jobs` prints the background job's pid (2).
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("jobs");
        for (int i = 0; i < 20000 && !ints.Contains(2); i++) { hw.Run(); }
        Assert.Contains(2, ints);       // the jobs builtin listed the background pid

        // A nonexistent job number is parsed (two digits) and safely ignored.
        for (int i = 0; i < 8000; i++) { hw.Run(); }
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("kill 10");
        for (int i = 0; i < 8000; i++) { hw.Run(); }
        Assert.True(os.HasProcesses);   // shell (and the still-running job) survived the bad kill

        // `kill 1` terminates the background job; the shell reaps it and announces "done ".
        for (int i = 0; i < 8000; i++) { hw.Run(); }
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("kill 1");
        for (int i = 0; i < 40000 && !strings.Contains("done "); i++) { hw.Run(); }

        Assert.Contains("done ", strings);  // the killed background job was reaped and announced
        Assert.True(os.HasProcesses);       // the shell itself is still alive
    }

    // Reads one int from stdin, echoes it, and exits. Used to test `fg`: it blocks in the
    // background, and once foregrounded and fed an int, it completes so fg's WAIT returns.
    private static byte[] EchoInput()
    {
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);
        return asm.Build();
    }

    // Runs the machine until the process in `slot` is Blocked on the given reason, or the cap is hit.
    // A shell command costs thousands of instructions (fork + FS exec), so tests must sync on state,
    // not fixed step counts.
    private static void RunUntilBlocked(Hardware hw, int slot, WaitReason reason, int cap)
    {
        for (int i = 0; i < cap; i++)
        {
            int e = OsLayout.ProcessEntryAddress(slot);
            if (Test.ReadWord(hw, e + Hardware.ProcessEntryState) == (int)ProcessState.Blocked
                && Test.ReadWord(hw, e + Hardware.ProcessEntryWaitReason) == (int)reason)
            {
                return;
            }
            hw.Run();
        }
    }

    [Fact]
    public void Shell_StopBgFg_ManageAJobThroughItsLifecycle()
    {
        // Background a job (pid 2, slot 1, blocked on input), drive it through stop → bg (which must
        // leave it runnable again), then fg it: fg CONTinues + focuses + WAITs. Once foregrounded and
        // blocked on input, feeding it an int makes it echo (99) and exit, returning fg's WAIT.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        (List<int> ints, _) = CaptureAll(hw);

        FsImage.EnsureDir(hw, "/bin");
        FsImage.WriteFile(hw, "/bin/echoin", EchoInput());
        os.LoadProcess(new Process(hw.Disk.Store(Programs.Shell()), 1024, 128));   // shell = slot 0
        hw.SetActiveProcess(0);

        for (int i = 0; i < 3000; i++) { hw.Run(); }
        hw.RaiseStringInputInterrupt("/bin/echoin &");     // background job → pid 2 (slot 1)
        RunUntilBlocked(hw, 1, WaitReason.Input, 60000);   // wait until it blocks on IN

        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("stop 1");            // stop the blocked job
        for (int i = 0; i < 8000; i++) { hw.Run(); }
        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("bg 1");              // continue it (still blocked on IN)
        for (int i = 0; i < 8000; i++) { hw.Run(); }
        Assert.True(os.HasProcesses);                      // shell + job survived stop/bg

        hw.SetActiveProcess(0);
        hw.RaiseStringInputInterrupt("fg 1");              // foreground: CONT + focus + WAIT
        // Sync on the SHELL entering WAIT (which runs *after* fg's SetFocus), so the job is focused
        // and blocked on IN by the time we feed it — the job was already blocked, so polling on the
        // job alone would return before fg focused it.
        RunUntilBlocked(hw, 0, WaitReason.ChildProcess, 60000);
        hw.RaiseInputInterrupt(99);                        // feed the now-focused job; it echoes + exits
        for (int i = 0; i < 60000 && !ints.Contains(99); i++) { hw.Run(); }

        Assert.Contains(99, ints);                         // the foregrounded job ran to completion
        Assert.True(os.HasProcesses);                      // the shell is still alive
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
