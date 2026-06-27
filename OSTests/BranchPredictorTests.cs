using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the 2-bit saturating branch predictor (BHT) in isolation and its integration
/// with Hardware: user-only scoring, the observational misprediction cycle penalty, the
/// BranchPredicted event, and that control flow is unchanged (behavior-preserving).
/// </summary>
public class BranchPredictorTests
{
    // ---- predictor unit behavior -----------------------------------------

    [Fact]
    public void ColdCounter_IsWeakNotTaken_PredictsNotTaken()
    {
        BranchPredictor predictor = new BranchPredictor(8);
        Assert.Equal(BranchPredictor.WeakNotTaken, predictor.CounterAt(0));
        Assert.False(predictor.Predict(0));
    }

    [Fact]
    public void Counter_SaturatesAtStrongTaken()
    {
        BranchPredictor predictor = new BranchPredictor(8);
        for (int i = 0; i < 10; i++)
        {
            predictor.Update(0, true);
        }
        Assert.Equal(BranchPredictor.StrongTaken, predictor.CounterAt(0));
        Assert.True(predictor.Predict(0));
    }

    [Fact]
    public void Counter_SaturatesAtStrongNotTaken()
    {
        BranchPredictor predictor = new BranchPredictor(8);
        for (int i = 0; i < 10; i++)
        {
            predictor.Update(0, false);
        }
        Assert.Equal(BranchPredictor.StrongNotTaken, predictor.CounterAt(0));
        Assert.False(predictor.Predict(0));
    }

    [Fact]
    public void PredictThreshold_NotTakenBelowTwo_TakenAtOrAboveTwo()
    {
        BranchPredictor predictor = new BranchPredictor(8);

        // Drive the counter to each of the four states and check the prediction.
        for (int i = 0; i < 3; i++) { predictor.Update(0, false); }   // 0: strong NT
        Assert.False(predictor.Predict(0));
        predictor.Update(0, true);                                    // 1: weak NT
        Assert.False(predictor.Predict(0));
        predictor.Update(0, true);                                    // 2: weak T
        Assert.True(predictor.Predict(0));
        predictor.Update(0, true);                                    // 3: strong T
        Assert.True(predictor.Predict(0));
    }

    [Fact]
    public void Record_TracksHitsMissesAndPredictions()
    {
        BranchPredictor predictor = new BranchPredictor(8);

        // Cold predicts not-taken; an actual "taken" is a miss.
        Assert.False(predictor.Record(0, true));
        // Counter is now weak-taken (2); a second "taken" is a hit.
        Assert.True(predictor.Record(0, true));

        Assert.Equal(2, predictor.Predictions);
        Assert.Equal(1, predictor.Hits);
        Assert.Equal(1, predictor.Misses);
    }

    [Fact]
    public void Accuracy_IsZeroWithNoPredictions_AndRatioOtherwise()
    {
        BranchPredictor predictor = new BranchPredictor(8);
        Assert.Equal(0.0, predictor.Accuracy);

        predictor.Record(0, true);   // miss  (cold predicts not-taken)
        predictor.Record(0, true);   // hit
        predictor.Record(0, true);   // hit
        Assert.Equal(3, predictor.Predictions);
        Assert.Equal(2.0 / 3.0, predictor.Accuracy, 10);
    }

    [Fact]
    public void TwoBitHysteresis_StrongTakenAbsorbsOneOppositeOutcome()
    {
        BranchPredictor predictor = new BranchPredictor(8);
        predictor.Update(0, true);   // 1 -> 2
        predictor.Update(0, true);   // 2 -> 3 (strong taken)
        Assert.True(predictor.Predict(0));

        predictor.Update(0, false);  // 3 -> 2: prediction NOT flipped yet
        Assert.True(predictor.Predict(0));
        predictor.Update(0, false);  // 2 -> 1: now flips to not-taken
        Assert.False(predictor.Predict(0));
    }

    [Fact]
    public void LoopBranch_MispredictsOncePerExitAfterWarmup()
    {
        BranchPredictor predictor = new BranchPredictor(8);

        // Warm up the loop branch to strongly-taken.
        for (int i = 0; i < 3; i++) { predictor.Record(0, true); }

        long missesBefore = predictor.Misses;

        // Three loop runs: each is several taken iterations then one not-taken exit.
        for (int loop = 0; loop < 3; loop++)
        {
            for (int iter = 0; iter < 5; iter++)
            {
                predictor.Record(0, true);   // loop body: predicted taken -> hit
            }
            predictor.Record(0, false);      // loop exit: the only mispredict
        }

        Assert.Equal(3, predictor.Misses - missesBefore);
    }

    [Fact]
    public void Aliasing_TwoAddressesAtSameIndexShareACounter()
    {
        BranchPredictor predictor = new BranchPredictor(4);

        // index = (addr / 4) & 3, so addr 0 and addr 16 both map to index 0.
        int addrA = 0;
        int addrB = 16;
        Assert.Equal(predictor.CounterAt(addrA), predictor.CounterAt(addrB));

        predictor.Update(addrA, true);
        Assert.Equal(predictor.CounterAt(addrA), predictor.CounterAt(addrB));
        Assert.Equal(BranchPredictor.WeakTaken, predictor.CounterAt(addrB));
    }

    [Fact]
    public void Constructor_RejectsNonPowerOfTwoSize()
    {
        Assert.Throws<ArgumentException>(() => new BranchPredictor(0));
        Assert.Throws<ArgumentException>(() => new BranchPredictor(48));
        Assert.Throws<ArgumentException>(() => new BranchPredictor(-8));
    }

    // ---- Hardware integration --------------------------------------------

    // Places a JZ at `address` whose target is `target`, sets/clears the zero flag, and
    // executes it directly (no run loop), returning the hardware.
    private static Hardware ExecuteConditionalJump(byte opcode, int address, int target, bool flagSet, int flagMask, PrivilegeLevel level)
    {
        Hardware hw = Test.NewHardware(512, new FakeOS());
        hw.SetPrivilegeLevel(level);

        int flags = hw.ReadRegister(RegisterName.EFLAGS);
        if (flagSet)
        {
            flags |= flagMask;
        }
        else
        {
            flags &= ~flagMask;
        }
        hw.WriteRegister(RegisterName.EFLAGS, flags);

        hw.WriteBytes(address, Test.Word(opcode, (byte)((target >> 8) & 0xFF), (byte)(target & 0xFF), 0));
        Instruction.Execute(address, hw);
        return hw;
    }

    [Fact]
    public void UserModeBranch_IsScoredByThePredictor()
    {
        // JZ with the zero flag set is "taken"; cold predicts not-taken, so this records
        // a prediction (a miss) on a user-mode branch.
        Hardware hw = ExecuteConditionalJump(Instruction.JZ, 100, 40, true, Test.ZeroFlagMask, PrivilegeLevel.User);

        BranchPredictor predictor = hw.GetBranchPredictor();
        Assert.Equal(1, predictor.Predictions);
        Assert.Equal(1, predictor.Misses);
    }

    [Fact]
    public void KernelModeBranch_IsNotScored()
    {
        // The same branch in Kernel mode (OS code / syscall handler) must not pollute the
        // user-program stats.
        Hardware hw = ExecuteConditionalJump(Instruction.JZ, 100, 40, true, Test.ZeroFlagMask, PrivilegeLevel.Kernel);

        BranchPredictor predictor = hw.GetBranchPredictor();
        Assert.Equal(0, predictor.Predictions);
    }

    [Fact]
    public void Misprediction_AddsAFixedCyclePenalty_HitAddsNone()
    {
        Hardware hw = Test.NewHardware(512, new FakeOS());
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegister(RegisterName.EFLAGS, hw.ReadRegister(RegisterName.EFLAGS) | Test.ZeroFlagMask);

        // First taken JZ at address A: cold predicts not-taken -> miss -> a penalty.
        // (Direct Execute does not add baseline cycles, so cycles == the penalty alone.)
        int addrA = 100;
        hw.WriteBytes(addrA, Test.Word(Instruction.JZ, 0, 40, 0));
        Instruction.Execute(addrA, hw);
        long oneMissPenalty = hw.GetCycles();
        Assert.True(oneMissPenalty > 0);

        // Re-running the same branch (now weak-taken) with the zero flag still set is a
        // hit -> no additional cycles.
        Instruction.Execute(addrA, hw);
        Assert.Equal(oneMissPenalty, hw.GetCycles());

        // A second fresh, cold branch at a different index also mispredicts -> the total
        // penalty is exactly twice one miss (a fixed per-miss penalty, no magic number).
        int addrB = 200;
        hw.WriteBytes(addrB, Test.Word(Instruction.JZ, 0, 40, 0));
        Instruction.Execute(addrB, hw);
        Assert.Equal(2 * oneMissPenalty, hw.GetCycles());
    }

    [Fact]
    public void BranchPredicted_EventCarriesPredictionOutcome()
    {
        Hardware hw = Test.NewHardware(512, new FakeOS());
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegister(RegisterName.EFLAGS, hw.ReadRegister(RegisterName.EFLAGS) | Test.ZeroFlagMask);

        BranchPredictedArgs? captured = null;
        hw.BranchPredicted += (object? sender, BranchPredictedArgs e) => { captured = e; };

        int address = 100;
        hw.WriteBytes(address, Test.Word(Instruction.JZ, 0, 40, 0));
        Instruction.Execute(address, hw);

        Assert.NotNull(captured);
        Assert.Equal(address, captured!.Address);
        Assert.False(captured.Predicted); // cold -> predicted not-taken
        Assert.True(captured.Actual);     // zero flag set -> actually taken
        Assert.False(captured.Hit);       // predicted != actual
    }

    [Fact]
    public void BranchPrediction_IsBehaviorPreserving_TakenJumps_NotTakenFallsThrough()
    {
        // Taken: the IP moves to programBase + target.
        Hardware takenHw = ExecuteConditionalJump(Instruction.JZ, 100, 40, true, Test.ZeroFlagMask, PrivilegeLevel.User);
        Assert.Equal(takenHw.GetProgramBase() + 40, takenHw.GetInstructionPointer());

        // Not taken: the handler leaves the IP untouched (direct Execute does not advance it).
        Hardware notTakenHw = Test.NewHardware(512, new FakeOS());
        notTakenHw.SetPrivilegeLevel(PrivilegeLevel.User);
        notTakenHw.WriteRegister(RegisterName.EFLAGS, notTakenHw.ReadRegister(RegisterName.EFLAGS) & ~Test.ZeroFlagMask);
        notTakenHw.SetInstructionPointer(100);
        notTakenHw.WriteBytes(100, Test.Word(Instruction.JZ, 0, 40, 0));
        Instruction.Execute(100, notTakenHw);
        Assert.Equal(100, notTakenHw.GetInstructionPointer());
    }
}
