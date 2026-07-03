using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// A captured snapshot of the registers the visualizer surfaces, plus the zero/sign
/// flags. Immutable so it can be stored per instruction step and diffed against the
/// previous step to highlight what changed.
/// </summary>
public sealed class RegisterSnapshot
{
    public static readonly RegisterName[] Shown =
    {
        RegisterName.EAX, RegisterName.EBX, RegisterName.ECX,
        RegisterName.EDX, RegisterName.ESI, RegisterName.EDI
    };

    // EFLAGS bit layout (mirrors InstructionFunctions): bit 0 = zero, bit 1 = sign.
    private const int ZeroFlagMask = 1;
    private const int SignFlagMask = 2;

    public IReadOnlyList<int> Values { get; }
    public bool Zero { get; }
    public bool Sign { get; }

    private RegisterSnapshot(IReadOnlyList<int> values, bool zero, bool sign)
    {
        Values = values;
        Zero = zero;
        Sign = sign;
    }

    public static RegisterSnapshot Capture(Hardware hw)
    {
        List<int> values = new List<int>();
        foreach (RegisterName name in Shown)
        {
            values.Add(hw.ReadRegister(name));
        }
        int flags = hw.ReadRegister(RegisterName.EFLAGS);
        return new RegisterSnapshot(values, (flags & ZeroFlagMask) != 0, (flags & SignFlagMask) != 0);
    }

    /// <summary>Renders "EAX=5 EBX=0 ... [Z-]" exactly as the streaming view does.</summary>
    public string Format()
    {
        List<string> parts = new List<string>();
        for (int i = 0; i < Shown.Length; i++)
        {
            parts.Add($"{Shown[i]}={Values[i]}");
        }
        string zero = "-";
        if (Zero)
        {
            zero = "Z";
        }
        string sign = "-";
        if (Sign)
        {
            sign = "S";
        }
        parts.Add($"[{zero}{sign}]");
        return string.Join(" ", parts);
    }

    /// <summary>True if register at index <paramref name="i"/> differs from <paramref name="previous"/>.</summary>
    public bool Changed(RegisterSnapshot? previous, int i)
    {
        if (previous == null)
        {
            return false;
        }
        return Values[i] != previous.Values[i];
    }
}

/// <summary>One executed instruction, tagged with the privilege level it ran at.</summary>
public sealed class InstructionStep
{
    public int Address { get; init; }
    public string Mnemonic { get; init; } = "";
    public PrivilegeLevel Privilege { get; init; }
    public string Process { get; init; } = "";
    public RegisterSnapshot Registers { get; init; } = null!;

    // User code is the "program" side; Kernel syscall handlers and Privileged OS
    // routines are the "kernel" side of the split instruction view.
    public bool IsProgramSide
    {
        get { return Privilege == PrivilegeLevel.User; }
    }
}

/// <summary>A privilege transition, with an OS-level description of what it means.</summary>
public sealed class PrivilegeTransition
{
    public PrivilegeLevel From { get; init; }
    public PrivilegeLevel To { get; init; }
    public string Description { get; init; } = "";
    // True when interrupts were masked at the transition — i.e. this is an atomic
    // OS-routine dispatch rather than a (preemptible) syscall trap into the kernel.
    public bool InterruptsMasked { get; init; }
    public int AtAddress { get; init; }
}

/// <summary>
/// The render-agnostic source of truth for the visualization: the current process and
/// privilege, the instruction history, the process/scheduler/heap views read from OS
/// memory, the latest register snapshot, the privilege-transition log, and run-stat
/// counters. Renderers read this; it holds no console or Spectre types.
/// </summary>
public sealed class VisualizerModel
{
    public string CurrentProcess { get; set; } = "(booting)";
    public PrivilegeLevel CurrentPrivilege { get; set; } = PrivilegeLevel.User;
    public bool ShowProgramIo { get; set; }

    // ---- foreground / shared screen --------------------------------------
    // The focused (foreground) process index — the one whose screen is shown and whose
    // stdin the live keyboard feeds. Kept in sync with Hardware.SetActiveProcess.
    public int FocusedProcess { get; set; } = -1;
    // Each process's screen output (values it emitted via OUT or OUTS), keyed by its
    // process index. Each entry is a string: integers are stored as their ToString().
    // The shared screen renders only the focused process's buffer.
    public Dictionary<int, List<string>> OutputBuffers { get; } = new Dictionary<int, List<string>>();

    // Appends an integer output to a process's screen buffer.
    public void RecordOutput(int sourceProcess, int value)
    {
        RecordOutput(sourceProcess, value.ToString());
    }

    // Appends a string output to a process's screen buffer.
    public void RecordOutput(int sourceProcess, string value)
    {
        if (!OutputBuffers.TryGetValue(sourceProcess, out List<string>? buffer))
        {
            buffer = new List<string>();
            OutputBuffers[sourceProcess] = buffer;
        }
        buffer.Add(value);
    }

    // The focused process's screen buffer, or an empty list when none.
    public IReadOnlyList<string> FocusedOutput()
    {
        if (FocusedProcess >= 0 && OutputBuffers.TryGetValue(FocusedProcess, out List<string>? buffer))
        {
            return buffer;
        }
        return Array.Empty<string>();
    }

    public bool HasOsImage { get; set; }
    public int CurrentIndex { get; set; } = -1;
    public List<BuddyHeapView.ProcessRow> ProcessTable { get; set; } = new List<BuddyHeapView.ProcessRow>();
    public List<BuddyHeapView.FreeBlock> FreeBlocks { get; set; } = new List<BuddyHeapView.FreeBlock>();
    public BuddyHeapView.BuddyNode? BuddyTree { get; set; }
    // The reconstructed filesystem snapshot (superblock stats, block map, dir tree). The
    // bridge rebuilds it only when an FS OS-routine runs (and once at boot), then swaps it
    // in wholesale, so frames can safely hold a reference. Null until first built.
    public FsDiskView.Snapshot? DiskView { get; set; }

    public const int MaxHistoryLength = 80;
    public List<InstructionStep> History { get; } = new List<InstructionStep>();
    public List<PrivilegeTransition> Transitions { get; } = new List<PrivilegeTransition>();
    public RegisterSnapshot? Registers { get; set; }
    public RegisterSnapshot? PreviousRegisters { get; set; }

    // ---- run statistics ---------------------------------------------------
    public int InstructionCount { get; set; }
    public int ContextSwitchCount { get; set; }
    public int FaultCount { get; set; }
    public int BlockCount { get; set; }
    public int WakeCount { get; set; }
    public int OutputCount { get; set; }
    public Dictionary<string, int> InstructionsByProcess { get; } = new Dictionary<string, int>();

    // ---- branch predictor (observational) --------------------------------
    // User-program branch-prediction stats and the observational cycle counter, fed by
    // the BranchPredicted event. Cycles include the misprediction penalty.
    public long BranchPredictions { get; set; }
    public long BranchHits { get; set; }
    public long BranchMisses { get; set; }
    public long Cycles { get; set; }

    public double BranchAccuracy
    {
        get
        {
            if (BranchPredictions == 0)
            {
                return 0.0;
            }
            else
            {
                return (double)BranchHits / BranchPredictions;
            }
        }
    }

    public void RecordInstruction(InstructionStep step)
    {
        PreviousRegisters = Registers;
        Registers = step.Registers;
        History.Add(step);
        if (History.Count > MaxHistoryLength)
        {
            History.RemoveAt(0);
        }
        InstructionCount++;
        if (InstructionsByProcess.TryGetValue(step.Process, out int n))
        {
            InstructionsByProcess[step.Process] = n + 1;
        }
        else
        {
            InstructionsByProcess[step.Process] = 1;
        }
    }
}
