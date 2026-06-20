using CSharpOS;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace CSharpOSConsole;

/// <summary>
/// Subscribes to Hardware and OperatingSystem events and renders a step-by-step
/// view of execution to the console. Paces execution either automatically (a
/// fixed delay) or manually (one keypress per instruction); press 's' to switch
/// to manual, 'a' to switch back to auto.
/// </summary>
public sealed class ConsoleVisualizer
{
    private readonly Hardware hw;
    private readonly OperatingSystem os;
    private string currentProcess = "(booting)";
    private bool manual;
    private int delayMs;

    private static readonly RegisterName[] Shown =
    {
        RegisterName.EAX, RegisterName.EBX, RegisterName.ECX,
        RegisterName.EDX, RegisterName.ESI, RegisterName.EDI
    };

    public ConsoleVisualizer(Hardware hw, OperatingSystem os, int delayMs)
    {
        this.hw = hw;
        this.os = os;
        this.delayMs = delayMs;

        // Scheduling/fault observability now comes from Hardware (the OS logic runs
        // as ISA code); process names are resolved from the OS's base->name map.
        hw.InstructionExecuted += OnInstructionExecuted;
        hw.MemoryWritten += OnMemoryWritten;
        hw.ProgramOutput += OnProgramOutput;
        hw.ContextSwitched += OnContextSwitched;
        hw.InvalidInstruction += OnInvalidInstruction;
    }

    private void OnContextSwitched(object? sender, ContextSwitchArgs e)
    {
        currentProcess = FriendlyName(os.NameForBase(e.ToProgramBase));
        WriteColored(ConsoleColor.Cyan, $"  ╞══ context switch → {currentProcess}");
    }

    private void OnInstructionExecuted(object? sender, InstructionExecutedArgs e)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{currentProcess,-12}] ");
        Console.ResetColor();
        Console.Write($"{e.Address,4}: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{Decode(e),-18}");
        Console.ResetColor();
        Console.WriteLine($"  {RegisterSnapshot()}");
        Pace();
    }

    private void OnMemoryWritten(object? sender, MemoryWrittenArgs e)
    {
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
        WriteColored(ConsoleColor.Green, $"      ► OUTPUT: {e.Value}");
    }

    private void OnInvalidInstruction(object? sender, InvalidInstructionArgs e)
    {
        string reason = e.Reason ?? "invalid instruction";
        WriteColored(ConsoleColor.Red, $"      ✗ INVALID [{e.Opcode:X2}] in {currentProcess} — {reason}");
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
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("      (Enter = step, a = auto) ");
            Console.ResetColor();
            ConsoleKeyInfo key = Console.ReadKey(true);
            Console.WriteLine();
            if (key.KeyChar == 'a' || key.KeyChar == 'A')
            {
                manual = false;
            }
            return;
        }

        Thread.Sleep(delayMs);
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.KeyChar == 's' || key.KeyChar == 'S')
            {
                manual = true;
            }
        }
    }

    private static string FriendlyName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(none)";
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
