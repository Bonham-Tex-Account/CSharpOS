using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace CSharpOSConsole;

/// <summary>
/// A terminal backed by a separate console window: it launches this same program
/// in "--terminal" mode in its own window and talks to it over a named pipe.
/// Output is sent to the window ("OUT:&lt;value&gt;"); lines typed in the window come
/// back as integers and surface via <see cref="InputEntered"/>. This is the
/// integration layer (process + window spawning) and is not unit-tested; the
/// routing logic it plugs into is covered by ProcessIoRouter's tests.
/// </summary>
public sealed class ConsoleWindowTerminal : IProcessTerminal
{
    private readonly NamedPipeServerStream pipe;
    private readonly StreamWriter writer;
    private readonly StreamReader reader;
    private readonly Process child;
    private readonly Thread readerThread;
    private volatile bool closed;

    public event Action<int>? InputEntered;

    public ConsoleWindowTerminal(string title)
    {
        string pipeName = "csos_" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        child = StartTerminalProcess(pipeName, title);
        pipe.WaitForConnection();

        writer = new StreamWriter(pipe) { AutoFlush = true };
        reader = new StreamReader(pipe);

        readerThread = new Thread(ReadLoop) { IsBackground = true };
        readerThread.Start();
    }

    // Launches this program again in terminal mode, in a brand-new console window.
    // UseShellExecute = true is what gives a console child its own window on Windows.
    private static Process StartTerminalProcess(string pipeName, string title)
    {
        string processPath = Environment.ProcessPath ?? "dotnet";
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true
        };

        // When launched via `dotnet`, the entry assembly is a .dll that must be
        // passed as the first argument; a published apphost runs the exe directly.
        string fileName = Path.GetFileNameWithoutExtension(processPath);
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? entryDll = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(entryDll))
            {
                info.ArgumentList.Add(entryDll);
            }
        }
        info.ArgumentList.Add("--terminal");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(title);

        return Process.Start(info)!;
    }

    public void WriteOutput(int value)
    {
        if (closed)
        {
            return;
        }
        try
        {
            writer.WriteLine("OUT:" + value);
        }
        catch (IOException)
        {
            // The window was closed by the user; ignore.
        }
    }

    private void ReadLoop()
    {
        try
        {
            string? line;
            while (!closed && (line = reader.ReadLine()) != null)
            {
                if (int.TryParse(line.Trim(), out int value))
                {
                    InputEntered?.Invoke(value);
                }
            }
        }
        catch (IOException)
        {
            // Pipe broken (window closed); stop reading.
        }
    }

    public void Close()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        try
        {
            writer.WriteLine("BYE");
        }
        catch (IOException)
        {
            // Already gone.
        }
        try
        {
            pipe.Dispose();
        }
        catch (Exception)
        {
            // Best effort.
        }
        try
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
