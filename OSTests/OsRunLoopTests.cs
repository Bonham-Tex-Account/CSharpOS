using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the three active dispatch branches inside Hardware.Run when an OS image
/// is loaded: stepping an in-progress Privileged routine, dispatching the Schedule
/// routine when idle, and dispatching a Wake routine when an interrupt is pending.
/// </summary>
public class OsRunLoopTests
{
    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
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
    public void Run_WhenPrivileged_StepsOneOsRoutineInstruction()
    {
        // When level == Privileged, Run steps the current OS routine instruction
        // rather than dispatching a new one or stepping a user process.
        Hardware hw = NewSeededHardware();
        int addr = 3000; // past the OS data section
        hw.WriteBytes(addr, Test.Word(Instruction.MOV_REG_IMM, 0, 77, 0));
        hw.SetInstructionPointer(addr);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);

        hw.Run();

        Assert.Equal(77, hw.ReadRegisterAt(0));
        Assert.Equal(addr + 4, hw.GetInstructionPointer());
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void Run_WhenIdleAndOsManaged_DispatchesScheduleRoutine()
    {
        // When no process is running (processRunning=false) and an OS image is
        // present, the first Run tick must dispatch the Schedule routine.
        Hardware hw = NewSeededHardware();
        // processRunning starts false; level starts User.
        Assert.False(hw.IsProcessRunning());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());

        hw.Run();

        // The Schedule routine was entered: hardware is now executing it (Privileged).
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void Run_WhenInterruptPending_DispatchesWakeRoutineBeforeSteppingProcess()
    {
        // When a device interrupt is queued and a process is running, Run must
        // drain the interrupt (dispatching the Wake routine) rather than step the
        // process instruction.
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, 0, 4096, 4096, 4, 64, 32);

        // Run until Schedule commits process 0 and processRunning becomes true.
        for (int i = 0; i < 500 && !hw.IsProcessRunning(); i++)
        {
            hw.Run();
        }
        Assert.True(hw.IsProcessRunning());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());

        // Queue an input interrupt.
        hw.RaiseInputInterrupt(42);

        // The next Run tick must dispatch WakeInput before executing any process instruction.
        hw.Run();

        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel()); // routine entered
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
