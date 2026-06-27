namespace CSharpOS;

/// <summary>
/// A 2-bit saturating-counter branch predictor backed by a branch history table (BHT)
/// indexed by branch address. Purely observational: it predicts the conditional jumps
/// (JZ/JNZ/JS/JNS), tracks accuracy, and never changes control flow, so the emulator's
/// behavior is unaffected. Counter states: 0 strongly-not-taken, 1 weakly-not-taken,
/// 2 weakly-taken, 3 strongly-taken; a branch is predicted taken when its counter is
/// >= <see cref="TakenThreshold"/>. Cold counters start at <see cref="WeakNotTaken"/>.
/// </summary>
public class BranchPredictor
{
    // ---- public constants ------------------------------------------------
    public const int DefaultSize = 64;        // BHT entries; must be a power of two
    public const int StrongNotTaken = 0;
    public const int WeakNotTaken = 1;        // cold-counter default
    public const int WeakTaken = 2;
    public const int StrongTaken = 3;
    public const int TakenThreshold = 2;      // predict taken when counter >= this

    // ---- private fields --------------------------------------------------
    private int[] table;
    private int size;

    // ---- public counters -------------------------------------------------
    public long Predictions { get; private set; }
    public long Hits { get; private set; }
    public long Misses { get; private set; }

    // ---- constructors ----------------------------------------------------
    public BranchPredictor() : this(DefaultSize)
    {
    }

    public BranchPredictor(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
        {
            throw new ArgumentException("BHT size must be a positive power of two.", nameof(size));
        }
        this.size = size;
        this.table = new int[size];
        for (int i = 0; i < size; i++)
        {
            this.table[i] = WeakNotTaken;
        }
    }

    // ---- integral functions ----------------------------------------------

    // Instructions are 4-byte aligned, so the low two address bits carry no
    // information; drop them, then mask to the table size (a power of two). Two
    // branch addresses that map to the same index alias onto one shared counter.
    private int IndexFor(int address)
    {
        return (address / 4) & (size - 1);
    }

    public int Size
    {
        get { return size; }
    }

    // The current 2-bit counter for a branch address (exposed for tests/visualizer).
    public int CounterAt(int address)
    {
        return table[IndexFor(address)];
    }

    // Predicts whether the branch at this address will be taken.
    public bool Predict(int address)
    {
        return table[IndexFor(address)] >= TakenThreshold;
    }

    // Nudges the 2-bit counter toward taken/not-taken, saturating at the ends.
    public void Update(int address, bool taken)
    {
        int index = IndexFor(address);
        if (taken)
        {
            if (table[index] < StrongTaken)
            {
                table[index]++;
            }
        }
        else
        {
            if (table[index] > StrongNotTaken)
            {
                table[index]--;
            }
        }
    }

    // Predict -> compare with the actual outcome -> update the counter, tracking
    // accuracy. Returns true if the prediction was correct (a hit).
    public bool Record(int address, bool taken)
    {
        bool predicted = Predict(address);
        Update(address, taken);
        Predictions++;
        bool hit = predicted == taken;
        if (hit)
        {
            Hits++;
        }
        else
        {
            Misses++;
        }
        return hit;
    }

    // Prediction accuracy in [0, 1]; 0 when nothing has been predicted yet.
    public double Accuracy
    {
        get
        {
            if (Predictions == 0)
            {
                return 0.0;
            }
            else
            {
                return (double)Hits / Predictions;
            }
        }
    }
}
