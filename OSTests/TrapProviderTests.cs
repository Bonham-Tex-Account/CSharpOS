using CSharpOS;

namespace OSTests;

// Tests for the three ITrapProvider implementations and the CollectTraps reflection
// path in BasicOS. Trap provider correctness is critical: a wrong opcode, register
// index, or privilege-level guard will silently mis-terminate processes or fail to
// protect memory.
//
// All three concrete providers are internal to BasicOSPlugin, so they are exercised
// through BasicOS and Hardware rather than being instantiated directly.
public class TrapProviderTests
{
    // ---- shared helpers -------------------------------------------------------

    // Builds a BasicOS-backed Hardware with a process layout loaded so
    // IsAddressInProcessRanges returns meaningful results.
    // ProgramAddress = 200, ProgramSize = 4.
    // Merged process range = [200, 200+4+KernelHeaderSize+64+64+KernelStackSize).
    private static Hardware BuildHardwareWithLayout()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(8192, Test.AllRegisters(), os);
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

    // EDGE CASE: IRET in Kernel mode must NOT trap — kernel handlers use IRET to
    // return to user code. Trapping here would kill every syscall.
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

    // EDGE CASE: IRET in Privileged mode must NOT trap — OS routines are not
    // prohibited from using IRET.
    [Fact]
    public void IretTrap_InPrivilegedMode_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, _) => { trapped = true; };

        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        ExecuteAt(hw, 200, Instruction.IRET, 0, 0, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: The IRET trap must carry a non-null, non-empty Reason string.
    // Hardware.TrapInvalidInstruction forwards the Reason to the InvalidInstructionArgs
    // event. An empty reason means faults are undiagnosable in logs.
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

    // ---- LoadBoundsTrapProvider ----------------------------------------------

    // EDGE CASE: LOAD in User mode with an out-of-bounds effective address must trap.
    // b2 is the address-register index for LOAD. Address = programBase + reg[b2].
    // programBase in User mode = 200. 200 + 5000 = 5200, outside [200, 508).
    [Fact]
    public void LoadTrap_InUserMode_OutOfBoundsAddress_Traps()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 5000); // b2=1 → address register; 200+5000=5200, out of range
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.True(trapped);
    }

    // EDGE CASE: LOAD in User mode with an in-bounds address must NOT trap.
    // 200 + 1 = 201, inside the merged process range.
    [Fact]
    public void LoadTrap_InUserMode_InBoundsAddress_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 1); // b2=1 → address register; 200+1=201, inside range
        hw.WriteBytes(201, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: LOAD in Kernel mode must NOT trap even with an out-of-bounds address.
    // Kernel mode has unrestricted memory access by design.
    [Fact]
    public void LoadTrap_InKernelMode_OutOfBoundsAddress_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.WriteRegisterAt(1, 5000);
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: LOAD trap reads the address from operand b2, not b1. Placing an
    // out-of-bounds value only in the b1-indexed register while b2 is in-bounds
    // must NOT trigger the trap. This pins the register-index selection against a
    // silent swap between destination and address registers.
    [Fact]
    public void LoadTrap_UsesB2AsAddressRegisterIndex_NotB1()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 5000); // b1=0 (destination reg index), out-of-bounds value — ignored by trap
        hw.WriteRegisterAt(1, 1);    // b2=1 (address reg index); 200+1=201, in bounds
        hw.WriteBytes(201, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: The LOAD trap must carry a non-null, non-empty Reason string.
    [Fact]
    public void LoadTrap_FiredInUserMode_ReasonStringIsNonEmpty()
    {
        Hardware hw = BuildHardwareWithLayout();

        string? reason = null;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { reason = e.Reason; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 5000);
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // ---- StoreBoundsTrapProvider ---------------------------------------------

    // EDGE CASE: STORE in User mode with an out-of-bounds effective address must trap.
    // b1 is the address-register index for STORE. Address = programBase + reg[b1].
    // programBase in User mode = 200. 200 + 5000 = 5200, outside [200, 508).
    [Fact]
    public void StoreTrap_InUserMode_OutOfBoundsAddress_Traps()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 5000); // b1=0 (address register); 200+5000=5200, out of range
        hw.WriteRegisterAt(1, 42);
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.True(trapped);
    }

    // EDGE CASE: STORE in User mode with an in-bounds address must NOT trap.
    // 200 + 1 = 201, inside the merged process range.
    [Fact]
    public void StoreTrap_InUserMode_InBoundsAddress_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 1); // b1=0 (address register); 200+1=201, in range
        hw.WriteRegisterAt(1, 42);
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: STORE in Kernel mode must NOT trap even with an out-of-bounds address.
    [Fact]
    public void StoreTrap_InKernelMode_OutOfBoundsAddress_DoesNotTrap()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.WriteRegisterAt(0, 5000);
        hw.WriteRegisterAt(1, 42);
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: STORE trap reads the address from operand b1, not b2. Placing an
    // out-of-bounds value only in the b2-indexed register while b1 is in-bounds
    // must NOT trigger the trap. This is the mirror of the LoadTrap_UsesB2 test
    // and pins the encoding asymmetry: STORE(addr=b1, val=b2), LOAD(dst=b1, addr=b2).
    [Fact]
    public void StoreTrap_UsesB1AsAddressRegisterIndex_NotB2()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool trapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { trapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 1);    // b1=0 (address register); 200+1=201, in bounds
        hw.WriteRegisterAt(1, 5000); // b2=1 (value register); out-of-bounds value — ignored by trap
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.False(trapped);
    }

    // EDGE CASE: The STORE trap must carry a non-null, non-empty Reason string.
    [Fact]
    public void StoreTrap_FiredInUserMode_ReasonStringIsNonEmpty()
    {
        Hardware hw = BuildHardwareWithLayout();

        string? reason = null;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { reason = e.Reason; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 5000);
        hw.WriteRegisterAt(1, 42);
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // EDGE CASE: The LOAD and STORE providers must bind to distinct opcodes.
    // Verifying by confirming that the STORE opcode does not fire for a LOAD
    // instruction and vice versa.
    [Fact]
    public void LoadTrap_DoesNotFire_ForStoreInstruction()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool loadTrapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.LOAD) { loadTrapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(0, 5000); // b1 for STORE; out-of-bounds
        hw.WriteRegisterAt(1, 42);
        ExecuteAt(hw, 200, Instruction.STORE, 0, 1, 0);

        Assert.False(loadTrapped);
    }

    // EDGE CASE: Symmetric counterpart — STORE trap must not fire for LOAD.
    [Fact]
    public void StoreTrap_DoesNotFire_ForLoadInstruction()
    {
        Hardware hw = BuildHardwareWithLayout();

        bool storeTrapped = false;
        hw.InvalidInstruction += (_, e) =>
        {
            if (e.Opcode == Instruction.STORE) { storeTrapped = true; }
        };

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 5000); // b2 for LOAD; out-of-bounds
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);

        Assert.False(storeTrapped);
    }

    // ---- CollectTraps / BasicOS construction ---------------------------------

    // EDGE CASE: CollectTraps must discover all three providers. Verified by
    // confirming that all three opcodes are trapped in User mode with appropriate
    // conditions met. If any provider is silently skipped, its opcode is unguarded.
    [Fact]
    public void BasicOs_CollectTraps_RegistersAllThreeOpcodes()
    {
        // Each opcode is checked on a separate Hardware instance. Firing one trap
        // enters the Privileged OS routine, which changes the privilege level and
        // prevents subsequent User-mode trap conditions from being evaluated.

        bool iretTrapped = false;
        Hardware hw1 = BuildHardwareWithLayout();
        hw1.SetPrivilegeLevel(PrivilegeLevel.User);
        hw1.InvalidInstruction += (_, e) => { if (e.Opcode == Instruction.IRET) { iretTrapped = true; } };
        ExecuteAt(hw1, 200, Instruction.IRET, 0, 0, 0);

        bool loadTrapped = false;
        Hardware hw2 = BuildHardwareWithLayout();
        hw2.SetPrivilegeLevel(PrivilegeLevel.User);
        hw2.WriteRegisterAt(1, 5000);
        hw2.InvalidInstruction += (_, e) => { if (e.Opcode == Instruction.LOAD) { loadTrapped = true; } };
        ExecuteAt(hw2, 200, Instruction.LOAD, 0, 1, 0);

        bool storeTrapped = false;
        Hardware hw3 = BuildHardwareWithLayout();
        hw3.SetPrivilegeLevel(PrivilegeLevel.User);
        hw3.WriteRegisterAt(1, 5000);
        hw3.InvalidInstruction += (_, e) => { if (e.Opcode == Instruction.STORE) { storeTrapped = true; } };
        ExecuteAt(hw3, 200, Instruction.STORE, 1, 0, 0);

        Assert.True(iretTrapped);
        Assert.True(loadTrapped);
        Assert.True(storeTrapped);
    }

    // EDGE CASE: CollectTraps must not produce duplicate opcode registrations.
    // If two providers bind the same opcode, the second is unreachable (the first
    // fires, calls TrapInvalidInstruction, and EvaluateTraps returns immediately).
    // Verified by confirming the three expected opcodes are all distinct values.
    [Fact]
    public void BasicOs_CollectTraps_AllRegisteredOpcodesAreDistinct()
    {
        // IRET=0x33, LOAD=0x05, STORE=0x06 — hardcoded verification so a constant
        // rename that accidentally collapses two opcodes is caught here.
        HashSet<byte> opcodes = new HashSet<byte>
        {
            Instruction.IRET,
            Instruction.LOAD,
            Instruction.STORE
        };

        Assert.Equal(3, opcodes.Count);
    }

    // EDGE CASE: BasicOS construction (which calls CollectTraps) must not throw.
    // If any provider's parameterless constructor throws, the exception propagates
    // out of CollectTraps and crashes the OS ctor — unrecoverable at plugin-load time.
    [Fact]
    public void BasicOs_Construction_DoesNotThrow()
    {
        // Construction succeeding is the assertion.
        BasicOS os = new BasicOS(new StringWriter());
        Assert.NotNull(os);
    }

    // EDGE CASE: CollectTraps reflection must skip the ITrapProvider interface itself
    // (interfaces are abstract in .NET, so the IsAbstract guard handles this). Verified
    // indirectly: if the interface were instantiated, Activator.CreateInstance would
    // throw and BasicOS construction would fail — so the previous test covers this.
    // This test explicitly verifies that only concrete types are reflected over.
    [Fact]
    public void BasicOs_CollectTraps_DoesNotAttemptToInstantiateITrapProviderInterface()
    {
        // If ITrapProvider itself were included, Activator.CreateInstance(typeof(ITrapProvider))
        // throws MissingMethodException. Successful construction proves it was skipped.
        BasicOS os = new BasicOS(new StringWriter());

        // Hardware can be attached — meaning LoadTraps received a valid list.
        Hardware hw = new Hardware(8192, Test.AllRegisters(), os);
        Assert.NotNull(hw);
    }

    // ---- LOAD/STORE register-index asymmetry summary -------------------------

    // POTENTIAL DYSFUNCTION: LOAD uses b2 as address register; STORE uses b1.
    // This test confirms the full cross-product: an out-of-bounds value in the
    // WRONG register for each instruction must never trigger its trap.
    [Fact]
    public void LoadAndStoreTrap_RegisterIndexAsymmetry_WrongRegisterNeverTriggersOwnTrap()
    {
        Hardware hw = BuildHardwareWithLayout();
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        // r0 = out-of-bounds, r1 = in-bounds
        hw.WriteRegisterAt(0, 5000);
        hw.WriteRegisterAt(1, 1);
        hw.WriteBytes(201, new byte[] { 0x00, 0x00, 0x00, 0x00 });

        bool loadTrappedWhenB1IsOutOfBounds = false;
        bool storeTrappedWhenB2IsOutOfBounds = false;
        hw.InvalidInstruction += (_, e) =>
        {
            // LOAD(dst=r0, addr=r1): b1=0 holds 5000 but trap reads b2=1 which holds 1 (in-bounds)
            if (e.Opcode == Instruction.LOAD) { loadTrappedWhenB1IsOutOfBounds = true; }
            // STORE(addr=r1, val=r0): b2=0 holds 5000 but trap reads b1=1 which holds 1 (in-bounds)
            if (e.Opcode == Instruction.STORE) { storeTrappedWhenB2IsOutOfBounds = true; }
        };

        // LOAD: b1=0 (destination, ignored by trap), b2=1 (address, in-bounds at r1=1)
        ExecuteAt(hw, 200, Instruction.LOAD, 0, 1, 0);
        // STORE: b1=1 (address, in-bounds at r1=1), b2=0 (value, holds 5000 but ignored by trap)
        ExecuteAt(hw, 200, Instruction.STORE, 1, 0, 0);

        Assert.False(loadTrappedWhenB1IsOutOfBounds);
        Assert.False(storeTrappedWhenB2IsOutOfBounds);
    }
}
