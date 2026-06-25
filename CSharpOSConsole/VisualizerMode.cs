namespace CSharpOSConsole;

/// <summary>
/// How much detail the ConsoleVisualizer renders. Higher tiers are supersets of
/// lower ones. Named OS routines, context switches, output, block/wake and faults
/// show in every tier; the heavier streams are added as the tier rises.
/// </summary>
public enum VisualizerMode
{
    // OS-level narrative only: OS routines, context switches, output, block/wake,
    // faults. No per-instruction stream, tables, memory writes, or privilege lines.
    Minimal = 0,

    // Adds the instruction stream + register snapshots, the process table, and the
    // free-memory map.
    Normal = 1,

    // Adds program memory writes and privilege transitions (syscall trap / IRET /
    // process resume).
    Verbose = 2
}

/// <summary>
/// Controls how often the dashboard redraws and how much data is captured per
/// instruction. Lower tiers reduce CPU overhead at the cost of display fidelity;
/// higher tiers show the most detail but are slower.
/// </summary>
public enum DetailLevel
{
    // Render every 10 steps; skip register capture and disassembly entirely.
    Low    = 0,

    // Render every 3 steps; capture registers but skip disassembly.
    Medium = 1,

    // Render every step; full register capture and disassembly (current behavior).
    High   = 2
}
