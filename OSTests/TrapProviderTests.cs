using CSharpOS;

namespace OSTests;

// Tests for the ITrapProvider implementations and the CollectTraps reflection path in BasicOS.
// After the memory-protection rework only IretTrapProvider remains (the LOAD/STORE bounds
// providers were removed — the MMU is now the sole memory-protection mechanism; out-of-bounds
// user access is a protection fault, covered in PagingTests). The provider is internal to
// BasicOSPlugin, so it is exercised through BasicOS and Hardware rather than instantiated.
public class TrapProviderTests
{
    // ---- shared helpers -------------------------------------------------------

    // A BasicOS-backed Hardware with a process layout loaded (ProgramAddress = 200), so the
    // privilege-gated trap condition evaluates against a running process.
    private static Hardware BuildHardwareWithLayout()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 200;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);
        return hw;
    }

    // Writes one ISA instruction word at address and calls Instruction.Execute on it.
    private static bool ExecuteAt(Hardware hw, int address, byte opcode, byte b1, byte b2, byte b3)
    {
        hw.WriteBytes(address, Test.Word(opcode, b1, b2, b3));
        return Instruction.Execute(address, hw);
    }

    // ---- IretTrapProvider ----------------------------------------------------

    // EDGE CASE: IRET in User mode must trap (privileged instruction violation).
    [Fact]
    public void IretTrap_InUserMode_TrapsAndFiresInvalidInstruction()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.IRET) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        ExecuteAt(hw, 200, Instruction.IRET, 0, 0, 0);

        Assert.True(trapped);
    }

    // EDGE CASE: IRET in Kernel mode must NOT trap — the shared syscall handler and the OS
    // routines both use IRET to return to user code. Trapping here would kill every syscall.
    [Fact]
    public void IretTrap_InKernelMode_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, _) => { trapped = true; };

        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        ExecuteAt(hw, 200, Instruction.IRET, 0, 0, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: The IRET trap must carry a non-null, non-empty Reason string so faults are
    // diagnosable in logs.
    [Fact]
    public void IretTrap_FiredInUserMode_ReasonStringIsNonEmpty()
    {
        Hardware hw = BuildHardwareWithLayout();

        string? reason = null;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.IRET) { reason = e.Reason; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        ExecuteAt(hw, 200, Instruction.IRET, 0, 0, 0);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // ---- CollectTraps / BasicOS construction ---------------------------------

    // EDGE CASE: CollectTraps must discover IretTrapProvider (the sole remaining provider),
    // so IRET is guarded in User mode. If it were silently skipped, IRET would be unguarded.
    [Fact]
    public void BasicOs_CollectTraps_RegistersTheIretProvider()
    {
        bool iretTrapped = false;
        Hardware hw = BuildHardwareWithLayout();
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.InvalidInstruction += (_, e) => { if (e.Opcode == Instruction.IRET) { iretTrapped = true; } };
        ExecuteAt(hw, 200, Instruction.IRET, 0, 0, 0);

        Assert.True(iretTrapped);
    }

    // EDGE CASE: BasicOS construction (which calls CollectTraps by reflection) must not throw —
    // if a provider's parameterless constructor threw, it would crash the OS ctor at load time.
    [Fact]
    public void BasicOs_Construction_DoesNotThrow()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Assert.NotNull(os);
    }

    // EDGE CASE: CollectTraps reflection must skip the ITrapProvider interface itself (the
    // IsAbstract guard). If the interface were instantiated, Activator.CreateInstance would
    // throw; successful construction + attach proves only concrete providers were reflected.
    [Fact]
    public void BasicOs_CollectTraps_DoesNotAttemptToInstantiateITrapProviderInterface()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(8192), Test.AllRegisters(), os);
        Assert.NotNull(hw);
    }
}
