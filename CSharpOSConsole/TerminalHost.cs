using System.IO.Pipes;

namespace CSharpOSConsole;

/// <summary>
/// The child-window side of a <see cref="ConsoleWindowTerminal"/>. Runs in its own
/// console window (this program launched with "--terminal &lt;pipe&gt; &lt;title&gt;"),
/// connects back to the emulator over the named pipe, prints the process's output,
/// and forwards numbers the user types as input for that process.
/// </summary>
public static class TerminalHost
{
    public static void Run(string pipeName, string title)
    {
        TrySetTitle(title);
        Console.WriteLine("=== " + title + " : process I/O ===");
        Console.WriteLine("Type a number and press Enter to send input to this process.");
        Console.WriteLine();

        using NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect();

        StreamReader reader = new StreamReader(pipe);
        StreamWriter writer = new StreamWriter(pipe) { AutoFlush = true };

        // Background: messages from the emulator -> this window.
        Thread fromEmulator = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "BYE")
                    {
                        break;
                    }
                    if (line.StartsWith("OUT:", StringComparison.Ordinal))
                    {
                        Console.WriteLine("  >> OUTPUT: " + line.Substring(4));
                    }
                }
            }
            catch (IOException)
            {
                // Emulator closed the pipe.
            }
            Console.WriteLine();
            Console.WriteLine("--- process finished; press Enter to close ---");
        })
        { IsBackground = true };
        fromEmulator.Start();

        // Foreground: this window's input -> the emulator.
        try
        {
            string? input;
            while ((input = Console.ReadLine()) != null)
            {
                if (int.TryParse(input.Trim(), out int value))
                {
                    writer.WriteLine(value);
                }
                else
                {
                    Console.WriteLine("  (please enter a whole number)");
                }
            }
        }
        catch (IOException)
        {
            // Pipe closed.
        }
    }

    private static void TrySetTitle(string title)
    {
        try
        {
            Console.Title = title;
        }
        catch (Exception)
        {
            // Console title is not settable on every platform; ignore.
        }
    }
}
