using CSharpOS;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace CSharpOSConsole;

/// <summary>
/// Subscribes to Hardware events and renders a step-by-step view of execution.
/// All text is written to an injected <see cref="TextWriter"/> (Console.Out in
/// production, a StringWriter in tests), which makes the output easy to capture
/// and assert on. Color is an optional side-channel applied to the real console
/// only when <c>useColor</c> is set, and keyboard pacing only runs when
/// <c>interactive</c> is set, so a captured run is plain, deterministic text.
///
/// Beyond the instruction stream it surfaces the OS's inner workings: privilege
/// transitions (syscall traps / OS-routine dispatch), block and wake events, the
/// process table read from OS memory on each context switch, and the free-memory
/// map whenever it changes.
/// </summary>
public sealed class ConsoleVisualizer
{
    private readonly Hardware hw;
    private readonly OperatingSystem os;
    private readonly TextWriter output;
    private readonly bool useColor;
    private readonly bool interactive;
    private readonly VisualizerMode mode;
    private string currentProcess = "(booting)";
    private bool manual;
    private int delayMs;
    private string lastFreeMap = "";

    // Whether program I/O (OUTPUT, and the value of an input interrupt) is mirrored
    // into the OS/Hardware window. Off by default — I/O lives in the per-process
    // windows; toggled live with the 'o' key.
    public bool ShowProgramIo { get; set; }

    // Per-tier gates. Higher tiers are supersets of lower ones.
    private bool ShowInstructions { get { return mode >= VisualizerMode.Normal; } }
    private bool ShowTables { get { return mode >= VisualizerMode.Normal; } }
    private bool ShowMemory { get { return mode >= VisualizerMode.Verbose; } }
    private bool ShowPrivilege { get { return mode >= VisualizerMode.Verbose; } }

    private static readonly RegisterName[] Shown =
    {
        RegisterName.EAX, RegisterName.EBX, RegisterName.ECX,
        RegisterName.EDX, RegisterName.ESI, RegisterName.EDI
    };

    public ConsoleVisualizer(Hardware hw, OperatingSystem os, int delayMs,
        TextWriter? output = null, bool useColor = true, bool interactive = true,
        VisualizerMode mode = VisualizerMode.Normal, bool showProgramIo = false)
    {
        this.hw = hw;
        this.os = os;
        this.delayMs = delayMs;
        this.output = output ?? Console.Out;
        this.useColor = useColor;
        this.interactive = interactive;
        this.mode = mode;
        ShowProgramIo = showProgramIo;

        // Scheduling/fault observability comes from Hardware (the OS logic runs as
        // ISA code); process names are resolved from the OS's base->name map.
        hw.InstructionExecuted += OnInstructionExecuted;
        hw.MemoryWritten += OnMemoryWritten;
        hw.ProgramOutput += OnProgramOutput;
        hw.ContextSwitched += OnContextSwitched;
        hw.InvalidInstruction += OnInvalidInstruction;
        hw.PrivilegeChanged += OnPrivilegeChanged;
        hw.ProcessBlocked += OnProcessBlocked;
        hw.ProcessWoken += OnProcessWoken;
        hw.OsRoutineEntered += OnOsRoutineEntered;
    }

    // Marks which OS routine is running (ContextSwitch, Halt, Schedule, ...) in
    // every tier, so the OS's own activity is always visible.
    private void OnOsRoutineEntered(object? sender, OsRoutineArgs e)
    {
        WriteColored(ConsoleColor.Magenta, $"      *  OS routine: {e.Name}");
    }

    private void OnContextSwitched(object? sender, ContextSwitchArgs e)
    {
        currentProcess = FriendlyName(os.NameForBase(e.ToProgramBase));
        WriteColored(ConsoleColor.Cyan, $"  === context switch -> {currentProcess}");
        if (ShowTables)
        {
            RenderProcessTable();
            RenderFreeMemoryIfChanged();
        }
    }

    private void OnInstructionExecuted(object? sender, InstructionExecutedArgs e)
    {
        if (!ShowInstructions)
        {
            return;
        }
        SetColor(ConsoleColor.DarkGray);
        output.Write($"[{currentProcess,-12}] ");
        ResetColor();
        output.Write($"{e.Address,4}: ");
        SetColor(ConsoleColor.White);
        output.Write($"{Decode(e),-18}");
        ResetColor();
        output.WriteLine($"  {RegisterSnapshot()}");
        Pace();
    }

    private void OnMemoryWritten(object? sender, MemoryWrittenArgs e)
    {
        if (!ShowMemory)
        {
            return;
        }
        // Skip bulk writes (program image load, register-state saves) and show
        // only word-sized writes, which are the program's own STORE operations.
        if (e.Data.Length > 4)
        {
            return;
        }
        int value = e.Data[0] | (e.Data[1] << 8) | (e.Data[2] << 16) | (e.Data[3] << 24);
        WriteColored(ConsoleColor.DarkYellow, $"      mem[{e.Address}] = {value}");
    }

    private void OnProgramOutput(object? sender, ProgramOutputArgs e)
    {
        // Program output belongs to the process's own window; only mirror it here
        // when I/O display is toggled on.
        if (!ShowProgramIo)
        {
            return;
        }
        WriteColored(ConsoleColor.Green, $"      >  OUTPUT: {e.Value}");
    }

    private void OnInvalidInstruction(object? sender, InvalidInstructionArgs e)
    {
        string reason = e.Reason ?? "invalid instruction";
        WriteColored(ConsoleColor.Red, $"      XX INVALID [{e.Opcode:X2}] in {currentProcess} - {reason}");
    }

    private void OnPrivilegeChanged(object? sender, PrivilegeChangedArgs e)
    {
        if (!ShowPrivilege)
        {
            return;
        }
        // The dispatch into an OS routine (-> Privileged) is already announced by
        // OsRoutineEntered; here we surface the other transitions (syscall, IRET,
        // process resume) so they aren't doubly reported.
        if (e.To == PrivilegeLevel.Privileged)
        {
            return;
        }
        WriteColored(ConsoleColor.DarkMagenta, $"      *  {e.From} -> {e.To}  ({DescribeTransition(e.From, e.To)})");
    }

    private void OnProcessBlocked(object? sender, ProcessBlockedArgs e)
    {
        WriteColored(ConsoleColor.DarkYellow, $"      || {currentProcess} blocked on {e.Reason}");
    }

    private void OnProcessWoken(object? sender, ProcessWokenArgs e)
    {
        // The wake (interrupt delivery) is OS/hardware activity and always shows; the
        // input value itself is program I/O, shown only when I/O display is on.
        string detail = "";
        if (e.Reason == WaitReason.Input && ShowProgramIo)
        {
            detail = $" (value {e.Value})";
        }
        WriteColored(ConsoleColor.Green, $"      >> wake signal: {e.Reason}{detail}");
    }

    // Describes a privilege transition in OS terms, so the dispatch path is legible.
    private static string DescribeTransition(PrivilegeLevel from, PrivilegeLevel to)
    {
        if (to == PrivilegeLevel.Privileged)
        {
            return "OS routine";
        }
        if (to == PrivilegeLevel.Kernel)
        {
            // From user code this is a syscall trap; from an OS routine it is the
            // resumption of a process that was interrupted inside a kernel handler.
            if (from == PrivilegeLevel.User)
            {
                return "syscall trap";
            }
            return "resume (kernel)";
        }
        if (to == PrivilegeLevel.User && from == PrivilegeLevel.Kernel)
        {
            return "IRET to user";
        }
        return "resume process";
    }

    // Renders the OS process table read directly from OS memory, marking the
    // currently scheduled slot. Only meaningful when an OS image is present.
    private void RenderProcessTable()
    {
        if (hw.GetOsMemorySize() == 0)
        {
            return;
        }
        int count = ReadOsWord(OsLayout.ProcessCountOffset);
        int current = ReadOsWord(OsLayout.CurrentIndexOffset);
        string plural = "s";
        if (count == 1)
        {
            plural = "";
        }
        WriteColored(ConsoleColor.DarkCyan, $"      +- process table ({count} slot{plural}, current={current}) -");
        for (int i = 0; i < count; i++)
        {
            int entry = OsLayout.ProcessEntryAddress(i);
            ProcessState state = (ProcessState)ReadOsWord(entry + Hardware.ProcessEntryState);
            WaitReason wait = (WaitReason)ReadOsWord(entry + Hardware.ProcessEntryWaitReason);
            int programAddress = ReadOsWord(entry + Hardware.ProcessEntryProgramAddress);
            string name = FriendlyName(os.NameForBase(programAddress));
            string marker = " ";
            if (i == current)
            {
                marker = ">";
            }
            string waitText = "";
            if (wait != WaitReason.None)
            {
                waitText = $" on {wait}";
            }
            WriteColored(ConsoleColor.DarkCyan, $"      | {marker} [{i}] {name,-12} {state}{waitText}");
        }
    }

    // Renders the OS free-range table, but only when it has changed since the last
    // render, so allocation and reclaim stand out without flooding the log.
    private void RenderFreeMemoryIfChanged()
    {
        if (hw.GetOsMemorySize() == 0)
        {
            return;
        }
        int count = ReadOsWord(OsLayout.FreeRangeCountOffset);
        List<string> blocks = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int slot = OsLayout.FreeRangeTableOffset + i * OsLayout.FreeRangeSize;
            int start = ReadOsWord(slot);
            int size = ReadOsWord(slot + 4);
            blocks.Add($"[{start}+{size}]");
        }
        string map = "(none)";
        if (blocks.Count != 0)
        {
            map = string.Join(" ", blocks);
        }
        if (map == lastFreeMap)
        {
            return;
        }
        lastFreeMap = map;
        WriteColored(ConsoleColor.DarkGray, $"      free memory: {map}");
    }

    private int ReadOsWord(int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    private string RegisterSnapshot()
    {
        List<string> parts = new List<string>();
        foreach (RegisterName name in Shown)
        {
            parts.Add($"{name}={hw.ReadRegister(name)}");
        }
        int flags = hw.ReadRegister(RegisterName.EFLAGS);
        string zero;
        if ((flags & 1) != 0)
        {
            zero = "Z";
        }
        else
        {
            zero = "-";
        }
        string sign;
        if ((flags & 2) != 0)
        {
            sign = "S";
        }
        else
        {
            sign = "-";
        }
        parts.Add($"[{zero}{sign}]");
        return string.Join(" ", parts);
    }

    private static string Decode(InstructionExecutedArgs e)
    {
        RegisterName R(byte index) { return (RegisterName)index; }
        int addr = (e.B1 << 8) | e.B2;

        switch (e.Opcode)
        {
            case Instruction.MOV_REG_REG: return $"MOV {R(e.B1)}, {R(e.B2)}";
            case Instruction.MOV_REG_IMM: return $"MOV {R(e.B1)}, {e.B2}";
            case Instruction.LOAD:        return $"LOAD {R(e.B1)}, [{R(e.B2)}]";
            case Instruction.STORE:       return $"STORE [{R(e.B1)}], {R(e.B2)}";
            case Instruction.ADD:         return $"ADD {R(e.B1)}, {R(e.B2)}";
            case Instruction.SUB:         return $"SUB {R(e.B1)}, {R(e.B2)}";
            case Instruction.MUL:         return $"MUL {R(e.B1)}, {R(e.B2)}";
            case Instruction.DIV:         return $"DIV {R(e.B1)}, {R(e.B2)}";
            case Instruction.CMP:         return $"CMP {R(e.B1)}, {R(e.B2)}";
            case Instruction.INC:         return $"INC {R(e.B1)}";
            case Instruction.DEC:         return $"DEC {R(e.B1)}";
            case Instruction.JMP:         return $"JMP {addr}";
            case Instruction.JZ:          return $"JZ {addr}";
            case Instruction.JNZ:         return $"JNZ {addr}";
            case Instruction.JS:          return $"JS {addr}";
            case Instruction.JNS:         return $"JNS {addr}";
            case Instruction.CALL:        return $"CALL {addr}";
            case Instruction.RET:         return "RET";
            case Instruction.OUT:         return $"OUT {R(e.B1)}";
            case Instruction.IN:          return $"IN {R(e.B1)}";
            case Instruction.HLT:         return "HLT";
            case Instruction.IRET:        return "IRET";
            default:                      return $"??? {e.Opcode:X2}";
        }
    }

    private void Pace()
    {
        if (manual)
        {
            SetColor(ConsoleColor.DarkGray);
            output.Write("      (Enter = step, a = auto, o = toggle I/O) ");
            ResetColor();
            ConsoleKeyInfo key = Console.ReadKey(true);
            output.WriteLine();
            if (key.KeyChar == 'a' || key.KeyChar == 'A')
            {
                manual = false;
            }
            else if (key.KeyChar == 'o' || key.KeyChar == 'O')
            {
                ToggleProgramIo();
            }
            return;
        }

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }
        if (interactive && !Console.IsInputRedirected && Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.KeyChar == 's' || key.KeyChar == 'S')
            {
                manual = true;
            }
            else if (key.KeyChar == 'o' || key.KeyChar == 'O')
            {
                ToggleProgramIo();
            }
        }
    }

    private void ToggleProgramIo()
    {
        ShowProgramIo = !ShowProgramIo;
        string state = "off";
        if (ShowProgramIo)
        {
            state = "on";
        }
        WriteColored(ConsoleColor.DarkGreen, $"      [program I/O in this window: {state}]");
    }

    private static string FriendlyName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(none)";
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    // Applies a foreground color to the real console only when coloring is enabled;
    // the text itself always goes to the injected writer.
    private void SetColor(ConsoleColor color)
    {
        if (useColor)
        {
            Console.ForegroundColor = color;
        }
    }

    private void ResetColor()
    {
        if (useColor)
        {
            Console.ResetColor();
        }
    }

    private void WriteColored(ConsoleColor color, string text)
    {
        SetColor(color);
        output.WriteLine(text);
        ResetColor();
    }
}
