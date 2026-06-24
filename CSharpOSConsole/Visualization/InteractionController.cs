namespace CSharpOSConsole.Visualization;

/// <summary>What the run loop should do after consulting the controller.</summary>
public enum StepAction
{
    Execute, // advance the emulator one instruction (live edge)
    Redraw,  // cursor moved over recorded history / state changed; just repaint
    Quit     // user asked to stop
}

/// <summary>
/// Drives pacing and navigation for the live dashboard: auto-run vs. single-step, and
/// forward/backward scrubbing over <see cref="FrameHistory"/> with the arrow keys. The
/// key-to-action mapping lives in the pure <see cref="HandleKey"/> (unit-testable); the
/// console polling in <see cref="NextAction"/> is the only part that touches the keyboard.
///
/// Keys: a = auto-run, s = single-step (pause), o = toggle program-I/O mirror,
/// left = step back, right/Enter = step forward (executes at the live edge), q = quit.
/// </summary>
public sealed class InteractionController
{
    private readonly FrameHistory frames;
    private readonly bool interactive;
    private readonly int delayMs;
    private readonly Action toggleIo;
    private bool paused;

    public InteractionController(FrameHistory frames, bool interactive, int delayMs, Action toggleIo)
    {
        this.frames = frames;
        this.interactive = interactive;
        this.delayMs = delayMs;
        this.toggleIo = toggleIo;
    }

    public bool Paused
    {
        get { return paused; }
    }

    /// <summary>
    /// Pure key handler: maps a key to the loop action and updates the cursor / mode.
    /// Console-free so it can be unit-tested.
    /// </summary>
    public StepAction HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.LeftArrow)
        {
            paused = true;
            frames.StepBack();
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.RightArrow || key.Key == ConsoleKey.Enter)
        {
            paused = true;
            if (frames.StepForward())
            {
                return StepAction.Redraw;
            }
            return StepAction.Execute; // at the live edge: advance the emulator
        }

        char c = char.ToLowerInvariant(key.KeyChar);
        if (c == 'a')
        {
            paused = false;
            frames.JumpToLiveEdge();
            return StepAction.Redraw;
        }
        if (c == 's')
        {
            paused = true;
            return StepAction.Redraw;
        }
        if (c == 'o')
        {
            toggleIo();
            return StepAction.Redraw;
        }
        if (c == 'q')
        {
            return StepAction.Quit;
        }
        return StepAction.Redraw;
    }

    /// <summary>
    /// Consults the keyboard and decides the next loop action. In auto mode it advances
    /// after an optional delay unless a key intervenes; in single-step mode it blocks
    /// until the user presses a key.
    /// </summary>
    public StepAction NextAction()
    {
        if (!interactive)
        {
            return StepAction.Execute;
        }

        if (paused)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            return HandleKey(key);
        }

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            return HandleKey(key);
        }
        return StepAction.Execute;
    }
}
