using CSharpOS;
using CSharpOSConsole.Visualization;

namespace OSTests;

/// <summary>
/// Tests the time-travel backbone: FrameHistory captures snapshots and the cursor
/// scrubs backward/forward, and InteractionController maps keys to the right loop
/// action. Both are pure (no Spectre, no console), so they are fully deterministic.
/// </summary>
public class FrameHistoryTests
{
    private static VisualizerModel ModelAtStep(int step)
    {
        VisualizerModel model = new VisualizerModel();
        model.InstructionCount = step;
        return model;
    }

    [Fact]
    public void Capture_AdvancesCursorToLiveEdge()
    {
        FrameHistory frames = new FrameHistory();
        Assert.True(frames.IsEmpty);

        frames.Capture(ModelAtStep(1));
        frames.Capture(ModelAtStep(2));

        Assert.Equal(2, frames.Count);
        Assert.Equal(1, frames.Cursor);
        Assert.True(frames.AtLiveEdge);
        Assert.Equal(2, frames.Current!.StepNumber);
    }

    [Fact]
    public void StepBack_ThenStepForward_MovesCursorWithoutLosingFrames()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        frames.Capture(ModelAtStep(2));
        frames.Capture(ModelAtStep(3));

        Assert.True(frames.StepBack());
        Assert.Equal(1, frames.Cursor);
        Assert.False(frames.AtLiveEdge);
        Assert.Equal(2, frames.Current!.StepNumber);

        Assert.True(frames.StepBack());
        Assert.Equal(0, frames.Cursor);
        Assert.False(frames.StepBack()); // already at the oldest frame

        Assert.True(frames.StepForward());
        Assert.Equal(1, frames.Cursor);
        Assert.Equal(2, frames.Current!.StepNumber);
    }

    [Fact]
    public void StepForward_AtLiveEdge_ReturnsFalse()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        Assert.False(frames.StepForward());
        Assert.True(frames.AtLiveEdge);
    }

    [Fact]
    public void JumpToLiveEdge_ReturnsCursorToLatest()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        frames.Capture(ModelAtStep(2));
        frames.StepBack();
        Assert.False(frames.AtLiveEdge);

        frames.JumpToLiveEdge();

        Assert.True(frames.AtLiveEdge);
        Assert.Equal(2, frames.Current!.StepNumber);
    }

    [Fact]
    public void Capture_BeyondCapacity_DropsOldestAndKeepsLiveEdge()
    {
        FrameHistory frames = new FrameHistory(capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            frames.Capture(ModelAtStep(i));
        }
        Assert.Equal(3, frames.Count);
        Assert.True(frames.AtLiveEdge);
        Assert.Equal(5, frames.Current!.StepNumber); // newest retained
    }

    // ---- InteractionController key mapping --------------------------------

    private static ConsoleKeyInfo Key(ConsoleKey key)
    {
        return new ConsoleKeyInfo('\0', key, false, false, false);
    }

    private static ConsoleKeyInfo Char(char c)
    {
        return new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false);
    }

    private static (InteractionController Controller, FrameHistory Frames) NewController(int toggleSink)
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        frames.Capture(ModelAtStep(2));
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { }, value => { });
        return (controller, frames);
    }

    [Fact]
    public void LeftArrow_PausesAndStepsBack_RequestingRedraw()
    {
        (InteractionController controller, FrameHistory frames) = NewController(0);

        StepAction action = controller.HandleKey(Key(ConsoleKey.LeftArrow));

        Assert.Equal(StepAction.Redraw, action);
        Assert.True(controller.Paused);
        Assert.False(frames.AtLiveEdge); // moved into the past
    }

    [Fact]
    public void RightArrow_AtLiveEdge_RequestsExecute()
    {
        (InteractionController controller, FrameHistory frames) = NewController(0);
        // Already at live edge -> forward must advance the emulator.
        StepAction action = controller.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal(StepAction.Execute, action);
        Assert.True(controller.Paused);
    }

    [Fact]
    public void RightArrow_WhenReviewingHistory_RedrawsForward()
    {
        (InteractionController controller, FrameHistory frames) = NewController(0);
        controller.HandleKey(Key(ConsoleKey.LeftArrow)); // step into past

        StepAction action = controller.HandleKey(Key(ConsoleKey.RightArrow));

        Assert.Equal(StepAction.Redraw, action);
        Assert.True(frames.AtLiveEdge); // back at the edge, but via history (not execute)
    }

    [Fact]
    public void AutoKey_ResumesAndJumpsToLiveEdge()
    {
        (InteractionController controller, FrameHistory frames) = NewController(0);
        controller.HandleKey(Key(ConsoleKey.LeftArrow));
        Assert.True(controller.Paused);

        StepAction action = controller.HandleKey(Char('a'));

        Assert.Equal(StepAction.Redraw, action);
        Assert.False(controller.Paused);
        Assert.True(frames.AtLiveEdge);
    }

    [Fact]
    public void QuitKey_RequestsQuit()
    {
        (InteractionController controller, FrameHistory frames) = NewController(0);
        Assert.Equal(StepAction.Quit, controller.HandleKey(Char('q')));
    }

    [Fact]
    public void ToggleKey_InvokesToggleCallback()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        int toggles = 0;
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { toggles++; }, () => { }, value => { });

        StepAction action = controller.HandleKey(Char('o'));

        Assert.Equal(StepAction.Redraw, action);
        Assert.Equal(1, toggles);
    }

    // ---- focus + process input (shared screen) -----------------------------

    [Fact]
    public void Digits_AccumulateIntoTheInputLine()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        List<int> submitted = new List<int>();
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { }, value => { submitted.Add(value); });

        controller.HandleKey(Char('4'));
        controller.HandleKey(Char('2'));

        Assert.Equal("42", controller.InputLine);
        Assert.Empty(submitted); // not submitted until Enter
    }

    [Fact]
    public void Enter_SubmitsTheTypedNumber_AndClearsTheLine()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        List<int> submitted = new List<int>();
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { }, value => { submitted.Add(value); });

        controller.HandleKey(Char('4'));
        controller.HandleKey(Char('2'));
        StepAction action = controller.HandleKey(Key(ConsoleKey.Enter));

        Assert.Equal(StepAction.Redraw, action);
        Assert.Equal(new List<int> { 42 }, submitted);
        Assert.Equal("", controller.InputLine);
    }

    [Fact]
    public void Enter_WithNoTypedNumber_SubmitsNothing()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        List<int> submitted = new List<int>();
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { }, value => { submitted.Add(value); });

        controller.HandleKey(Key(ConsoleKey.Enter));

        Assert.Empty(submitted);
    }

    [Fact]
    public void Backspace_RemovesTheLastDigit()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { }, value => { });

        controller.HandleKey(Char('4'));
        controller.HandleKey(Char('2'));
        controller.HandleKey(Key(ConsoleKey.Backspace));

        Assert.Equal("4", controller.InputLine);
    }

    [Fact]
    public void Tab_InvokesTheCycleFocusCallback()
    {
        FrameHistory frames = new FrameHistory();
        frames.Capture(ModelAtStep(1));
        int cycles = 0;
        InteractionController controller = new InteractionController(frames, interactive: true, delayMs: 0,
            () => { }, () => { cycles++; }, value => { });

        StepAction action = controller.HandleKey(Key(ConsoleKey.Tab));

        Assert.Equal(StepAction.Redraw, action);
        Assert.Equal(1, cycles);
    }
}
