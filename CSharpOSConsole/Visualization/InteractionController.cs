namespace CSharpOSConsole.Visualization;

/// <summary>What the run loop should do after consulting the controller.</summary>
public enum StepAction
{
    Execute, // advance the emulator one instruction (live edge)
    Redraw,  // cursor moved over recorded history / state changed; just repaint
    Quit     // user asked to stop
}

/// <summary>
/// Drives pacing, navigation, focus, and process input for the live dashboard. It
/// pairs auto-run vs. single-step and forward/backward scrubbing over
/// <see cref="FrameHistory"/> with the shared-screen interaction: Tab cycles the
/// focused process, digits build a number, and Enter submits it to the focused
/// process. The key-to-action mapping lives in the pure <see cref="HandleKey"/>
/// (unit-testable); the console polling in <see cref="NextAction"/> is the only part
/// that touches the keyboard.
///
/// Keys: a = auto-run, s = single-step (pause), o = toggle program-I/O mirror,
/// left = step back, right = step forward (executes at the live edge), Tab = switch
/// focus, digits + Enter = send a number to the focused process, Backspace = edit the
/// number, q = quit.
/// </summary>
public sealed class InteractionController
{
    private readonly FrameHistory frames;
    private readonly bool interactive;
    private readonly int delayMs;
    private readonly Action toggleIo;
    private readonly Action cycleFocus;
    private readonly Action<int> submitInput;
    private bool paused;
    private string inputLine = "";

    public InteractionController(FrameHistory frames, bool interactive, int delayMs,
        Action toggleIo, Action cycleFocus, Action<int> submitInput)
    {
        this.frames = frames;
        this.interactive = interactive;
        this.delayMs = delayMs;
        this.toggleIo = toggleIo;
        this.cycleFocus = cycleFocus;
        this.submitInput = submitInput;
    }

    public bool Paused
    {
        get { return paused; }
    }

    /// <summary>The digits typed but not yet submitted, shown as the screen's input line.</summary>
    public string InputLine
    {
        get { return inputLine; }
    }

    /// <summary>
    /// Pure key handler: maps a key to the loop action and updates the cursor / mode /
    /// pending input. Console-free so it can be unit-tested.
    /// </summary>
    public StepAction HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.LeftArrow)
        {
            paused = true;
            frames.StepBack();
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            paused = true;
            if (frames.StepForward())
            {
                return StepAction.Redraw;
            }
            return StepAction.Execute; // at the live edge: advance the emulator
        }
        if (key.Key == ConsoleKey.Tab)
        {
            cycleFocus();
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            // Submit the accumulated number to the focused process, if any.
            if (inputLine.Length > 0)
            {
                if (int.TryParse(inputLine, out int value))
                {
                    submitInput(value);
                }
                inputLine = "";
            }
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (inputLine.Length > 0)
            {
                inputLine = inputLine.Substring(0, inputLine.Length - 1);
            }
            return StepAction.Redraw;
        }
        if (key.KeyChar >= '0' && key.KeyChar <= '9')
        {
            inputLine += key.KeyChar;
            return StepAction.Redraw;
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
