using CSharpOS;

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
    private readonly Action<string>? submitStringInput;
    private readonly Action<int>? submitKey;
    private readonly Action? toggleDisk;
    private bool paused;
    private bool keyPassthrough;
    private string inputLine = "";

    public InteractionController(FrameHistory frames, bool interactive, int delayMs,
        Action toggleIo, Action cycleFocus, Action<int> submitInput,
        Action<string>? submitStringInput = null, Action<int>? submitKey = null,
        Action? toggleDisk = null)
    {
        this.frames = frames;
        this.interactive = interactive;
        this.delayMs = delayMs;
        this.toggleIo = toggleIo;
        this.cycleFocus = cycleFocus;
        this.submitInput = submitInput;
        this.submitStringInput = submitStringInput;
        this.submitKey = submitKey;
        this.toggleDisk = toggleDisk;
    }

    public bool Paused
    {
        get { return paused; }
    }

    public bool KeyPassthrough
    {
        get { return keyPassthrough; }
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
        // F1 toggles keyboard passthrough. In passthrough mode every key is forwarded
        // to the process buffer and visualizer shortcuts are suppressed. F1 itself is
        // never forwarded so it always works as the escape hatch.
        if (key.Key == ConsoleKey.F1)
        {
            keyPassthrough = !keyPassthrough;
            return StepAction.Redraw;
        }
        if (keyPassthrough)
        {
            if (key.Key == ConsoleKey.UpArrow)    { submitKey?.Invoke(Hardware.KeyUp);    return StepAction.Redraw; }
            if (key.Key == ConsoleKey.DownArrow)  { submitKey?.Invoke(Hardware.KeyDown);  return StepAction.Redraw; }
            if (key.Key == ConsoleKey.LeftArrow)  { submitKey?.Invoke(Hardware.KeyLeft);  return StepAction.Redraw; }
            if (key.Key == ConsoleKey.RightArrow) { submitKey?.Invoke(Hardware.KeyRight); return StepAction.Redraw; }
            if (key.Key == ConsoleKey.Escape)     { submitKey?.Invoke(27);  return StepAction.Redraw; }
            if (key.Key == ConsoleKey.Tab)        { submitKey?.Invoke(9);   return StepAction.Redraw; }
            if (key.Key == ConsoleKey.Enter)      { submitKey?.Invoke(13);  return StepAction.Redraw; }
            if (key.Key == ConsoleKey.Backspace)  { submitKey?.Invoke(8);   return StepAction.Redraw; }
            if (key.KeyChar >= ' ' && key.KeyChar <= '~')
            {
                submitKey?.Invoke(key.KeyChar);
                return StepAction.Redraw;
            }
            return StepAction.Redraw;
        }
        // UpArrow/DownArrow always go to the focused process as raw keys.
        if (key.Key == ConsoleKey.UpArrow)
        {
            submitKey?.Invoke(Hardware.KeyUp);
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            submitKey?.Invoke(Hardware.KeyDown);
            return StepAction.Redraw;
        }
        // LeftArrow/RightArrow: scrub history when paused; send to process when running.
        if (key.Key == ConsoleKey.LeftArrow)
        {
            if (paused)
            {
                frames.StepBack();
                return StepAction.Redraw;
            }
            submitKey?.Invoke(Hardware.KeyLeft);
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.RightArrow)
        {
            if (paused)
            {
                if (frames.StepForward())
                {
                    return StepAction.Redraw;
                }
                return StepAction.Execute; // at the live edge: advance the emulator
            }
            submitKey?.Invoke(Hardware.KeyRight);
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Tab)
        {
            cycleFocus();
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Escape)
        {
            submitKey?.Invoke(27);
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            submitKey?.Invoke(13);
            // Submit the accumulated input to the focused process: as an integer if all
            // digits, otherwise as a string line (for processes waiting on INS).
            if (inputLine.Length > 0)
            {
                if (int.TryParse(inputLine, out int value))
                {
                    submitInput(value);
                }
                else
                {
                    submitStringInput?.Invoke(inputLine);
                }
                inputLine = "";
            }
            return StepAction.Redraw;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            submitKey?.Invoke(8);
            if (inputLine.Length > 0)
            {
                inputLine = inputLine.Substring(0, inputLine.Length - 1);
            }
            return StepAction.Redraw;
        }
        // Printable characters: command shortcuts (a/s/o/q) only activate when the
        // input line is empty and consume the key — they are NOT forwarded to the process.
        // All other printable chars extend the input line and are forwarded via submitKey.
        if (key.KeyChar >= ' ' && key.KeyChar <= '~')
        {
            char lower = char.ToLowerInvariant(key.KeyChar);
            if (inputLine.Length == 0)
            {
                if (lower == 'a')
                {
                    paused = false;
                    frames.JumpToLiveEdge();
                    return StepAction.Redraw;
                }
                if (lower == 's')
                {
                    paused = true;
                    return StepAction.Redraw;
                }
                if (lower == 'o')
                {
                    toggleIo();
                    return StepAction.Redraw;
                }
                if (lower == 'd')
                {
                    toggleDisk?.Invoke();
                    return StepAction.Redraw;
                }
                if (lower == 'q')
                {
                    return StepAction.Quit;
                }
            }
            submitKey?.Invoke(key.KeyChar);
            inputLine += key.KeyChar;
            return StepAction.Redraw;
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
