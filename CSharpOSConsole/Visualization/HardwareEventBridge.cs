using CSharpOS;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// Subscribes to Hardware's observability events and translates each into updates on
/// the <see cref="VisualizerModel"/> plus a call to the active <see cref="IVisualizerRenderer"/>.
/// This is the only piece that knows about Hardware; the model and renderers stay
/// decoupled from the emulator.
/// </summary>
public sealed class HardwareEventBridge
{
    private readonly Hardware hw;
    private readonly OperatingSystem os;
    private readonly VisualizerModel model;
    private readonly IVisualizerRenderer renderer;
    private readonly Pacer pacer;
    private readonly DetailLevel detail;

    public HardwareEventBridge(Hardware hw, OperatingSystem os, VisualizerModel model,
        IVisualizerRenderer renderer, Pacer pacer, DetailLevel detail = DetailLevel.High)
    {
        this.hw = hw;
        this.os = os;
        this.model = model;
        this.renderer = renderer;
        this.pacer = pacer;
        this.detail = detail;

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

    private void OnOsRoutineEntered(object? sender, OsRoutineArgs e)
    {
        renderer.OsRoutineEntered(e.Name);
    }

    private void OnContextSwitched(object? sender, ContextSwitchArgs e)
    {
        model.CurrentProcess = PlainTextRenderer.FriendlyName(os.NameForBase(e.ToProgramBase));
        model.ContextSwitchCount++;
        if (BuddyHeapView.HasOs(hw))
        {
            model.HasOsImage = true;
            model.CurrentIndex = BuddyHeapView.CurrentIndex(hw);
            model.ProcessTable = BuddyHeapView.ReadProcessTable(hw, os.NameForBase);
            model.FreeBlocks = BuddyHeapView.ReadFreeBlocks(hw) ?? new List<BuddyHeapView.FreeBlock>();
            model.BuddyTree = BuddyHeapView.ReadTree(hw, os.NameForBase);
        }
        renderer.ContextSwitched(model);
    }

    private void OnInstructionExecuted(object? sender, InstructionExecutedArgs e)
    {
        // Decode is a single switch lookup — always do it regardless of detail level.
        string mnemonic = Disassembler.Decode(e.Opcode, e.B1, e.B2, e.B3);
        RegisterSnapshot? registers = null;
        if (detail != DetailLevel.Low)
        {
            registers = RegisterSnapshot.Capture(hw);
        }
        InstructionStep step = new InstructionStep
        {
            Address = e.Address,
            Mnemonic = mnemonic,
            Privilege = hw.GetPrivilegeLevel(),
            Process = model.CurrentProcess,
            Registers = registers!
        };
        model.CurrentPrivilege = step.Privilege;
        model.RecordInstruction(step);
        renderer.InstructionExecuted(step, model);
        pacer.AfterStep();
    }

    private void OnMemoryWritten(object? sender, MemoryWrittenArgs e)
    {
        // Skip bulk writes (program image load, register-state saves) and surface only
        // word-sized writes, which are the program's own STORE operations.
        if (e.Data.Length > 4)
        {
            return;
        }
        int value = e.Data[0] | (e.Data[1] << 8) | (e.Data[2] << 16) | (e.Data[3] << 24);
        renderer.MemoryWritten(e.Address, value);
    }

    private void OnProgramOutput(object? sender, ProgramOutputArgs e)
    {
        model.OutputCount++;
        // Program output belongs to the process's own window; only mirror it here when
        // I/O display is toggled on.
        if (model.ShowProgramIo)
        {
            renderer.ProgramOutput(e.Value);
        }
    }

    private void OnInvalidInstruction(object? sender, InvalidInstructionArgs e)
    {
        model.FaultCount++;
        string reason = e.Reason ?? "invalid instruction";
        renderer.InvalidInstruction(e.Opcode, reason, model.CurrentProcess);
    }

    private void OnPrivilegeChanged(object? sender, PrivilegeChangedArgs e)
    {
        PrivilegeTransition transition = new PrivilegeTransition
        {
            From = e.From,
            To = e.To,
            Description = DescribeTransition(e.From, e.To),
            AtAddress = hw.GetInstructionPointer()
        };
        model.CurrentPrivilege = e.To;
        model.Transitions.Add(transition);
        renderer.PrivilegeChanged(transition, model);
    }

    private void OnProcessBlocked(object? sender, ProcessBlockedArgs e)
    {
        model.BlockCount++;
        renderer.ProcessBlocked(model.CurrentProcess, e.Reason);
    }

    private void OnProcessWoken(object? sender, ProcessWokenArgs e)
    {
        model.WakeCount++;
        // The wake (interrupt delivery) is OS/hardware activity and always shows; the
        // input value itself is program I/O, shown only when I/O display is on.
        bool showValue = e.Reason == WaitReason.Input && model.ShowProgramIo;
        renderer.ProcessWoken(e.Reason, e.Value, showValue);
    }

    // Describes a privilege transition in OS terms, so the dispatch path is legible.
    public static string DescribeTransition(PrivilegeLevel from, PrivilegeLevel to)
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
}
