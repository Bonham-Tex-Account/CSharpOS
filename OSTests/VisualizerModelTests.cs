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
    public void RecordOutput_AppendsPerSourceProcess()
    {
        VisualizerModel model = new VisualizerModel();
        model.RecordOutput(0, 11);
        model.RecordOutput(1, 22);
        model.RecordOutput(0, 33);

        Assert.Equal(new List<string> { "11", "33" }, model.OutputBuffers[0]);
        Assert.Equal(new List<string> { "22" }, model.OutputBuffers[1]);
    }

    [Fact]
    public void FocusedOutput_ReturnsTheFocusedProcessBuffer()
    {
        VisualizerModel model = new VisualizerModel();
        model.RecordOutput(0, 11);
        model.RecordOutput(1, 22);

        model.FocusedProcess = 1;
        Assert.Equal(new List<string> { "22" }, model.FocusedOutput());

        model.FocusedProcess = 0;
        Assert.Equal(new List<string> { "11" }, model.FocusedOutput());
    }

    [Fact]
    public void FocusedOutput_IsEmptyWhenNothingFocusedOrNoOutput()
    {
        VisualizerModel model = new VisualizerModel();
        Assert.Empty(model.FocusedOutput()); // FocusedProcess == -1 by default

        model.FocusedProcess = 5; // focused but never produced output
        Assert.Empty(model.FocusedOutput());
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
