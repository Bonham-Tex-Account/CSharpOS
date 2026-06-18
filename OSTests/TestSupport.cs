using CSharpOS;

namespace OSTests;

/// <summary>
/// Minimal IOperatingSystem test double that records the calls Hardware makes
/// against it, without performing any scheduling or memory management.
/// </summary>
internal sealed class FakeOS : IOperatingSystem
{
    public int AttachHardwareCount;
    public int ContextSwitchCount;
    public Hardware? LastAttachedHardware;

    public bool InvalidInstructionCalled;
    public byte LastOpcode;
    public byte LastB1;
    public byte LastB2;
    public byte LastB3;

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

    /// <summary>
    /// Encodes a single 4-byte instruction word.
    /// </summary>
    public static byte[] Word(byte opcode, byte b1, byte b2, byte b3)
    {
        return new byte[] { opcode, b1, b2, b3 };
    }
}
