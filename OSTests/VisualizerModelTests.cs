using CSharpOS;
using CSharpOSConsole.Visualization;

namespace OSTests;

/// <summary>
/// Deterministic tests for VisualizerModel's instruction history cap and
/// per-instruction accounting. No Hardware or Spectre dependency required.
/// </summary>
public class VisualizerModelTests
{
    private static InstructionStep MakeStep(string mnemonic, string process = "p")
    {
        return new InstructionStep
        {
            Address = 0,
            Mnemonic = mnemonic,
            Privilege = PrivilegeLevel.User,
            Process = process,
            Registers = null!
        };
    }

    [Fact]
    public void History_ExactlyAtCap_RetainsAllEntries()
    {
        VisualizerModel model = new VisualizerModel();
        for (int i = 0; i < VisualizerModel.MaxHistoryLength; i++)
        {
            model.RecordInstruction(MakeStep("NOP"));
        }
        Assert.Equal(VisualizerModel.MaxHistoryLength, model.History.Count);
    }

    [Fact]
    public void History_OneOverCap_EvictsOldestEntry()
    {
        VisualizerModel model = new VisualizerModel();
        model.RecordInstruction(MakeStep("FIRST"));
        for (int i = 1; i < VisualizerModel.MaxHistoryLength; i++)
        {
            model.RecordInstruction(MakeStep("NOP"));
        }
        // Exactly at cap; FIRST is still present.
        Assert.Equal("FIRST", model.History[0].Mnemonic);

        // One more pushes FIRST out.
        model.RecordInstruction(MakeStep("LAST"));

        Assert.Equal(VisualizerModel.MaxHistoryLength, model.History.Count);
        Assert.Equal("NOP", model.History[0].Mnemonic);
        Assert.Equal("LAST", model.History[model.History.Count - 1].Mnemonic);
    }

    [Fact]
    public void History_ManyOverCap_NeverExceedsMaxHistoryLength()
    {
        VisualizerModel model = new VisualizerModel();
        int total = VisualizerModel.MaxHistoryLength * 3;
        for (int i = 0; i < total; i++)
        {
            model.RecordInstruction(MakeStep("NOP"));
        }
        Assert.Equal(VisualizerModel.MaxHistoryLength, model.History.Count);
    }

    [Fact]
    public void InstructionCount_IncrementsForEveryStep_EvenAfterHistoryTruncates()
    {
        VisualizerModel model = new VisualizerModel();
        int total = VisualizerModel.MaxHistoryLength + 10;
        for (int i = 0; i < total; i++)
        {
            model.RecordInstruction(MakeStep("NOP"));
        }
        Assert.Equal(total, model.InstructionCount);
    }

    [Fact]
    public void InstructionsByProcess_TracksCountPerProcess()
    {
        VisualizerModel model = new VisualizerModel();
        model.RecordInstruction(MakeStep("NOP", "a.bin"));
        model.RecordInstruction(MakeStep("NOP", "a.bin"));
        model.RecordInstruction(MakeStep("NOP", "b.bin"));

        Assert.Equal(2, model.InstructionsByProcess["a.bin"]);
        Assert.Equal(1, model.InstructionsByProcess["b.bin"]);
    }
}
