using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Drives the assembled context-switch routine in isolation: a hand-seeded process
/// table in OS memory, an interrupted process, then DispatchOsRoutine(0) and a
/// privileged single-step loop until the routine returns into the next process.
/// </summary>
public class OsContextSwitchRoutineTests
{
    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

    // Runs privileged instructions one at a time (mirroring Hardware.Run's advance)
    // until the routine drops out of Privileged mode via OSRET, or a step cap trips.
    private static void RunRoutine(Hardware hw)
    {
        for (int step = 0; step < 2000; step++)
        {
            if (hw.GetPrivilegeLevel() != PrivilegeLevel.Privileged)
            {
                return;
            }
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }
        Assert.Fail("Routine did not return within the step cap");
    }

    private static void SeedEntry(Hardware hw, int index, int state, int level, int eax, int eip,
        int programAddress, int programSize, int requiredMemory, int requiredStackSize)
    {
        int entry = OsLayout.ProcessEntryAddress(index);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EAX), eax);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP), eip);
        WriteWord(hw, entry + Hardware.ProcessEntryLevel, level);
        WriteWord(hw, entry + Hardware.ProcessEntryState, state);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, programAddress);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramSize, programSize);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredMemory, requiredMemory);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredStackSize, requiredStackSize);
    }

    [Fact]
    public void ContextSwitch_SavesCurrent_ResumesNextReady()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 2000, 0x222, 300, 8, 64, 32);

        // Process 0 is running: its live registers and the IP it was about to run.
        hw.WriteRegisterAt((byte)RegisterName.EAX,1234);
        hw.SetInstructionPointer(0x999);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);

        RunRoutine(hw);

        // Switched to process 1.
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));        // process 1's registers live
        Assert.Equal(0x222, hw.GetInstructionPointer());    // resumed at process 1's IP
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());

        // Process 0's interrupted context was persisted to its entry.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1234, ReadWord(hw, entry0 + hw.GetRegisterOffset(RegisterName.EAX)));
        Assert.Equal(0x999, ReadWord(hw, entry0 + hw.GetRegisterOffset(RegisterName.EIP)));

        // Layout now targets process 1.
        Assert.Equal(300, hw.GetProgramBase());
    }

    [Fact]
    public void ContextSwitch_SkipsBlocked_ToFindReady()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 3);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 2000, 0x222, 300, 4, 64, 32);
        SeedEntry(hw, 2, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 3000, 0x333, 500, 4, 64, 32);

        hw.WriteRegisterAt((byte)RegisterName.EAX,1);
        hw.SetInstructionPointer(0x10);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Index 1 is Blocked, so the scan skips it and lands on index 2.
        Assert.Equal(2, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(3000, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(0x333, hw.GetInstructionPointer());
    }

    [Fact]
    public void ContextSwitch_WrapsAround_BackToEarlierReady()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 3);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 2); // running the last one
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 2000, 0x222, 300, 4, 64, 32);
        SeedEntry(hw, 2, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 3000, 0x333, 500, 4, 64, 32);

        hw.WriteRegisterAt((byte)RegisterName.EAX,9);
        hw.SetInstructionPointer(0x20);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // From index 2, scan wraps past blocked 1 to index 0.
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(1000, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void ContextSwitch_AllBlocked_RecordsIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 2000, 0x222, 300, 4, 64, 32);

        hw.WriteRegisterAt((byte)RegisterName.EAX,7);
        hw.SetInstructionPointer(0x30);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // No Ready process: idle sentinel, and the running process was still saved.
        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(7, ReadWord(hw, entry0 + hw.GetRegisterOffset(RegisterName.EAX)));
    }

    [Fact]
    public void ContextSwitch_SkipsTerminated_ToFindReady()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 3);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready,      (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Terminated, (int)PrivilegeLevel.User,    0,     0, 200, 4, 64, 32);
        SeedEntry(hw, 2, (int)ProcessState.Ready,      (int)PrivilegeLevel.User, 3000, 0x333, 500, 4, 64, 32);

        hw.WriteRegisterAt((byte)RegisterName.EAX,7);
        hw.SetInstructionPointer(0x10);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Index 1 is Terminated — the scan skips it and lands on index 2.
        Assert.Equal(2, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(3000, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(0x333, hw.GetInstructionPointer());
    }

    [Fact]
    public void ContextSwitched_NotFiredWhenSameProcessResumes()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);

        // First: Schedule commits process 0 at base 100, establishing lastContextBase.
        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);
        Assert.Equal(100, hw.GetProgramBase()); // process 0 running

        // Subscribe only after the first resume so we don't count that fire.
        int eventCount = 0;
        hw.ContextSwitched += (object? sender, ContextSwitchArgs e) => { eventCount++; };

        // Wake with no blocked processes: saves and immediately restores the same
        // entry, so the base stays at 100 and ContextSwitched must not fire.
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, (int)WaitReason.Input);
        RunRoutine(hw);

        Assert.Equal(0, eventCount);
        Assert.Equal(1000, hw.ReadRegisterAt((byte)RegisterName.EAX)); // still process 0's register value
    }

    private static int ReadWord(Hardware hw, int address)
    {
        return Test.ReadWord(hw, address);
    }

    private static void WriteWord(Hardware hw, int address, int value)
    {
        Test.WriteWord(hw, address, value);
    }
}
