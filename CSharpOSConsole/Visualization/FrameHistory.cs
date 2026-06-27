using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// An immutable snapshot of the renderable state at one executed instruction. Because
/// the bridge replaces (never mutates) the model's process-table/free-block/buddy-tree
/// objects, a frame can safely hold references to them — capturing a frame is cheap.
/// Used to redraw the dashboard at any past step (view replay, not CPU reverse-run).
/// </summary>
public sealed class Frame
{
    public int StepNumber { get; init; }       // 1-based instruction index at this frame
    public int HistoryLength { get; init; }     // model.History.Count at capture time
    public RegisterSnapshot? Registers { get; init; }
    public RegisterSnapshot? PreviousRegisters { get; init; }
    public PrivilegeLevel Privilege { get; init; }
    public string CurrentProcess { get; init; } = "";
    public int CurrentIndex { get; init; } = -1;
    public bool HasOsImage { get; init; }
    public IReadOnlyList<BuddyHeapView.ProcessRow> ProcessTable { get; init; } = new List<BuddyHeapView.ProcessRow>();
    public IReadOnlyList<BuddyHeapView.FreeBlock> FreeBlocks { get; init; } = new List<BuddyHeapView.FreeBlock>();
    public BuddyHeapView.BuddyNode? BuddyTree { get; init; }

    public int InstructionCount { get; init; }
    public int ContextSwitchCount { get; init; }
    public int FaultCount { get; init; }
    public int BlockCount { get; init; }
    public int WakeCount { get; init; }
    public int OutputCount { get; init; }

    // Branch-predictor stats (observational): user-program predictions and the cycle
    // counter (baseline + misprediction penalties).
    public long BranchPredictions { get; init; }
    public long BranchHits { get; init; }
    public long BranchMisses { get; init; }
    public double BranchAccuracy { get; init; }
    public long Cycles { get; init; }
}

/// <summary>
/// A capped, append-only sequence of <see cref="Frame"/> snapshots plus a cursor used
/// for forward/backward scrubbing. The cursor sits at the live edge (the latest frame)
/// during normal execution; stepping back moves it into the past for review.
/// </summary>
public sealed class FrameHistory
{
    public const int DefaultCapacity = 20000;

    private readonly List<Frame> frames = new List<Frame>();
    private readonly int capacity;
    private int cursor = -1;

    public FrameHistory(int capacity = DefaultCapacity)
    {
        this.capacity = capacity;
    }

    public int Count
    {
        get { return frames.Count; }
    }

    public int Cursor
    {
        get { return cursor; }
    }

    public bool IsEmpty
    {
        get { return frames.Count == 0; }
    }

    /// <summary>True when the cursor is at the most recent frame (live edge).</summary>
    public bool AtLiveEdge
    {
        get { return frames.Count == 0 || cursor == frames.Count - 1; }
    }

    public Frame? Current
    {
        get
        {
            if (cursor < 0 || cursor >= frames.Count)
            {
                return null;
            }
            return frames[cursor];
        }
    }

    /// <summary>Captures the model's current state as a new frame and parks the cursor on it.</summary>
    public void Capture(VisualizerModel model)
    {
        Frame frame = new Frame
        {
            StepNumber = model.InstructionCount,
            HistoryLength = model.History.Count,
            Registers = model.Registers,
            PreviousRegisters = model.PreviousRegisters,
            Privilege = model.CurrentPrivilege,
            CurrentProcess = model.CurrentProcess,
            CurrentIndex = model.CurrentIndex,
            HasOsImage = model.HasOsImage,
            ProcessTable = model.ProcessTable,
            FreeBlocks = model.FreeBlocks,
            BuddyTree = model.BuddyTree,
            InstructionCount = model.InstructionCount,
            ContextSwitchCount = model.ContextSwitchCount,
            FaultCount = model.FaultCount,
            BlockCount = model.BlockCount,
            WakeCount = model.WakeCount,
            OutputCount = model.OutputCount,
            BranchPredictions = model.BranchPredictions,
            BranchHits = model.BranchHits,
            BranchMisses = model.BranchMisses,
            BranchAccuracy = model.BranchAccuracy,
            Cycles = model.Cycles
        };
        frames.Add(frame);
        if (frames.Count > capacity)
        {
            frames.RemoveAt(0);
        }
        cursor = frames.Count - 1;
    }

    /// <summary>Moves the cursor one frame back. Returns true if it moved.</summary>
    public bool StepBack()
    {
        if (cursor > 0)
        {
            cursor--;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Moves the cursor one frame forward over already-recorded history. Returns true if
    /// it moved; false if already at the live edge (the caller should execute instead).
    /// </summary>
    public bool StepForward()
    {
        if (cursor < frames.Count - 1)
        {
            cursor++;
            return true;
        }
        return false;
    }

    public void JumpToLiveEdge()
    {
        cursor = frames.Count - 1;
    }
}
