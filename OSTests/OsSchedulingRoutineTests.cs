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
    private const byte EAX = 0;
    private const byte EIP = 8;

    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(8192, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

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

    private static void SeedEntry(Hardware hw, int index, ProcessState state, WaitReason wait,
        PrivilegeLevel level, int eax, int eip, int programAddress)
    {
        int entry = OsLayout.ProcessEntryAddress(index);
        WriteWord(hw, entry + EAX * 4, eax);
        WriteWord(hw, entry + EIP * 4, eip);
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
        Assert.Equal(2000, hw.ReadRegisterAt(EAX));
        Assert.Equal(0x222, hw.GetInstructionPointer());
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

        hw.WriteRegisterAt(EAX, 1234);
        hw.SetInstructionPointer(0x40);
        hw.DispatchOsRoutine(Hardware.IvtBlockInput, (int)WaitReason.Input);
        RunRoutine(hw);

        // Process 0 is now Blocked on Input, with its frame saved, and 1 runs.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        Assert.Equal((int)WaitReason.Input, ReadWord(hw, entry0 + Hardware.ProcessEntryWaitReason));
        Assert.Equal(1234, ReadWord(hw, entry0 + EAX * 4));
        Assert.Equal(0x40, ReadWord(hw, entry0 + EIP * 4));
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt(EAX));
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
    public void Wake_MakesWaiterReady_AndResumesInterruptedProcess()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0); // process 0 running
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 2000, 0x222, 300);

        hw.WriteRegisterAt(EAX, 555);
        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, (int)WaitReason.Input);
        RunRoutine(hw);

        // The waiter became Ready, but the running process keeps running unchanged.
        int entry1 = OsLayout.ProcessEntryAddress(1);
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
        Assert.Equal((int)WaitReason.None, ReadWord(hw, entry1 + Hardware.ProcessEntryWaitReason));
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));   // still process 0
        Assert.Equal(555, hw.ReadRegisterAt(EAX));                    // its registers restored
        Assert.Equal(0x50, hw.GetInstructionPointer());               // and its IP
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void Wake_OnlyWakesMatchingReason()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 3);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, ProcessState.Ready, WaitReason.None, PrivilegeLevel.User, 1000, 0x111, 100);
        SeedEntry(hw, 1, ProcessState.Blocked, WaitReason.Output, PrivilegeLevel.User, 2000, 0x222, 300);
        SeedEntry(hw, 2, ProcessState.Blocked, WaitReason.Input, PrivilegeLevel.User, 3000, 0x333, 500);

        hw.SetInstructionPointer(0x50);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput, (int)WaitReason.Input);
        RunRoutine(hw);

        // The Output waiter stays blocked; only the Input waiter is woken.
        int entry1 = OsLayout.ProcessEntryAddress(1);
        int entry2 = OsLayout.ProcessEntryAddress(2);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry1 + Hardware.ProcessEntryState));
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry2 + Hardware.ProcessEntryState));
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
        Assert.Equal(2000, hw.ReadRegisterAt(EAX));
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

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    private static void WriteWord(Hardware hw, int address, int value)
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
