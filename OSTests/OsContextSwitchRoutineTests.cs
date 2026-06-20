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
    private const byte EAX = 0;
    private const byte EIP = 8;

    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(8192, new FakeOS());
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
        WriteWord(hw, entry + EAX * 4, eax);
        WriteWord(hw, entry + EIP * 4, eip);
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
        hw.WriteRegisterAt(EAX, 1234);
        hw.SetInstructionPointer(0x999);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);

        RunRoutine(hw);

        // Switched to process 1.
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(2000, hw.ReadRegisterAt(EAX));        // process 1's registers live
        Assert.Equal(0x222, hw.GetInstructionPointer());    // resumed at process 1's IP
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());

        // Process 0's interrupted context was persisted to its entry.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1234, ReadWord(hw, entry0 + EAX * 4));
        Assert.Equal(0x999, ReadWord(hw, entry0 + EIP * 4));

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

        hw.WriteRegisterAt(EAX, 1);
        hw.SetInstructionPointer(0x10);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Index 1 is Blocked, so the scan skips it and lands on index 2.
        Assert.Equal(2, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(3000, hw.ReadRegisterAt(EAX));
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

        hw.WriteRegisterAt(EAX, 9);
        hw.SetInstructionPointer(0x20);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // From index 2, scan wraps past blocked 1 to index 0.
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(1000, hw.ReadRegisterAt(EAX));
    }

    [Fact]
    public void ContextSwitch_AllBlocked_RecordsIdle()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 1000, 0x111, 100, 4, 64, 32);
        SeedEntry(hw, 1, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, 2000, 0x222, 300, 4, 64, 32);

        hw.WriteRegisterAt(EAX, 7);
        hw.SetInstructionPointer(0x30);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // No Ready process: idle sentinel, and the running process was still saved.
        Assert.Equal(-1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(7, ReadWord(hw, entry0 + EAX * 4));
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
