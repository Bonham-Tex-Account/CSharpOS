using CSharpOS;

namespace CSharpOSConsole;

/// <summary>
/// Builds the demo programs as assembled byte images.
/// </summary>
public static class Programs
{
    // ===== CounterToTen ======================================================
    // Counts 1..10, printing each value, then halts.
    public static byte[] CounterToTen()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 0);    // counter
        asm.MovImm(RegisterName.EBX, 10);   // limit
        asm.Label("loop");
        asm.Inc(RegisterName.EAX);          // counter++
        asm.Out(RegisterName.EAX);          // print counter
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("loop");                    // repeat until counter == 10
        asm.Hlt();
        return asm.Build();
    }

    // ===== AverageOfList =====================================================
    // Builds the list [10, 20, 30, 40] in memory, sums it, prints the average (25).
    public static byte[] AverageOfList()
    {
        Assembler asm = new Assembler();

        asm.MovImm(RegisterName.ESI, 4);          // ESI = element stride (4 bytes)

        // --- write the list into the data area ---
        asm.MovImmLabel(RegisterName.EBX, "data"); // EBX = data pointer
        asm.MovImm(RegisterName.EAX, 10);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 20);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 30);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Add(RegisterName.EBX, RegisterName.ESI);
        asm.MovImm(RegisterName.EAX, 40);
        asm.Store(RegisterName.EBX, RegisterName.EAX);

        // --- sum the list ---
        asm.MovImmLabel(RegisterName.EBX, "data"); // reset pointer
        asm.MovImm(RegisterName.ECX, 0);           // sum
        asm.MovImm(RegisterName.EDX, 4);           // remaining count
        asm.Label("sum");
        asm.Load(RegisterName.EAX, RegisterName.EBX);  // EAX = *ptr
        asm.Add(RegisterName.ECX, RegisterName.EAX);   // sum += EAX
        asm.Add(RegisterName.EBX, RegisterName.ESI);   // ptr += 4
        asm.Dec(RegisterName.EDX);                     // count--
        asm.Jnz("sum");

        // --- average = sum / 4 ---
        asm.MovImm(RegisterName.EDI, 4);
        asm.Mov(RegisterName.EAX, RegisterName.ECX);
        asm.Div(RegisterName.EAX, RegisterName.EDI);
        asm.Out(RegisterName.EAX);                 // print average
        asm.Hlt();

        asm.DataInt("data");   // slot 0
        asm.DataInt("d1");     // slot 1
        asm.DataInt("d2");     // slot 2
        asm.DataInt("d3");     // slot 3
        return asm.Build();
    }

    // ===== BusyThenHalt ======================================================
    // Busy-works for `iterations` loop turns (a countdown), then prints `printValue`
    // and halts. Non-interactive and self-terminating, so it is ideal for memory-churn
    // demos: several can coexist (filling the buddy heap) and then drain as they finish.
    // Both arguments must fit in a byte (0..255).
    public static byte[] BusyThenHalt(int iterations, int printValue)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, iterations);
        asm.Label("spin");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin");
        asm.MovImm(RegisterName.EAX, printValue);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // ===== FilesystemDemo ====================================================
    // A filesystem demo (Phase 4): the process itself uses the FSYS syscall to create a file
    // "/note", write the string "HI!" into it, close and reopen it, read the bytes back into a
    // separate buffer, and OUTS the result to the screen — proving the FSYS open/write/close/
    // read path end to end from a live user process. (Boot itself already loads every program
    // from the filesystem now; this makes the FS visible in the Screen panel.) All buffers live
    // in the program image's first page, which is RAM-home, and the read destination is a fresh
    // buffer so the printed "HI!" can only have come from the file, not stale memory.
    public static byte[] FilesystemDemo()
    {
        const int PathOff = 128;   // "/note"
        const int DataOff = 160;   // "HI!" source (3 chars, word-per-char)
        const int ReadOff = 192;   // read destination (distinct, never pre-filled)

        Assembler asm = new Assembler();
        // open("/note", create) → fd in EAX, kept in EBX across the later syscalls.
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.MovImm16(RegisterName.EBX, PathOff);
        asm.MovImm(RegisterName.ECX, Hardware.FsysCreateFlag);
        asm.Fsys();
        asm.Mov(RegisterName.EBX, RegisterName.EAX);
        // write("HI!", 3)
        asm.MovImm16(RegisterName.ECX, DataOff);
        asm.MovImm(RegisterName.EDX, 3);
        asm.MovImm(RegisterName.EAX, Hardware.FsysWrite);
        asm.Fsys();
        // close(fd)
        asm.MovImm(RegisterName.EAX, Hardware.FsysClose);
        asm.Fsys();
        // reopen("/note") → fd (offset back to 0)
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.MovImm16(RegisterName.EBX, PathOff);
        asm.MovImm(RegisterName.ECX, 0);
        asm.Fsys();
        asm.Mov(RegisterName.EBX, RegisterName.EAX);
        // read(3) into ReadOff
        asm.MovImm16(RegisterName.ECX, ReadOff);
        asm.MovImm(RegisterName.EDX, 3);
        asm.MovImm(RegisterName.EAX, Hardware.FsysRead);
        asm.Fsys();
        // print what came back
        asm.MovImm16(RegisterName.EAX, ReadOff);
        asm.MovImm(RegisterName.ECX, 3);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        byte[] code = asm.Build();

        byte[] image = new byte[ReadOff + 3 * 4];
        Array.Copy(code, image, code.Length);
        image[PathOff]      = (byte)'/';
        image[PathOff + 4]  = (byte)'n';
        image[PathOff + 8]  = (byte)'o';
        image[PathOff + 12] = (byte)'t';
        image[PathOff + 16] = (byte)'e';
        // PathOff + 20 stays 0 → null terminator
        image[DataOff]      = (byte)'H';
        image[DataOff + 4]  = (byte)'I';
        image[DataOff + 8]  = (byte)'!';
        return image;
    }

    // ---- /bin command programs (Shell §2) --------------------------------
    // These are shipped as real FS programs under /bin and exec'd by the shell like any other
    // program. Each receives argv the standard way: at entry EAX = argc, EBX = argv base (virtual);
    // argv[k] = *(argvBase + k*4) is a virtual pointer to a null-terminated word-per-char string.
    // argv[0] is the command as typed. Path arguments (argv[1]) live in the RAM-home argv
    // reservation, so passing them straight to the FSYS path syscalls (open/unlink/mkdir/readdir)
    // is correct despite those syscalls using flat ProgramAddress+ptr translation.

    // Writes `s` into `image` at byte offset `off`, one character per 4-byte word (the OUTS/INS /
    // path convention). Leaves the following word as the null terminator (arrays start zeroed).
    private static void WriteString(byte[] image, int off, string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            image[off + i * 4] = (byte)s[i];
        }
    }

    // ===== Echo ==============================================================
    // echo: print each argument (argv[1..]) back, one per output. The clearest argv demo.
    public static byte[] Echo()
    {
        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);    // ESI = argv base
        asm.Mov(RegisterName.EDI, RegisterName.EAX);    // EDI = argc
        asm.MovImm(RegisterName.ECX, 1);                // k = 1 (skip argv[0] = the command name)
        asm.Label("echo_loop");
        asm.Cmp(RegisterName.ECX, RegisterName.EDI);
        asm.Jns("echo_done");                           // k >= argc → done
        asm.Mov(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm(RegisterName.EDX, 4);
        asm.Mul(RegisterName.EAX, RegisterName.EDX);    // k*4
        asm.Add(RegisterName.EAX, RegisterName.ESI);    // &argv[k]
        asm.Load(RegisterName.EAX, RegisterName.EAX);   // argv[k] (virtual string pointer)
        asm.MovImm(RegisterName.EDX, 20);               // max words (OUTS stops at the null)
        asm.Outs(RegisterName.EAX, RegisterName.EDX);
        asm.Inc(RegisterName.ECX);
        asm.Jmp("echo_loop");
        asm.Label("echo_done");
        asm.Hlt();
        return asm.Build();
    }

    // ===== Rm ================================================================
    // rm <file>: delete a file via FsysUnlink(argv[1]).
    public static byte[] Rm()
    {
        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);    // argv base
        asm.MovImm(RegisterName.EDX, 2);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("rm_exit");                              // argc < 2 → nothing to remove
        asm.MovImm(RegisterName.EAX, 4);
        asm.Add(RegisterName.EAX, RegisterName.ESI);
        asm.Load(RegisterName.EBX, RegisterName.EAX);   // EBX = argv[1] (path)
        asm.MovImm(RegisterName.EAX, Hardware.FsysUnlink);
        asm.Fsys();
        asm.Label("rm_exit");
        asm.Hlt();
        return asm.Build();
    }

    // ===== Mkdir =============================================================
    // mkdir <dir>: create a directory via FsysMkdir(argv[1]).
    public static byte[] Mkdir()
    {
        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);    // argv base
        asm.MovImm(RegisterName.EDX, 2);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("mkdir_exit");                           // argc < 2 → nothing to make
        asm.MovImm(RegisterName.EAX, 4);
        asm.Add(RegisterName.EAX, RegisterName.ESI);
        asm.Load(RegisterName.EBX, RegisterName.EAX);   // EBX = argv[1] (path)
        asm.MovImm(RegisterName.EAX, Hardware.FsysMkdir);
        asm.Fsys();
        asm.Label("mkdir_exit");
        asm.Hlt();
        return asm.Build();
    }

    // ===== Cat ===============================================================
    // cat <file>: print a file's contents. Reads in fixed chunks into a RAM-home image buffer and
    // OUTS each chunk until read returns 0 (EOF). (FSYS read buffers are page-translated, so a
    // RAM-home image buffer is fine; the path in argv[1] is likewise RAM-home for FsysOpen.)
    public static byte[] Cat()
    {
        const int ReadBuf = 256;     // RAM-home buffer, clear of the code
        const int ReadChunk = 32;    // chars (words) per read

        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);    // argv base
        asm.MovImm(RegisterName.EDX, 2);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("cat_exit");                             // argc < 2 → nothing to print
        asm.MovImm(RegisterName.EAX, 4);
        asm.Add(RegisterName.EAX, RegisterName.ESI);
        asm.Load(RegisterName.EDI, RegisterName.EAX);   // EDI = argv[1] (path)
        // open(path, 0)
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.Mov(RegisterName.EBX, RegisterName.EDI);
        asm.MovImm(RegisterName.ECX, 0);
        asm.Fsys();
        asm.MovImm(RegisterName.EDX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("cat_exit");                             // open failed
        asm.Mov(RegisterName.EBX, RegisterName.EAX);    // EBX = fd (preserved across FSYS/OUTS)
        asm.Label("cat_loop");
        asm.MovImm(RegisterName.EAX, Hardware.FsysRead);
        asm.MovImm16(RegisterName.ECX, ReadBuf);
        asm.MovImm(RegisterName.EDX, ReadChunk);
        asm.Fsys();                                     // EAX = chars read (0 = EOF, -1 = error)
        asm.MovImm(RegisterName.EDX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Jz("cat_close");
        asm.Js("cat_close");
        asm.Mov(RegisterName.EDX, RegisterName.EAX);    // len = chars read
        asm.MovImm16(RegisterName.EAX, ReadBuf);
        asm.Outs(RegisterName.EAX, RegisterName.EDX);
        asm.Jmp("cat_loop");
        asm.Label("cat_close");
        asm.MovImm(RegisterName.EAX, Hardware.FsysClose);
        asm.Fsys();
        asm.Label("cat_exit");
        asm.Hlt();
        byte[] code = asm.Build();
        byte[] image = new byte[ReadBuf + ReadChunk * 4];
        Array.Copy(code, image, code.Length);
        return image;
    }

    // ===== Ls ================================================================
    // ls [dir]: list a directory's entries (defaults to "/"). Walks FsysReaddir by index until it
    // returns -1, printing each entry's name. The out buffer + the default "/" path are RAM-home
    // image buffers (FsysReaddir uses flat path/out translation).
    public static byte[] Ls()
    {
        const int RootOff = 128;   // the default "/" path (word-per-char)
        const int OutBuf = 192;    // 64-byte readdir entry lands here (RAM-home)

        Assembler asm = new Assembler();
        asm.Mov(RegisterName.ESI, RegisterName.EBX);    // argv base
        asm.MovImm16(RegisterName.EDI, RootOff);        // EDI = dir path ptr (default "/")
        asm.MovImm(RegisterName.EDX, 2);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("ls_have_dir");                          // argc < 2 → keep the default
        asm.MovImm(RegisterName.EAX, 4);
        asm.Add(RegisterName.EAX, RegisterName.ESI);
        asm.Load(RegisterName.EDI, RegisterName.EAX);   // EDI = argv[1] (dir path)
        asm.Label("ls_have_dir");
        asm.MovImm(RegisterName.ECX, 0);                // i = entry index (preserved across FSYS)
        asm.Label("ls_loop");
        asm.Mov(RegisterName.EBX, RegisterName.EDI);    // dir path ptr
        asm.MovImm16(RegisterName.EDX, OutBuf);
        asm.MovImm(RegisterName.EAX, Hardware.FsysReaddir);
        asm.Fsys();                                     // EAX = entry type or -1 (past end)
        asm.MovImm(RegisterName.EDX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EDX);
        asm.Js("ls_done");                              // -1 → done
        asm.MovImm16(RegisterName.EAX, OutBuf + FsLayout.DirEntryName);
        asm.MovImm(RegisterName.EDX, FsLayout.NameMaxChars);
        asm.Outs(RegisterName.EAX, RegisterName.EDX);   // print the entry name
        asm.Inc(RegisterName.ECX);
        asm.Jmp("ls_loop");
        asm.Label("ls_done");
        asm.Hlt();
        byte[] code = asm.Build();
        byte[] image = new byte[OutBuf + FsLayout.DirEntryBytes];
        Array.Copy(code, image, code.Length);
        WriteString(image, RootOff, "/");
        return image;
    }

    // ===== Help ==============================================================
    // help: print the list of available commands.
    public static byte[] Help()
    {
        const int MsgOff = 64;
        const string Msg = "cmds: ls cat rm mkdir echo help";

        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, MsgOff);
        asm.MovImm(RegisterName.ECX, Msg.Length);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        byte[] code = asm.Build();
        byte[] image = new byte[MsgOff + (Msg.Length + 1) * 4];
        Array.Copy(code, image, code.Length);
        WriteString(image, MsgOff, Msg);
        return image;
    }

    // ===== Shell =============================================================
    // A real command shell (Shell §2 + §2.5 job control): prompt, read a command line (INS), fork,
    // have the child exec-by-path the typed command with its arguments (FSYS Exec). If the line ends
    // with " &" the job runs in the BACKGROUND — the parent records nothing to wait on and returns to
    // the prompt immediately; otherwise (foreground) the parent focuses the child and WAITs. At the
    // top of every loop the parent drains finished background jobs with REAP (non-blocking), printing
    // "done " for each. v1 uses absolute paths (no CWD): "/bin/ls /", "/bin/cat /note", "/bin/counter &".
    // A command that does not resolve prints "?".
    //
    // The parent reads the line (it is the focused foreground process, so keyboard input reaches it)
    // into a DATA-region buffer, then forks: the child inherits the buffer and execs it. This relies
    // on FORK propagating the parent's just-typed line to the child — which works now that
    // kernel-mediated writes (INS) mark their frame dirty so fork's flush_frames carries them.
    public static byte[] Shell()
    {
        // Image-resident strings live above the code (which is < StringBase bytes); the line buffer
        // and the jobs table live in the DATA region (needs memory >= 1024).
        // Each string gets a 32-byte slot so none overlaps the next (word-per-char: a 5-char string
        // spans 20 bytes). "done " ran into "jobs" at 16-byte spacing, printing "donej".
        const int PromptOff   = 1024; // "$ " prompt (just above the code; guarded below)
        const int ErrOff      = 1056; // "?" printed when a command fails to exec
        const int DoneOff     = 1088; // "done " printed when a background job is reaped
        const int JobsCmdOff  = 1120; // "jobs" builtin name
        const int KillCmdOff  = 1152; // "kill" builtin name
        const int StopCmdOff  = 1184; // "stop" builtin name
        const int BgCmdOff    = 1216; // "bg" builtin name
        const int FgCmdOff    = 1248; // "fg" builtin name
        const int StringBase  = PromptOff;
        const int LineBuf     = 1408; // typed command line (DATA; past the ~1280-byte image)
        const int LineMax     = 32;   // buffer capacity in words
        const int JobsBase    = 1664; // background jobs table (DATA): MaxJobs words of pid (0 = empty)
        const int MaxJobs     = 8;
        const int AmpChar     = 0x26; // '&'
        const int SpaceChar   = 0x20;
        const int DigitZero   = 0x30; // '0'
        const int DigitNine   = 0x39; // '9'

        Assembler asm = new Assembler();
        asm.Label("loop");
        // Drain finished background jobs (non-blocking): reap each, clear its jobs-table slot, and
        // announce it with "done ".
        asm.Label("drain");
        asm.MovImm(RegisterName.EAX, 0);                // target = any child
        asm.Reap(RegisterName.EAX);                     // EAX = reaped pid (0 when none dead)
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("drained");
        asm.Mov(RegisterName.R8, RegisterName.EAX);     // R8 = reaped pid
        asm.Call("job_clear");                          // clear its jobs-table slot
        asm.MovImm16(RegisterName.EAX, DoneOff);
        asm.MovImm(RegisterName.ECX, 5);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Jmp("drain");
        asm.Label("drained");
        // Prompt + read a command line.
        asm.MovImm16(RegisterName.EAX, PromptOff);
        asm.MovImm(RegisterName.ECX, 2);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, LineBuf);
        asm.MovImm(RegisterName.ECX, LineMax);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);
        // Builtin dispatch: shell-internal commands act on the jobs table (they cannot be /bin
        // programs). Compare the first token against "jobs" / "kill".
        asm.MovImm16(RegisterName.R14, JobsCmdOff);
        asm.Call("cmd_is");
        asm.MovImm(RegisterName.EBX, 1);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("do_jobs");
        asm.MovImm16(RegisterName.R14, KillCmdOff);
        asm.Call("cmd_is");
        asm.MovImm(RegisterName.EBX, 1);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("do_kill");
        asm.MovImm16(RegisterName.R14, StopCmdOff);
        asm.Call("cmd_is");
        asm.MovImm(RegisterName.EBX, 1);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("do_stop");
        asm.MovImm16(RegisterName.R14, BgCmdOff);
        asm.Call("cmd_is");
        asm.MovImm(RegisterName.EBX, 1);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("do_bg");
        asm.MovImm16(RegisterName.R14, FgCmdOff);
        asm.Call("cmd_is");
        asm.MovImm(RegisterName.EBX, 1);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("do_fg");
        // Scan for a trailing '&' → background flag R8; truncate the line there.
        asm.MovImm(RegisterName.R8, 0);
        asm.MovImm16(RegisterName.ESI, LineBuf);
        asm.MovImm(RegisterName.R9, 0);
        asm.Label("amp");
        asm.MovImm(RegisterName.R10, LineMax);
        asm.Cmp(RegisterName.R9, RegisterName.R10);
        asm.Jns("amp_end");
        asm.Load(RegisterName.R11, RegisterName.ESI);
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("amp_end");
        asm.MovImm(RegisterName.R12, AmpChar);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jnz("amp_next");
        asm.MovImm(RegisterName.R11, 0);
        asm.Store(RegisterName.ESI, RegisterName.R11);
        asm.MovImm(RegisterName.R8, 1);
        asm.Jmp("amp_end");
        asm.Label("amp_next");
        asm.MovImm(RegisterName.R12, 4);
        asm.Add(RegisterName.ESI, RegisterName.R12);
        asm.Inc(RegisterName.R9);
        asm.Jmp("amp");
        asm.Label("amp_end");
        // Fork; the child execs the (possibly '&'-stripped) command line.
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent");
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, LineBuf);
        asm.Fsys();
        asm.MovImm16(RegisterName.EAX, ErrOff);
        asm.MovImm(RegisterName.ECX, 1);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);
        asm.Label("parent");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);    // ESI = child pid
        asm.MovImm(RegisterName.R9, 0);
        asm.Cmp(RegisterName.R8, RegisterName.R9);
        asm.Jz("fg");                                   // R8 == 0 → foreground
        // Background: record the child pid in a free jobs-table slot, then loop (no wait/focus).
        asm.MovImm16(RegisterName.R9, JobsBase);
        asm.MovImm(RegisterName.R10, 0);
        asm.Label("rec_find");
        asm.MovImm(RegisterName.R11, MaxJobs);
        asm.Cmp(RegisterName.R10, RegisterName.R11);
        asm.Jns("loop");                                // table full: still reaped later via REAP(any)
        asm.Load(RegisterName.R11, RegisterName.R9);
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("rec_store");
        asm.MovImm(RegisterName.R11, 4);
        asm.Add(RegisterName.R9, RegisterName.R11);
        asm.Inc(RegisterName.R10);
        asm.Jmp("rec_find");
        asm.Label("rec_store");
        asm.Store(RegisterName.R9, RegisterName.ESI);   // slot = child pid
        asm.Jmp("loop");
        asm.Label("fg");
        asm.SetFocus(RegisterName.ESI);
        asm.Wait(RegisterName.ESI);
        asm.Jmp("loop");

        // do_jobs: print each active background job's pid (as an int).
        asm.Label("do_jobs");
        asm.MovImm16(RegisterName.R9, JobsBase);
        asm.MovImm(RegisterName.R10, 0);
        asm.Label("jobs_loop");
        asm.MovImm(RegisterName.R11, MaxJobs);
        asm.Cmp(RegisterName.R10, RegisterName.R11);
        asm.Jns("loop");
        asm.Load(RegisterName.R11, RegisterName.R9);
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("jobs_next");
        asm.Out(RegisterName.R11);                      // print the job's pid
        asm.Label("jobs_next");
        asm.MovImm(RegisterName.R12, 4);
        asm.Add(RegisterName.R9, RegisterName.R12);
        asm.Inc(RegisterName.R10);
        asm.Jmp("jobs_loop");

        // Job-control builtins, all "cmd <n>": resolve the job number to a pid (job_lookup), then
        // signal it. kill = terminate, stop = SIGSTOP, bg = SIGCONT (leave unfocused), fg = SIGCONT +
        // focus + WAIT (and clear the slot once the job actually exits).
        asm.Label("do_kill");
        asm.Call("job_lookup");                         // ESI = pid (0 = no such job)
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.ESI, RegisterName.R12);
        asm.Jz("loop");
        asm.MovImm(RegisterName.EDX, Hardware.SigTerm);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);
        asm.Jmp("loop");

        asm.Label("do_stop");
        asm.Call("job_lookup");
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.ESI, RegisterName.R12);
        asm.Jz("loop");
        asm.MovImm(RegisterName.EDX, Hardware.SigStop);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);
        asm.Jmp("loop");

        asm.Label("do_bg");
        asm.Call("job_lookup");
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.ESI, RegisterName.R12);
        asm.Jz("loop");
        asm.MovImm(RegisterName.EDX, Hardware.SigCont);
        asm.Kill(RegisterName.ESI, RegisterName.EDX);   // resume in the background (unfocused)
        asm.Jmp("loop");

        asm.Label("do_fg");
        asm.Call("job_lookup");
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.ESI, RegisterName.R12);
        asm.Jz("loop");
        asm.Mov(RegisterName.R15, RegisterName.ESI);    // R15 = pid (survives Kill/SetFocus/Wait)
        asm.MovImm(RegisterName.EDX, Hardware.SigCont);
        asm.Kill(RegisterName.R15, RegisterName.EDX);   // resume if stopped (harmless if already running)
        asm.SetFocus(RegisterName.R15);
        asm.Wait(RegisterName.R15);                     // EAX = status (-2 if it stops again)
        asm.MovImm(RegisterName.R14, 0);
        asm.Dec(RegisterName.R14);
        asm.Dec(RegisterName.R14);                      // R14 = -2 (stopped)
        asm.Cmp(RegisterName.EAX, RegisterName.R14);
        asm.Jz("loop");                                 // stopped again → keep the job in the table
        asm.Mov(RegisterName.R8, RegisterName.R15);
        asm.Call("job_clear");                          // exited → drop it from the jobs table
        asm.Jmp("loop");

        // cmd_is: EAX = 1 if LineBuf's first token equals the null-terminated const string at R14,
        // else 0. A token ends at a space or null. Clobbers EAX/R10-R14. CALL/RET (user stack).
        asm.Label("cmd_is");
        asm.MovImm16(RegisterName.R13, LineBuf);
        asm.Label("ci_loop");
        asm.Load(RegisterName.R11, RegisterName.R14);   // const char
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("ci_constend");
        asm.Load(RegisterName.R10, RegisterName.R13);   // line char
        asm.Cmp(RegisterName.R10, RegisterName.R11);
        asm.Jnz("ci_no");
        asm.MovImm(RegisterName.R12, 4);
        asm.Add(RegisterName.R13, RegisterName.R12);
        asm.Add(RegisterName.R14, RegisterName.R12);
        asm.Jmp("ci_loop");
        asm.Label("ci_constend");
        asm.Load(RegisterName.R10, RegisterName.R13);   // line char just past the token
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R10, RegisterName.R12);
        asm.Jz("ci_yes");
        asm.MovImm(RegisterName.R12, SpaceChar);
        asm.Cmp(RegisterName.R10, RegisterName.R12);
        asm.Jz("ci_yes");
        asm.Label("ci_no");
        asm.MovImm(RegisterName.EAX, 0);
        asm.Ret();
        asm.Label("ci_yes");
        asm.MovImm(RegisterName.EAX, 1);
        asm.Ret();

        // parse_uint: R13 = pointer to the first digit (word-per-char); EAX = accumulated value.
        // Stops at the first non-digit. Clobbers EAX/R10/R11/R13. CALL/RET.
        asm.Label("parse_uint");
        asm.MovImm(RegisterName.EAX, 0);
        asm.Label("pu_loop");
        asm.Load(RegisterName.R11, RegisterName.R13);
        asm.MovImm(RegisterName.R10, DigitZero);
        asm.Cmp(RegisterName.R11, RegisterName.R10);
        asm.Js("pu_done");                              // char < '0'
        asm.MovImm(RegisterName.R10, DigitNine);
        asm.Cmp(RegisterName.R10, RegisterName.R11);
        asm.Js("pu_done");                              // char > '9'
        asm.MovImm(RegisterName.R10, 10);
        asm.Mul(RegisterName.EAX, RegisterName.R10);    // EAX *= 10
        asm.MovImm(RegisterName.R10, DigitZero);
        asm.Sub(RegisterName.R11, RegisterName.R10);    // digit value
        asm.Add(RegisterName.EAX, RegisterName.R11);
        asm.MovImm(RegisterName.R10, 4);
        asm.Add(RegisterName.R13, RegisterName.R10);
        asm.Jmp("pu_loop");
        asm.Label("pu_done");
        asm.Ret();

        // job_lookup: parse LineBuf's job-number argument ("cmd <n>") → ESI = the job's pid, or 0 if
        // there is no argument / n is out of range / that slot is empty. CALL/RET (calls parse_uint).
        asm.Label("job_lookup");
        asm.MovImm16(RegisterName.R13, LineBuf);
        asm.Label("jl_tospace");
        asm.Load(RegisterName.R11, RegisterName.R13);
        asm.MovImm(RegisterName.R12, 0);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("jl_none");                              // no argument
        asm.MovImm(RegisterName.R12, SpaceChar);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("jl_skipsp");
        asm.MovImm(RegisterName.R12, 4);
        asm.Add(RegisterName.R13, RegisterName.R12);
        asm.Jmp("jl_tospace");
        asm.Label("jl_skipsp");
        asm.MovImm(RegisterName.R12, 4);
        asm.Add(RegisterName.R13, RegisterName.R12);    // step over the space
        asm.Load(RegisterName.R11, RegisterName.R13);
        asm.MovImm(RegisterName.R12, SpaceChar);
        asm.Cmp(RegisterName.R11, RegisterName.R12);
        asm.Jz("jl_skipsp");                            // skip extra spaces
        asm.Call("parse_uint");                         // R13 → EAX = n
        asm.MovImm(RegisterName.R12, 1);
        asm.Sub(RegisterName.EAX, RegisterName.R12);    // n - 1 (job numbers are 1-based)
        asm.Js("jl_none");
        asm.MovImm(RegisterName.R12, MaxJobs);
        asm.Cmp(RegisterName.EAX, RegisterName.R12);
        asm.Jns("jl_none");
        asm.MovImm(RegisterName.R12, 4);
        asm.Mul(RegisterName.EAX, RegisterName.R12);
        asm.MovImm16(RegisterName.R12, JobsBase);
        asm.Add(RegisterName.EAX, RegisterName.R12);    // &slot
        asm.Load(RegisterName.ESI, RegisterName.EAX);   // ESI = slot pid (0 if empty)
        asm.Ret();
        asm.Label("jl_none");
        asm.MovImm(RegisterName.ESI, 0);
        asm.Ret();

        // job_clear: R8 = pid; zero the jobs-table slot holding it (if any). CALL/RET. Clobbers R9-R11.
        asm.Label("job_clear");
        asm.MovImm16(RegisterName.R9, JobsBase);
        asm.MovImm(RegisterName.R10, 0);
        asm.Label("jc_find");
        asm.MovImm(RegisterName.R11, MaxJobs);
        asm.Cmp(RegisterName.R10, RegisterName.R11);
        asm.Jns("jc_done");
        asm.Load(RegisterName.R11, RegisterName.R9);
        asm.Cmp(RegisterName.R11, RegisterName.R8);
        asm.Jnz("jc_next");
        asm.MovImm(RegisterName.R11, 0);
        asm.Store(RegisterName.R9, RegisterName.R11);
        asm.Jmp("jc_done");
        asm.Label("jc_next");
        asm.MovImm(RegisterName.R11, 4);
        asm.Add(RegisterName.R9, RegisterName.R11);
        asm.Inc(RegisterName.R10);
        asm.Jmp("jc_find");
        asm.Label("jc_done");
        asm.Ret();

        byte[] code = asm.Build();
        if (code.Length > StringBase)
        {
            throw new InvalidOperationException(
                $"Shell code ({code.Length} bytes) overruns the image string area at {StringBase}; raise the string offsets.");
        }

        byte[] image = new byte[FgCmdOff + 8 * 4];
        Array.Copy(code, image, code.Length);
        WriteString(image, PromptOff, "$ ");
        WriteString(image, ErrOff, "?");
        WriteString(image, DoneOff, "done ");
        WriteString(image, JobsCmdOff, "jobs");
        WriteString(image, KillCmdOff, "kill");
        WriteString(image, StopCmdOff, "stop");
        WriteString(image, BgCmdOff, "bg");
        WriteString(image, FgCmdOff, "fg");
        return image;
    }

    // ===== Snake =============================================================
    // Snake (Visualizer §3): a full playable game rendered as a text grid. Arrow keys steer (INPOLL,
    // non-blocking), 'q' quits; eating food ('*') grows the snake ('O'); hitting a wall ('#') or itself
    // ends the game. Each tick the WHOLE grid is drawn as one OUTS string (rows split by '\n'), which
    // the dashboard's Screen canvas mode shows in place. The body uses the "life-countdown" scheme: a
    // grid cell holds ticks-until-vacate (head = length, tail = 1); a normal tick decrements every
    // snake cell (tail vacates) then sets the new head to length; a grow tick SKIPS the decrement and
    // bumps length, so the snake lengthens. Launch as /bin/snake; killable/Ctrl-C-able via job control.
    // Needs a roomy DATA region (grid + render buffer live past the image at 2048+), so the launching
    // shell is loaded with extra memory (exec preserves RequiredMemory).
    public static byte[] Snake()
    {
        const int W = 8;             // grid width  (power of two → AND-mask for random food, W<<-shift index)
        const int H = 8;             // grid height (power of two). Small so the grid + render buffer
                                     // working set fits the 4-frame page pool — 16×8 thrashed badly
                                     // (≈1 tick per 65k steps); 8×8 keeps it responsive.
        const int Cells = W * H;
        int WShift = System.Numerics.BitOperations.Log2((uint)W);   // idx = y*W + x  →  y << WShift
        // Image-resident: the game-over line lives above the code (guarded below).
        const int OverOff = 1536;    // "GAME OVER\n"
        // DATA (virtual, past the ~1.2 KB image; the process is loaded with ample memory).
        const int HX = 2048, HY = 2052, DX = 2056, DY = 2060, LEN = 2064;
        const int FX = 2068, FY = 2072, SCORE = 2076, RNG = 2080;
        const int GRID = 2112;               // Cells words
        const int REND = GRID + Cells * 4;   // render-string buffer
        const int Wall = 0x23, Body = 0x4F, FoodCh = 0x2A, Empty = 0x2E, Nl = 0x0A; // # O * . \n
        const int QuitKey = 0x71;            // 'q'

        Assembler asm = new Assembler();

        // ---- small emit helpers (keep the game code readable) ----
        void Ld(int addr, RegisterName dst) { asm.MovImm16(RegisterName.EAX, addr); asm.Load(dst, RegisterName.EAX); }
        void StR(int addr, RegisterName src) { asm.MovImm16(RegisterName.EAX, addr); asm.Store(RegisterName.EAX, src); }
        void StI(int addr, int val)
        {
            asm.MovImm16(RegisterName.EAX, addr);
            if (val == -1) { asm.MovImm(RegisterName.EBX, 0); asm.Dec(RegisterName.EBX); }
            else if (val >= 0 && val <= 255) { asm.MovImm(RegisterName.EBX, val); }
            else { asm.MovImm16(RegisterName.EBX, val); }
            asm.Store(RegisterName.EAX, RegisterName.EBX);
        }
        // Store R11 (a char) at the render pointer R13, then advance R13 by one word.
        void EmitR11() { asm.Store(RegisterName.R13, RegisterName.R11); asm.MovImm(RegisterName.R12, 4); asm.Add(RegisterName.R13, RegisterName.R12); }

        // ---- INIT ----
        // Clear the grid.
        asm.MovImm16(RegisterName.R8, GRID);
        asm.MovImm(RegisterName.R9, 0);
        asm.Label("clr");
        asm.MovImm(RegisterName.R10, Cells);
        asm.Cmp(RegisterName.R9, RegisterName.R10);
        asm.Jns("clr_done");
        asm.MovImm(RegisterName.R11, 0);
        asm.Store(RegisterName.R8, RegisterName.R11);
        asm.MovImm(RegisterName.R10, 4);
        asm.Add(RegisterName.R8, RegisterName.R10);
        asm.Inc(RegisterName.R9);
        asm.Jmp("clr");
        asm.Label("clr_done");
        StI(HX, W / 2); StI(HY, H / 2); StI(DX, 1); StI(DY, 0); StI(LEN, 3); StI(SCORE, 0); StI(RNG, 12345);
        // Initial 3-cell snake pointing right at the centre: head (W/2,H/2)=3, then 2, 1.
        StI(GRID + ((H / 2) * W + (W / 2)) * 4, 3);
        StI(GRID + ((H / 2) * W + (W / 2 - 1)) * 4, 2);
        StI(GRID + ((H / 2) * W + (W / 2 - 2)) * 4, 1);
        asm.Call("place_food");

        // ---- MAIN LOOP ----
        asm.Label("main");
        asm.Call("render");
        // Non-blocking input.
        asm.InkPoll(RegisterName.R8);                 // R8 = keycode, or -1 if none
        asm.MovImm(RegisterName.R10, QuitKey);
        asm.Cmp(RegisterName.R8, RegisterName.R10);
        asm.Jz("quit");
        asm.MovImm16(RegisterName.R10, Hardware.KeyUp);
        asm.Cmp(RegisterName.R8, RegisterName.R10);
        asm.Jz("k_up");
        asm.MovImm16(RegisterName.R10, Hardware.KeyDown);
        asm.Cmp(RegisterName.R8, RegisterName.R10);
        asm.Jz("k_down");
        asm.MovImm16(RegisterName.R10, Hardware.KeyLeft);
        asm.Cmp(RegisterName.R8, RegisterName.R10);
        asm.Jz("k_left");
        asm.MovImm16(RegisterName.R10, Hardware.KeyRight);
        asm.Cmp(RegisterName.R8, RegisterName.R10);
        asm.Jz("k_right");
        asm.Jmp("move");
        // Each arrow sets the direction, rejecting a 180° reversal (running into your own neck).
        asm.Label("k_up");
        Ld(DY, RegisterName.R10); asm.MovImm(RegisterName.R12, 1); asm.Cmp(RegisterName.R10, RegisterName.R12); asm.Jz("move");
        StI(DX, 0); StI(DY, -1); asm.Jmp("move");
        asm.Label("k_down");
        Ld(DY, RegisterName.R10); asm.MovImm(RegisterName.R12, 0); asm.Dec(RegisterName.R12); asm.Cmp(RegisterName.R10, RegisterName.R12); asm.Jz("move");
        StI(DX, 0); StI(DY, 1); asm.Jmp("move");
        asm.Label("k_left");
        Ld(DX, RegisterName.R10); asm.MovImm(RegisterName.R12, 1); asm.Cmp(RegisterName.R10, RegisterName.R12); asm.Jz("move");
        StI(DX, -1); StI(DY, 0); asm.Jmp("move");
        asm.Label("k_right");
        Ld(DX, RegisterName.R10); asm.MovImm(RegisterName.R12, 0); asm.Dec(RegisterName.R12); asm.Cmp(RegisterName.R10, RegisterName.R12); asm.Jz("move");
        StI(DX, 1); StI(DY, 0); asm.Jmp("move");

        // ---- MOVE + COLLISION ----
        asm.Label("move");
        // new head = (HX+DX, HY+DY) → R8, R9
        Ld(HX, RegisterName.R8); Ld(DX, RegisterName.R10); asm.Add(RegisterName.R8, RegisterName.R10);
        Ld(HY, RegisterName.R9); Ld(DY, RegisterName.R10); asm.Add(RegisterName.R9, RegisterName.R10);
        // wall: outside [0,W) × [0,H) → dead
        asm.MovImm(RegisterName.R10, 0); asm.Cmp(RegisterName.R8, RegisterName.R10); asm.Js("dead");
        asm.MovImm(RegisterName.R10, W); asm.Cmp(RegisterName.R8, RegisterName.R10); asm.Jns("dead");
        asm.MovImm(RegisterName.R10, 0); asm.Cmp(RegisterName.R9, RegisterName.R10); asm.Js("dead");
        asm.MovImm(RegisterName.R10, H); asm.Cmp(RegisterName.R9, RegisterName.R10); asm.Jns("dead");
        // &grid[newhead] → R14  (idx = R9*W + R8; ×4; + GRID)
        asm.Mov(RegisterName.R14, RegisterName.R9); asm.MovImm(RegisterName.R12, WShift); asm.Shl(RegisterName.R14, RegisterName.R12);
        asm.Add(RegisterName.R14, RegisterName.R8);
        asm.MovImm(RegisterName.R12, 2); asm.Shl(RegisterName.R14, RegisterName.R12);
        asm.MovImm16(RegisterName.R12, GRID); asm.Add(RegisterName.R14, RegisterName.R12);
        // grow?  new head == food
        Ld(FX, RegisterName.R10); asm.Cmp(RegisterName.R8, RegisterName.R10); asm.Jnz("nogrow");
        Ld(FY, RegisterName.R10); asm.Cmp(RegisterName.R9, RegisterName.R10); asm.Jnz("nogrow");
        // GROW: length++, score++, set head cell = length (no decrement → snake lengthens), new food
        Ld(LEN, RegisterName.R11); asm.Inc(RegisterName.R11); StR(LEN, RegisterName.R11);
        Ld(SCORE, RegisterName.R10); asm.Inc(RegisterName.R10); StR(SCORE, RegisterName.R10);
        asm.Store(RegisterName.R14, RegisterName.R11);
        StR(HX, RegisterName.R8); StR(HY, RegisterName.R9);
        asm.Call("place_food");
        asm.Jmp("main");
        asm.Label("nogrow");
        // decrement every snake cell (>0) — the tail vacates
        asm.MovImm16(RegisterName.EDI, GRID); asm.MovImm(RegisterName.ESI, 0);
        asm.Label("dec");
        asm.MovImm(RegisterName.EDX, Cells); asm.Cmp(RegisterName.ESI, RegisterName.EDX); asm.Jns("dec_done");
        asm.Load(RegisterName.EBX, RegisterName.EDI);
        asm.MovImm(RegisterName.EDX, 0); asm.Cmp(RegisterName.EBX, RegisterName.EDX); asm.Jz("dec_next");
        asm.Dec(RegisterName.EBX); asm.Store(RegisterName.EDI, RegisterName.EBX);
        asm.Label("dec_next");
        asm.MovImm(RegisterName.EDX, 4); asm.Add(RegisterName.EDI, RegisterName.EDX); asm.Inc(RegisterName.ESI); asm.Jmp("dec");
        asm.Label("dec_done");
        // self-collision: grid[newhead] still > 0 (body that hasn't vacated)
        asm.Load(RegisterName.EBX, RegisterName.R14); asm.MovImm(RegisterName.EDX, 0); asm.Cmp(RegisterName.EBX, RegisterName.EDX); asm.Jnz("dead");
        Ld(LEN, RegisterName.R11); asm.Store(RegisterName.R14, RegisterName.R11);
        StR(HX, RegisterName.R8); StR(HY, RegisterName.R9);
        asm.Jmp("main");

        // ---- GAME OVER / QUIT ----
        asm.Label("dead");
        asm.Call("render");
        asm.MovImm16(RegisterName.EAX, OverOff);
        asm.MovImm(RegisterName.ECX, 10);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);        // "GAME OVER\n" (canvas mode shows it)
        Ld(SCORE, RegisterName.EAX); asm.Exit(RegisterName.EAX);
        asm.Label("quit");
        Ld(SCORE, RegisterName.EAX); asm.Exit(RegisterName.EAX);

        // ---- place_food (CALL/RET): pick a random empty cell via a 16-bit LCG ----
        asm.Label("place_food");
        asm.Label("pf_retry");
        Ld(RNG, RegisterName.EAX);
        asm.MovImm16(RegisterName.EBX, 25173); asm.Mul(RegisterName.EAX, RegisterName.EBX);
        asm.MovImm16(RegisterName.EBX, 13849); asm.Add(RegisterName.EAX, RegisterName.EBX);
        StR(RNG, RegisterName.EAX);
        asm.Mov(RegisterName.EBX, RegisterName.EAX); asm.MovImm(RegisterName.ECX, W - 1); asm.And(RegisterName.EBX, RegisterName.ECX);  // fx = rng & (W-1)
        asm.Mov(RegisterName.ECX, RegisterName.EAX); asm.MovImm(RegisterName.EDX, 4); asm.Shr(RegisterName.ECX, RegisterName.EDX);
        asm.MovImm(RegisterName.EDX, H - 1); asm.And(RegisterName.ECX, RegisterName.EDX);   // fy = (rng>>4) & (H-1)
        asm.Mov(RegisterName.R10, RegisterName.ECX); asm.MovImm(RegisterName.R12, WShift); asm.Shl(RegisterName.R10, RegisterName.R12);
        asm.Add(RegisterName.R10, RegisterName.EBX);
        asm.MovImm(RegisterName.R12, 2); asm.Shl(RegisterName.R10, RegisterName.R12);
        asm.MovImm16(RegisterName.R12, GRID); asm.Add(RegisterName.R10, RegisterName.R12);
        asm.Load(RegisterName.R12, RegisterName.R10); asm.MovImm(RegisterName.EDX, 0); asm.Cmp(RegisterName.R12, RegisterName.EDX); asm.Jnz("pf_retry"); // occupied → retry
        StR(FX, RegisterName.EBX); StR(FY, RegisterName.ECX);
        asm.Ret();

        // ---- render (CALL/RET): build the whole frame at REND and OUTS it ----
        asm.Label("render");
        Ld(FX, RegisterName.R8); Ld(FY, RegisterName.R9);    // R8=FX, R9=FY (kept across the loop)
        asm.MovImm16(RegisterName.R13, REND);
        asm.Call("border");                                   // top border
        asm.MovImm(RegisterName.R14, 0);                      // y
        asm.Label("rrow");
        asm.MovImm(RegisterName.R10, H); asm.Cmp(RegisterName.R14, RegisterName.R10); asm.Jns("rrow_done");
        asm.MovImm(RegisterName.R11, Wall); EmitR11();        // left wall
        asm.MovImm(RegisterName.R15, 0);                      // x
        asm.Label("rcol");
        asm.MovImm(RegisterName.R10, W); asm.Cmp(RegisterName.R15, RegisterName.R10); asm.Jns("rcol_done");
        asm.Cmp(RegisterName.R15, RegisterName.R8); asm.Jnz("r_notfood");   // food? x==FX && y==FY
        asm.Cmp(RegisterName.R14, RegisterName.R9); asm.Jnz("r_notfood");
        asm.MovImm(RegisterName.R11, FoodCh); asm.Jmp("r_emit");
        asm.Label("r_notfood");
        asm.Mov(RegisterName.R10, RegisterName.R14); asm.MovImm(RegisterName.R12, WShift); asm.Shl(RegisterName.R10, RegisterName.R12);  // idx = y*W+x
        asm.Add(RegisterName.R10, RegisterName.R15);
        asm.MovImm(RegisterName.R12, 2); asm.Shl(RegisterName.R10, RegisterName.R12);
        asm.MovImm16(RegisterName.R12, GRID); asm.Add(RegisterName.R10, RegisterName.R12);
        asm.Load(RegisterName.R12, RegisterName.R10);
        asm.MovImm(RegisterName.R10, 0); asm.Cmp(RegisterName.R12, RegisterName.R10); asm.Jz("r_empty");    // snake? grid[idx] > 0
        asm.MovImm(RegisterName.R11, Body); asm.Jmp("r_emit");
        asm.Label("r_empty");
        asm.MovImm(RegisterName.R11, Empty);
        asm.Label("r_emit");
        EmitR11();
        asm.Inc(RegisterName.R15); asm.Jmp("rcol");
        asm.Label("rcol_done");
        asm.MovImm(RegisterName.R11, Wall); EmitR11();        // right wall
        asm.MovImm(RegisterName.R11, Nl); EmitR11();          // newline
        asm.Inc(RegisterName.R14); asm.Jmp("rrow");
        asm.Label("rrow_done");
        asm.Call("border");                                   // bottom border
        // score line: "S:" + 3 fixed decimal digits + newline
        asm.MovImm(RegisterName.R11, 0x53); EmitR11();        // 'S'
        asm.MovImm(RegisterName.R11, 0x3A); EmitR11();        // ':'
        Ld(SCORE, RegisterName.EDI);
        asm.Mov(RegisterName.EAX, RegisterName.EDI); asm.MovImm(RegisterName.EBX, 100); asm.Div(RegisterName.EAX, RegisterName.EBX);   // h
        asm.MovImm(RegisterName.R11, 0x30); asm.Add(RegisterName.R11, RegisterName.EAX); EmitR11();
        asm.Mov(RegisterName.EBX, RegisterName.EAX); asm.MovImm(RegisterName.ECX, 100); asm.Mul(RegisterName.EBX, RegisterName.ECX);
        asm.Mov(RegisterName.ESI, RegisterName.EDI); asm.Sub(RegisterName.ESI, RegisterName.EBX);           // rem = score - h*100
        asm.Mov(RegisterName.EAX, RegisterName.ESI); asm.MovImm(RegisterName.EBX, 10); asm.Div(RegisterName.EAX, RegisterName.EBX);    // t
        asm.MovImm(RegisterName.R11, 0x30); asm.Add(RegisterName.R11, RegisterName.EAX); EmitR11();
        asm.Mov(RegisterName.EBX, RegisterName.EAX); asm.MovImm(RegisterName.ECX, 10); asm.Mul(RegisterName.EBX, RegisterName.ECX);
        asm.Mov(RegisterName.EAX, RegisterName.ESI); asm.Sub(RegisterName.EAX, RegisterName.EBX);           // o = rem - t*10
        asm.MovImm(RegisterName.R11, 0x30); asm.Add(RegisterName.R11, RegisterName.EAX); EmitR11();
        asm.MovImm(RegisterName.R11, Nl); EmitR11();
        asm.MovImm(RegisterName.R11, 0); asm.Store(RegisterName.R13, RegisterName.R11);   // null-terminate
        asm.MovImm16(RegisterName.EAX, REND); asm.MovImm(RegisterName.ECX, 250); asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Ret();

        // ---- border (CALL/RET): (W+2) '#' then newline at R13 ----
        asm.Label("border");
        asm.MovImm(RegisterName.R10, 0);
        asm.Label("brd");
        asm.MovImm(RegisterName.R12, W + 2); asm.Cmp(RegisterName.R10, RegisterName.R12); asm.Jns("brd_done");
        asm.MovImm(RegisterName.R11, Wall); EmitR11();
        asm.Inc(RegisterName.R10); asm.Jmp("brd");
        asm.Label("brd_done");
        asm.MovImm(RegisterName.R11, Nl); EmitR11();
        asm.Ret();

        byte[] code = asm.Build();
        if (code.Length > OverOff)
        {
            throw new InvalidOperationException(
                $"Snake code ({code.Length} bytes) overruns the string area at {OverOff}; raise the offsets.");
        }
        byte[] image = new byte[OverOff + 10 * 4];
        Array.Copy(code, image, code.Length);
        WriteString(image, OverOff, "GAME OVER\n");
        return image;
    }

    // ===== SpawnChildren =====================================================
    // Parent forks three children with different lifetimes, then waits for all three.
    // Children output 1/2/3; parent outputs 0 last. Produces a 4-node tree.
    // WAIT clobbers EAX with the exit status, so each child PID is saved to a
    // dedicated register (ECX/EDX/ESI) before any WAIT runs.
    public static byte[] SpawnChildren()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 0);             // comparison constant (child EAX after FORK)

        // Fork child 1.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent1");
        // Child 1: short busy work, then halt.
        asm.MovImm(RegisterName.EAX, 100);
        asm.Label("spin1");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin1");
        asm.MovImm(RegisterName.EAX, 1);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent1");
        asm.Mov(RegisterName.ECX, RegisterName.EAX); // save child 1 pid

        // Fork child 2.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent2");
        // Child 2: medium busy work, then halt.
        asm.MovImm(RegisterName.EAX, 200);
        asm.Label("spin2");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin2");
        asm.MovImm(RegisterName.EAX, 2);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent2");
        asm.Mov(RegisterName.EDX, RegisterName.EAX); // save child 2 pid

        // Fork child 3.
        asm.Fork();
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent3");
        // Child 3: long busy work, then halt.
        asm.MovImm(RegisterName.EAX, 150);
        asm.Label("spin3");
        asm.Dec(RegisterName.EAX);
        asm.Jnz("spin3");
        asm.MovImm(RegisterName.EAX, 3);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("parent3");
        asm.Mov(RegisterName.ESI, RegisterName.EAX); // save child 3 pid

        // All three pids saved; now wait for each in turn.
        asm.Wait(RegisterName.ECX);                  // wait for child 1
        asm.Wait(RegisterName.EDX);                  // wait for child 2
        asm.Wait(RegisterName.ESI);                  // wait for child 3
        asm.MovImm(RegisterName.EAX, 0);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // ===== StringsDemo =======================================================
    // Interactive guessing game. Secret = 42. Reads guesses via IN, prints a hint
    // code (1 = too low, 2 = too high) until the guess is correct, then prints it.
    // Prompts for a name, reads it via INS, echoes it back via OUTS. Demonstrates
    // string I/O: type text and press Enter in the screen panel to respond.
    public static (byte[] image, int requiredMemory) StringsDemo()
    {
        const int inputWords = 32;
        const string prompt = "Name? ";

        // Pass 1: build with placeholder addresses to find code length.
        byte[] pass1 = BuildStringsDemoAsm(promptAddr: 0, prompt.Length, inputAddr: 0, inputWords).Build();

        // Prompt lives right after the code in the program image.
        int promptAddr = pass1.Length;
        // Input buffer starts after the embedded prompt (in RequiredMemory, past the image).
        int inputAddr = promptAddr + prompt.Length * 4;

        // Pass 2: rebuild with correct addresses.
        byte[] code = BuildStringsDemoAsm(promptAddr, prompt.Length, inputAddr, inputWords).Build();

        // Append the prompt string as 4-byte words (little-endian, char in low byte).
        byte[] image = new byte[code.Length + prompt.Length * 4];
        Array.Copy(code, image, code.Length);
        for (int i = 0; i < prompt.Length; i++)
        {
            image[code.Length + i * 4] = (byte)prompt[i];
        }

        return (image, inputWords * 4);
    }

    private static Assembler BuildStringsDemoAsm(int promptAddr, int promptLen, int inputAddr, int inputWords)
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, promptAddr);
        asm.MovImm(RegisterName.ECX, promptLen);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, inputAddr);
        asm.MovImm(RegisterName.ECX, inputWords);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, inputAddr);
        asm.MovImm(RegisterName.ECX, inputWords);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        return asm;
    }

    // ===== GuessingGame ======================================================
    public static byte[] GuessingGame()
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 42);   // secret
        asm.MovImm(RegisterName.ECX, 1);    // "too low" code
        asm.MovImm(RegisterName.EDX, 2);    // "too high" code
        asm.Label("guess");
        asm.In(RegisterName.EAX);           // read a guess
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("correct");
        asm.Js("toolow");                   // guess < secret
        asm.Out(RegisterName.EDX);          // too high
        asm.Jmp("guess");
        asm.Label("toolow");
        asm.Out(RegisterName.ECX);          // too low
        asm.Jmp("guess");
        asm.Label("correct");
        asm.Out(RegisterName.EAX);          // the answer
        asm.Hlt();
        return asm.Build();
    }
}
