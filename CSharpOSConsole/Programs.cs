using CSharpOS;

namespace CSharpOSConsole;

/// <summary>
/// Builds the demo programs as assembled byte images.
/// </summary>
public static class Programs
{
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

    // A real command shell (Shell §2): prompt, read a command line (INS), fork, have the child
    // exec-by-path the typed command with its arguments (FSYS Exec) while the parent focuses the
    // child (so its output is foreground) and WAITs for it, then loops. v1 uses absolute paths (no
    // CWD): commands are typed like "/bin/ls /", "/bin/cat /note". A command that does not resolve
    // prints "?".
    //
    // The parent reads the line (it is the focused foreground process, so keyboard input reaches
    // it) into a DATA-region buffer, then forks: the child inherits the buffer and execs it. This
    // relies on FORK propagating the parent's just-typed line to the child — which works now that
    // kernel-mediated writes (INS) mark their frame dirty so fork's flush_frames carries them.
    public static byte[] Shell()
    {
        const int PromptOff = 128;   // "$ " prompt (image-resident, read-only)
        const int ErrOff = 160;      // "?" printed when a command fails to exec
        const int LineBuf = 512;     // typed command line — a DATA-region page (needs memory >= 1024)
        const int LineMax = 32;      // buffer capacity in words

        Assembler asm = new Assembler();
        asm.Label("loop");
        asm.MovImm16(RegisterName.EAX, PromptOff);
        asm.MovImm(RegisterName.ECX, 2);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);   // prompt
        asm.MovImm16(RegisterName.EAX, LineBuf);
        asm.MovImm(RegisterName.ECX, LineMax);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);    // read a line (blocks until one arrives)
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jnz("parent");
        // Child (EAX == 0): become the typed command. FSYS Exec returns only on failure.
        asm.MovImm(RegisterName.EAX, Hardware.FsysExec);
        asm.MovImm16(RegisterName.EBX, LineBuf);
        asm.Fsys();
        asm.MovImm16(RegisterName.EAX, ErrOff);
        asm.MovImm(RegisterName.ECX, 1);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);   // command not found / not a file
        asm.MovImm(RegisterName.EAX, 0);
        asm.Exit(RegisterName.EAX);
        // Parent (EAX == child PID): focus the child + wait, then prompt again. Save the PID before
        // WAIT (which clobbers EAX with the exit status).
        asm.Label("parent");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);
        asm.SetFocus(RegisterName.ESI);
        asm.Wait(RegisterName.ESI);
        asm.Jmp("loop");
        byte[] code = asm.Build();

        // The line buffer lives in the DATA region, not the image; the image is just code + prompt.
        byte[] image = new byte[ErrOff + 4];
        Array.Copy(code, image, code.Length);
        WriteString(image, PromptOff, "$ ");
        WriteString(image, ErrOff, "?");
        return image;
    }

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
