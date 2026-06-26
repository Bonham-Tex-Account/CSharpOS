namespace CSharpOS;

/// <summary>
/// Layout of the OS in-memory region: an interrupt vector table, the assembled OS
/// routines, then the OS data structures (scheduler state, process table, buddy
/// allocator bitmap). Offsets are absolute addresses, since OS code runs in
/// Privileged mode with a program base of 0. Shared between the routine assembler
/// (which references these as immediates) and the C# side (which seeds the data).
/// </summary>
public static class OsLayout
{
    // Code begins right after the IVT; the data section sits at a fixed base so its
    // field offsets are compile-time constants the routines can load directly. The
    // base must clear the assembled routines; BuildOsImage guards against overrun.
    public const int CodeBase = Hardware.IvtSize;
    // The assembled OS routines (scheduler, allocator, disk, and the spawning family:
    // spawn/fork/exec/wait/exit) sit between CodeBase and DataBase; BuildOsImage guards
    // against overrun. Raised to 8192 once the spawning routines were added.
    public const int DataBase = 8192;

    // ---- scheduler state header (4-byte fields at the data section base) ---
    public const int ProcessCountOffset    = DataBase + 0;
    public const int CurrentIndexOffset    = DataBase + 4;   // -1 when the CPU is idle
    public const int BuddyHeapStartOffset  = DataBase + 8;   // start address of managed heap
    public const int BuddyHeapSizeOffset   = DataBase + 12;  // power-of-2 heap size
    public const int BoostTimerOffset      = DataBase + 16;  // MLFQ: ticks until global priority reset
    public const int QuantumTableOffset    = DataBase + 20;  // MLFQ: 4 × 4-byte tick thresholds per level
    public const int BuddyMinBlockOffset   = DataBase + 36;  // minimum allocatable block size (power of 2)
    public const int BuddyLevelsOffset     = DataBase + 40;  // tree depth: log2(HeapSize / MinBlock)
    public const int NextPidOffset         = DataBase + 44;  // monotonic PID counter (spawning)
    public const int KernelImageSlotOffset = DataBase + 48;  // disk slot holding the syscall image (EXEC re-loads it)

    // ---- MLFQ constants ---------------------------------------------------
    public const int QueueCount    = 4;
    public const int BoostInterval = 20;

    // ---- buddy allocator constants ----------------------------------------
    // Default minimum block size; stored in OS data at BuddyMinBlockOffset so
    // tests can override it per instance by writing a different value before
    // seeding. The ISA allocator reads this from memory rather than baking it in.
    public const int BuddyDefaultMinBlock = 256;
    // Fixed number of bitmap words loaded into R8-R15 on each allocator call.
    // 8 words × 32 bits = 256 bits → supports trees with up to 255 nodes
    // (8 levels, heap up to 255 × MinBlock bytes).
    public const int BuddyBitmapWords = 8;

    // ---- process table -----------------------------------------------------
    public const int MaxProcesses       = 8;
    public const int ProcessTableOffset = DataBase + 52;  // after header + buddy fields + NextPid + KernelImageSlot

    // ---- buddy bitmap (compact: 1 bit per tree node, bit=1 means FREE) ----
    // Stored as BuddyBitmapWords consecutive 4-byte words immediately after the
    // process table. Initially only the root bit (bit 0 of word 0) is set.
    public const int BuddyBitmapOffset = ProcessTableOffset + MaxProcesses * Hardware.ProcessEntrySize;

    // ---- privileged scratch stack -----------------------------------------
    // A small stack the privileged OS routines point ESP at so they can CALL/RET
    // shared subroutines (e.g. the buddy allocator). Safe as a single shared region
    // because privileged routines run atomically and never nest.
    public const int PrivilegedStackSize = 64;
    public const int PrivilegedStackOffset = BuddyBitmapOffset + BuddyBitmapWords * 4;
    public const int PrivilegedStackTop = PrivilegedStackOffset + PrivilegedStackSize;

    // Total OS region size.
    public const int TotalSize = PrivilegedStackTop;

    public static int ProcessEntryAddress(int index)
    {
        return ProcessTableOffset + index * Hardware.ProcessEntrySize;
    }
}
