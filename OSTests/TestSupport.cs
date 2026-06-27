using CSharpOS;

namespace OSTests;

/// <summary>
/// Minimal IOperatingSystem test double that records the calls Hardware makes
/// against it, without performing any scheduling or memory management.
/// </summary>
internal sealed class FakeOS : IOperatingSystem
{
    public int OsMemorySize => 0;
    public byte[] BuildOsImage(int osMemoryBase) => Array.Empty<byte>();

    public int AttachHardwareCount;
    public int ContextSwitchCount;
    public Hardware? LastAttachedHardware;

    public bool InvalidInstructionCalled;
    public byte LastOpcode;
    public byte LastB1;
    public byte LastB2;
    public byte LastB3;

    // No OS image: the bare hardware harness used by low-level instruction tests.
    public bool HasProcesses { get; set; }

    public int LoadProcessCount;

    public void LoadProcess(Process process)
    {
        LoadProcessCount++;
    }

    public void AttachHardware(Hardware hw)
    {
        AttachHardwareCount++;
        LastAttachedHardware = hw;
    }

    public void ContextSwitch(Hardware hw)
    {
        ContextSwitchCount++;
    }

    public void HandleInvalidInstruction(Hardware hw, byte opcode, byte b1, byte b2, byte b3)
    {
        InvalidInstructionCalled = true;
        LastOpcode = opcode;
        LastB1 = b1;
        LastB2 = b2;
        LastB3 = b3;
    }

    public int HaltCount;

    public void HandleHalt(Hardware hw)
    {
        HaltCount++;
    }

    public int BlockCount;
    public WaitReason LastBlockReason;
    public int WakeCount;
    public WaitReason LastWakeReason;

    // Always "running" so plain hardware tests execute the instruction at the
    // current IP without needing a scheduler.
    public bool HasRunningProcess => true;

    public void BlockCurrentProcess(Hardware hw, WaitReason reason)
    {
        BlockCount++;
        LastBlockReason = reason;
    }

    public void Wake(WaitReason reason)
    {
        WakeCount++;
        LastWakeReason = reason;
    }

    public void Schedule(Hardware hw)
    {
    }
}

/// <summary>
/// Concrete OperatingSystem used to exercise the abstract base with a
/// caller-supplied trap table.
/// </summary>
internal sealed class TrappingOS : CSharpOS.OperatingSystem
{
    public TrappingOS(List<Trap> traps, TextWriter log) : base(traps, log)
    {
    }
}

internal static class Test
{
    // Minimum machine size that allows the OS to boot and run simple test processes.
    // TotalSize covers the OS image; the extra gives a heap large enough for basic scenarios.
    // Use this instead of hard-coding 4096 so tests survive layout growth.
    public static int MinMachineSize => OsLayout.TotalSize + 4096;

    // Machine size that gives exactly MaxProcesses leaf nodes (one leaf per process-table slot).
    // Useful for "fill the heap then fail" tests where leafCount must not exceed MaxProcesses.
    public static int FullHeapMachineSize => OsLayout.TotalSize + OsLayout.BuddyDefaultMinBlock * OsLayout.MaxProcesses;

    // Machine size for tests that load or hand-seed the OS region: the OS image plus
    // `heapBytes` of headroom for process memory and heap pokes. Sizing relative to
    // OsLayout.TotalSize (instead of a bare literal) means a future growth of the OS
    // region or kernel never silently outgrows a test's machine, and making a machine
    // bigger is a one-line change to the heapBytes argument rather than chasing
    // hard-coded sizes across the suite.
    public static int MachineWithHeap(int heapBytes)
    {
        return OsLayout.TotalSize + heapBytes;
    }

    /// <summary>
    /// Builds a Hardware instance with the full register set declared in RegisterName.
    /// </summary>
    public static Hardware NewHardware(int memorySize, IOperatingSystem os)
    {
        RegisterName[] registers = Enum.GetValues<RegisterName>();
        return new Hardware(memorySize, registers, os);
    }

    public static RegisterName[] AllRegisters()
    {
        return Enum.GetValues<RegisterName>();
    }

    // Width in bytes of a machine word / register / instruction. The ISA is fixed at
    // 4-byte words, so the register file size, per-register offsets, and instruction
    // stride are all multiples of this. Mirrors the 4-byte word the production
    // Hardware code uses throughout; kept here so the tests have a single source.
    public const int WordSize = 4;

    // EFLAGS bit masks. The corresponding ZeroFlag/SignFlag bits inside
    // InstructionFunctions are private, so they can't be shared across the assembly
    // boundary; these mirror them for the tests. Bit 0 = zero flag, bit 1 = sign flag.
    public const int ZeroFlagMask = 1;
    public const int SignFlagMask = 2;

    /// <summary>
    /// Total size in bytes of the full register file: one WordSize-wide slot per
    /// register declared in RegisterName.
    /// </summary>
    public static int RegisterFileBytes()
    {
        return AllRegisters().Length * WordSize;
    }

    /// <summary>
    /// Returns the EFLAGS zero flag as 0 or 1.
    /// </summary>
    public static int ZeroFlag(Hardware hw)
    {
        return hw.ReadRegister(RegisterName.EFLAGS) & ZeroFlagMask;
    }

    /// <summary>
    /// Returns the EFLAGS sign flag as 0 or 1.
    /// </summary>
    public static int SignFlag(Hardware hw)
    {
        return (hw.ReadRegister(RegisterName.EFLAGS) & SignFlagMask) >> 1;
    }

    /// <summary>
    /// Encodes a single 4-byte instruction word.
    /// </summary>
    public static byte[] Word(byte opcode, byte b1, byte b2, byte b3)
    {
        return new byte[] { opcode, b1, b2, b3 };
    }

    /// <summary>
    /// Reads a 4-byte little-endian word from hardware memory at the given address.
    /// </summary>
    public static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    /// <summary>
    /// Writes a value as a 4-byte little-endian word to hardware memory at the given address.
    /// </summary>
    public static void WriteWord(Hardware hw, int address, int value)
    {
        hw.WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }
}
