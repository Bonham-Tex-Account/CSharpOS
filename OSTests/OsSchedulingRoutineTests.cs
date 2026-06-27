using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Isolation tests for the Schedule, Block, Wake, and Halt routines, driven the
/// same way as the context-switch routine: a hand-seeded process table in OS
/// memory, a dispatch, then a privileged single-step loop until the routine returns.
/// </summary>
public class OsSchedulingRoutineTests
{
    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

    private static void RunRoutine(Hardware hw)
    {
        for (int step = 0; step < 2000; step++)
        {
            if (hw.InterruptsEnabled())
            {
                return;
            }
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }
        Assert.Fail("Routine did not return within the step cap");
    }

    private static void SeedEntry(Hardware hw, int index, ProcessState state, WaitReason wait,
        PrivilegeLevel level, int eax, int eip, int programAddress)
    {
        int entry = OsLayout.ProcessEntryAddress(index);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EAX), eax);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP), eip);
        WriteWord(hw, entry + Hardware.ProcessEntryLevel, (int)level);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)state);
        WriteWord(hw, entry + Hardware.ProcessEntryWaitReason, (int)wait);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, programAddress);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramSize, 4);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredMemory, 64);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredStackSize, 32);
    }

    [Fact]
    public void Schedule_FromIdle_PicksAReadyProcess()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1); // idle
        SeedEntry(hw, 0, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(300 + 0x222, hw.GetInstructionPointer()); // base + saved EIP offset
    }

    [Fact]
    public void Schedule_FromIdle_AllBlocked_StaysIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 1000, 0x111, 100);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void Block_MarksCurrentBlocked_AndSwitchesAway()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.WriteRegisterAt((byte)RegisterName.EAX,1234);
        hw.SetInstructionPointer(0x40);
        hw.DispatchOsRoutine(Hardware.IvtBlockInput, (int)WaitReason.Input);
        RunRoutine(hw);

        // Process 0 is now Blocked on Input, with its frame saved, and 1 runs.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal((int)WaitReason.Input, ReadWord(hw, entry0 + Hardware.ProcessEntryWaitReason));
        Assert.Equal(1234, ReadWord(hw, entry0 + hw.GetRegisterOffset(RegisterName.EAX)));
        Assert.Equal(0x40, ReadWord(hw, entry0 + hw.GetRegisterOffset(RegisterName.EIP)));
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void Block_LastReadyProcess_GoesIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);

        hw.SetInstructionPointer(0x40);
        hw.DispatchOsRoutine(Hardware.IvtBlockOutput, (int)WaitReason.Output);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)WaitReason.Output, ReadWord(hw, entry0 + Hardware.ProcessEntryWaitReason));
        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void Wake_MakesTargetedWaiterReady_AndResumesInterruptedProcess()
    {
        // The wake routine takes the target device (== process index) in EAX, set
        // by DispatchOsRoutine's argument.
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0); // process 0 running
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.WriteRegisterAt((byte)RegisterName.EAX,555);
        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, 1); // device 1 (process index 1)
        RunRoutine(hw);

        // The waiter became Ready, but the running process keeps running unchanged.
        int entry1 = OsLayout.ProcessEntryAddress(1);
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
        Assert.Equal((int)WaitReason.None, ReadWord(hw, entry1 + Hardware.ProcessEntryWaitReason));
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));   // still process 0
        Assert.Equal(555, hw.ReadRegisterAt((byte)RegisterName.EAX));                    // its registers restored
        Assert.Equal(100 + 0x50, hw.GetInstructionPointer()); // base + saved EIP offset               // and its IP
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void Wake_DoesNotWakeTarget_WhenItIsBlockedOnADifferentReason()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Blocked, WaitReason.Output, PrivilegeLevel.User, 2000, 0x222, 300);

        // An INPUT interrupt for device 1, but that process is blocked on OUTPUT:
        // the reason does not match, so it stays blocked.
        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, 1);
        RunRoutine(hw);

        int entry1 = OsLayout.ProcessEntryAddress(1);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
    }

    [Fact]
    public void Halt_TerminatesCurrent_AndResumesNext()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.SetInstructionPointer(0x60);
        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void Halt_LastProcess_GoesIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);

        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void InvalidInstruction_TerminatesCurrent_AndResumesNext()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.SetInstructionPointer(0x60);
        hw.DispatchOsRoutine(Hardware.IvtInvalidInstruction);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void InvalidInstruction_LastProcess_GoesIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);

        hw.DispatchOsRoutine(Hardware.IvtInvalidInstruction);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void Schedule_SkipsTerminated_ToFindReady()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, ProcessState.Terminated, WaitReason.None, PrivilegeLevel.User,    0,     0, 100);
        SeedEntry(hw, 1, ProcessState.Ready,      WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        // Index 0 is Terminated — Schedule skips it and picks index 1.
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(300 + 0x222, hw.GetInstructionPointer()); // base + saved EIP offset
    }

    [Fact]
    public void Wake_WhenTargetNotBlocked_ResumesCurrentUnchanged()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 2000, 0x222, 300);

        // A (spurious) input interrupt for device 1, which is not blocked: nothing
        // changes and the running process keeps going.
        hw.WriteRegisterAt((byte)RegisterName.EAX,999);
        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, 1);
        RunRoutine(hw);

        int entry1 = OsLayout.ProcessEntryAddress(1);
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(999, hw.ReadRegisterAt((byte)RegisterName.EAX));
        Assert.Equal(100 + 0x50, hw.GetInstructionPointer()); // base + saved EIP offset
    }

    [Fact]
    public void Wake_TargetsOnlyTheSignaledDevice_NotOtherWaiters()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 3);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready,   WaitReason.None,  PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 2000, 0x222, 300);
        SeedEntry(hw, 2, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 3000, 0x333, 500);

        // Input arrives for device 2 only: device 1 must stay blocked.
        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, 2);
        RunRoutine(hw);

        int entry1 = OsLayout.ProcessEntryAddress(1);
        int entry2 = OsLayout.ProcessEntryAddress(2);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
        Assert.Equal((int)ProcessState.Ready,   ReadWord(hw, entry2 + Hardware.ProcessEntryState));
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
