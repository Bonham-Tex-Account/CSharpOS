namespace CSharpOS;

/// <summary>
/// Raised after a user-mode conditional branch (JZ/JNZ/JS/JNS) is evaluated by the
/// branch predictor, carrying what was predicted, what actually happened, and whether
/// the prediction was a hit. Observational only — control flow is unaffected.
/// </summary>
public class BranchPredictedArgs : EventArgs
{
    public int Address { get; init; }
    public bool Predicted { get; init; }
    public bool Actual { get; init; }
    public bool Hit { get; init; }
}
