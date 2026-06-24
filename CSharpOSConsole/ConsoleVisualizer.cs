using CSharpOS;
using CSharpOSConsole.Visualization;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace CSharpOSConsole;

/// <summary>
/// Coordinates the step-by-step visualization of execution. It wires a
/// <see cref="VisualizerModel"/> (the render-agnostic source of truth), a
/// <see cref="HardwareEventBridge"/> (translates Hardware events into model updates),
/// an <see cref="IVisualizerRenderer"/> (draws it), and a <see cref="Pacer"/>
/// (keyboard pacing). The default renderer is the streaming
/// <see cref="PlainTextRenderer"/>, which writes deterministic text to the injected
/// <see cref="TextWriter"/> — Console.Out in production, a StringWriter in tests.
/// </summary>
public sealed class ConsoleVisualizer
{
    private readonly VisualizerModel model;
    private readonly IVisualizerRenderer renderer;
    private readonly HardwareEventBridge bridge;
    private readonly Pacer pacer;

    public ConsoleVisualizer(Hardware hw, OperatingSystem os, int delayMs,
        TextWriter? output = null, bool useColor = true, bool interactive = true,
        VisualizerMode mode = VisualizerMode.Normal, bool showProgramIo = false)
    {
        TextWriter writer = output ?? Console.Out;
        model = new VisualizerModel { ShowProgramIo = showProgramIo };
        renderer = new PlainTextRenderer(writer, useColor, mode);
        pacer = new Pacer(writer, useColor, interactive, delayMs, ToggleProgramIo);
        bridge = new HardwareEventBridge(hw, os, model, renderer, pacer);
    }

    /// <summary>
    /// Whether program I/O (OUTPUT, and the value of an input interrupt) is mirrored
    /// into the OS/Hardware window. Off by default — I/O lives in the per-process
    /// windows; toggled live with the 'o' key.
    /// </summary>
    public bool ShowProgramIo
    {
        get { return model.ShowProgramIo; }
        set { model.ShowProgramIo = value; }
    }

    private void ToggleProgramIo()
    {
        model.ShowProgramIo = !model.ShowProgramIo;
        renderer.ProgramIoToggled(model.ShowProgramIo);
    }
}
