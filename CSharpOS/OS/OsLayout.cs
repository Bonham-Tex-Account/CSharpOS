namespace CSharpOS;

/// <summary>
/// Layout of the OS in-memory region: an interrupt vector table, the assembled OS
/// routines, then the OS data structures (scheduler state, process table, free
/// list, pending queue). Offsets are absolute addresses, since OS code runs in
/// Privileged mode with a program base of 0. Shared between the routine assembler
/// (which references these as immediates) and the C# side (which seeds the data).
/// </summary>
public static class OsLayout
{
    // Code begins right after the IVT; the data section sits at a fixed base so its
    // field offsets are compile-time constants the routines can load directly. The
    // base must clear the assembled routines (~1KB); BuildOsImage guards against
    // overrun, so this is kept just above the code to conserve memory.
    public const int CodeBase = Hardware.IvtSize;
    public const int DataBase = 2048;

    // ---- scheduler state header (4-byte fields at the data section base) ---
    public const int ProcessCountOffset   = DataBase + 0;
    public const int CurrentIndexOffset   = DataBase + 4;   // -1 when the CPU is idle
    public const int FreeRangeCountOffset = DataBase + 8;
    public const int PendingCountOffset   = DataBase + 12;
    public const int BoostTimerOffset     = DataBase + 16;  // MLFQ: ticks until global priority reset
    public const int QuantumTableOffset   = DataBase + 20;  // MLFQ: 4 × 4-byte tick thresholds per level

    // ---- MLFQ constants ---------------------------------------------------
    public const int QueueCount    = 4;
    public const int BoostInterval = 20;

    // ---- process table -----------------------------------------------------
    public const int MaxProcesses       = 8;
    public const int ProcessTableOffset = DataBase + 36;  // after header + boost timer + quantum table

    // ---- free memory ranges (Start:4, Size:4 each) -------------------------
    public const int MaxFreeRanges       = 16;
    public const int FreeRangeTableOffset = ProcessTableOffset + MaxProcesses * Hardware.ProcessEntrySize;
    public const int FreeRangeSize        = 8;

    // ---- pending queue (process-table indices awaiting activation) ---------
    public const int MaxPending        = 8;
    public const int PendingQueueOffset = FreeRangeTableOffset + MaxFreeRanges * FreeRangeSize;

    // Total OS region size: everything above plus the pending queue.
    public const int TotalSize = PendingQueueOffset + MaxPending * 4;

    public static int ProcessEntryAddress(int index)
    {
        return ProcessTableOffset + index * Hardware.ProcessEntrySize;
    }
}
