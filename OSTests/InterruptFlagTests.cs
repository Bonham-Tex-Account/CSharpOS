using CSharpOS;

namespace OSTests;

/// <summary>
/// The hardware interrupt-enable flag (the CPU's IF) carries the atomicity that the
/// removed Privileged level used to encode. After the privilege model collapsed to two
/// levels (User/Kernel), OS-routine dispatch masks interrupts (atomic); OSRET/IRET
/// restore them; and a syscall trap leaves them enabled, so the shared syscall handler
/// stays preemptible. These tests pin that behavior down directly.
/// </summary>
public class InterruptFlagTests
{
    [Fact]
    public void InterruptsEnabled_DefaultsTrue()
    {
        Hardware hw = Test.NewHardware(256, new FakeOS());
        Assert.True(hw.InterruptsEnabled());
    }

    [Fact]
    public void DispatchOsRoutine_MasksInterrupts()
    {
        Hardware hw = Test.NewHardware(512, new FakeOS());
        hw.ReserveOsMemory(256);
        Test.WriteWord(hw, Hardware.IvtContextSwitch * 4, 200); // routine lives at 200
        hw.SetInstructionPointer(50);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);

        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        Assert.False(hw.InterruptsEnabled()); // atomic OS routine in flight
    }

    [Fact]
    public void OsRet_RestoresInterrupts()
    {
        Hardware hw = Test.NewHardware(2048, new FakeOS());
        // Simulate being inside an atomic OS routine, then return to a process.
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.SetInterruptsEnabled(false);
        hw.WriteRegisterAt((byte)RegisterName.ECX, (int)PrivilegeLevel.User);
        int ip = 1000;
        hw.WriteBytes(ip, Test.Word(Instruction.OSRET, (byte)RegisterName.ECX, 0, 0));

        Instruction.Execute(ip, hw);

        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
        Assert.True(hw.InterruptsEnabled()); // returning to a preemptible process
    }

    [Fact]
    public void EnterKernel_LeavesInterruptsEnabled_SoTheHandlerIsPreemptible()
    {
        Hardware hw = Test.NewHardware(1024, new FakeOS());
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 0;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);
        Test.WriteWord(hw, Hardware.IvtSyscall * 4, 500); // shared handler address
        hw.SetInstructionPointer(0);

        hw.EnterKernel(Instruction.OUT, 0);

        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        Assert.True(hw.InterruptsEnabled()); // a syscall trap does NOT mask interrupts
        Assert.Equal(500, hw.GetInstructionPointer());
    }

    [Fact]
    public void Iret_LeavesInterruptsEnabled()
    {
        Hardware hw = Test.NewHardware(1024, new FakeOS());
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 0;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);
        Test.WriteWord(hw, Hardware.IvtSyscall * 4, 500);
        hw.SetInstructionPointer(40);
        hw.EnterKernel(Instruction.IN, 0);

        hw.Iret();

        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
        Assert.True(hw.InterruptsEnabled());
    }

    [Fact]
    public void RunLoop_WhileInterruptsMasked_DoesNotDispatchPendingInterrupt()
    {
        // With interrupts masked (an atomic OS routine in flight), a queued device
        // interrupt must NOT be dispatched: Run steps the routine instead.
        Hardware hw = Test.NewHardware(2048, new FakeOS());
        hw.ReserveOsMemory(256);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.SetInterruptsEnabled(false);
        int ip = 1000;
        hw.SetInstructionPointer(ip);
        hw.WriteBytes(ip, Test.Word(Instruction.MOV_REG_IMM, 0, 9, 0)); // a routine instruction
        hw.GetDevice(0).Waiters.Add(0);
        hw.RaiseInputInterrupt(7, 0); // queued, but interrupts are masked

        hw.Run();

        // The routine instruction ran; the interrupt stayed queued (still buffered, not
        // delivered): the device's input buffer is untouched by a wake this tick.
        Assert.Equal(9, hw.ReadRegisterAt(0));
        Assert.Equal(ip + 4, hw.GetInstructionPointer());
        Assert.False(hw.InterruptsEnabled());
    }
}
