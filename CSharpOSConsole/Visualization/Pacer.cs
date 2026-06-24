namespace CSharpOSConsole.Visualization;

/// <summary>
/// Paces the streaming view: optional fixed delay between steps, and live keyboard
/// control (`s` to single-step, `a` to resume auto, `o` to toggle program I/O). A
/// no-op when not interactive, so captured/test runs are unaffected. Phase 3 replaces
/// this with an InteractionController that also supports backward stepping.
/// </summary>
public sealed class Pacer
{
    private readonly TextWriter output;
    private readonly bool useColor;
    private readonly bool interactive;
    private readonly int delayMs;
    private readonly Action toggleIo;
    private bool manual;

    public Pacer(TextWriter output, bool useColor, bool interactive, int delayMs, Action toggleIo)
    {
        this.output = output;
        this.useColor = useColor;
        this.interactive = interactive;
        this.delayMs = delayMs;
        this.toggleIo = toggleIo;
    }

    public void AfterStep()
    {
        if (manual)
        {
            SetColor(ConsoleColor.DarkGray);
            output.Write("      (Enter = step, a = auto, o = toggle I/O) ");
            ResetColor();
            ConsoleKeyInfo key = Console.ReadKey(true);
            output.WriteLine();
            if (key.KeyChar == 'a' || key.KeyChar == 'A')
            {
                manual = false;
            }
            else if (key.KeyChar == 'o' || key.KeyChar == 'O')
            {
                toggleIo();
            }
            return;
        }

        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }
        if (interactive && !Console.IsInputRedirected && Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.KeyChar == 's' || key.KeyChar == 'S')
            {
                manual = true;
            }
            else if (key.KeyChar == 'o' || key.KeyChar == 'O')
            {
                toggleIo();
            }
        }
    }

    private void SetColor(ConsoleColor color)
    {
        if (useColor)
        {
            Console.ForegroundColor = color;
        }
    }

    private void ResetColor()
    {
        if (useColor)
        {
            Console.ResetColor();
        }
    }
}
