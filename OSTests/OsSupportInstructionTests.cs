using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the OS-support instructions (MOV_REG_IMM16, SAVEREGS, LOADREGS,
/// SETLAYOUT, OSRET) and the Kernel-mode absolute addressing that underpins OS
/// code running in its own memory region.
/// </summary>
public class OsSupportInstructionTests
{
    private static Hardware NewHw()
    {
        return Test.NewHardware(4096, new FakeOS());
    }

    // Executes a single instruction word placed at a scratch address.
    private static void Exec(Hardware hw, byte opcode, byte b1, byte b2, byte b3)
    {
        int at = 2048;
        hw.WriteBytes(at, Test.Word(opcode, b1, b2, b3));
        Instruction.Execute(at, hw);
    }

    [Fact]
    public void MovImm16_LoadsFullSixteenBitValue()
    {
        Hardware hw = NewHw();
        Exec(hw, Instruction.MOV_REG_IMM16, (byte)RegisterName.EAX, 0x12, 0x34);
        Assert.Equal(0x1234, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void MovImm16_Assembler_RoundTrips()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EBX, 1000);
        byte[] code = asm.Build();
        Hardware hw = NewHw();
        hw.WriteBytes(0, code);
        Instruction.Execute(0, hw);
        Assert.Equal(1000, hw.ReadRegisterAt((byte)RegisterName.EBX));
    }

    [Fact]
    public void GetProgramBase_InKernelMode_IsZero()
    {
        Hardware hw = NewHw();
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Assert.Equal(0, hw.GetProgramBase());
    }

    [Fact]
    public void SaveRegs_WritesCapturedContextIncludingIp()
    {
        Hardware hw = NewHw();
        hw.WriteRegisterAt((byte)RegisterName.EAX, 111);
        hw.WriteRegisterAt((byte)RegisterName.EBX, 222);
        hw.SetInstructionPointer(900);
        hw.CaptureInterruptedContext();

        // Routine clobbers the live registers as scratch before persisting.
        hw.WriteRegisterAt((byte)RegisterName.EAX, -1);
        int target = 1024;
        hw.WriteRegisterAt((byte)RegisterName.ECX, target);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Exec(hw, Instruction.SAVEREGS, (byte)RegisterName.ECX, 0, 0);

        // The persisted frame holds the interrupted values, not the scratch ones.
        Assert.Equal(111, ReadWord(hw, target + hw.GetRegisterOffset(RegisterName.EAX)));
        Assert.Equal(222, ReadWord(hw, target + hw.GetRegisterOffset(RegisterName.EBX)));
        Assert.Equal(900, ReadWord(hw, target + hw.GetRegisterOffset(RegisterName.EIP)));
    }

    [Fact]
    public void LoadRegs_DoesNotDisturbLiveRegisters_UntilOsRet()
    {
        Hardware hw = NewHw();
        int entry = 1024;
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EAX), 777);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP), 1500);

        hw.WriteRegisterAt((byte)RegisterName.EAX, 5);
        hw.WriteRegisterAt((byte)RegisterName.EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);

        Exec(hw, Instruction.LOADREGS, (byte)RegisterName.EBX, 0, 0);
        // Live registers are still the routine's scratch after LOADREGS.
        Assert.Equal(5, hw.ReadRegisterAt((byte)RegisterName.EAX));
    }

    [Fact]
    public void OsRet_CommitsStagedContext_SetsIpAndLevel()
    {
        Hardware hw = NewHw();
        int entry = 1024;
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EAX), 777);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP), 1500);

        hw.WriteRegisterAt((byte)RegisterName.EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Exec(hw, Instruction.LOADREGS, (byte)RegisterName.EBX, 0, 0);

        // Routine computes the return level into a live register, then OSRETs it.
        hw.WriteRegisterAt((byte)RegisterName.ECX, (int)PrivilegeLevel.User);
        Exec(hw, Instruction.OSRET, (byte)RegisterName.ECX, 0, 0);

        Assert.Equal(777, hw.ReadRegisterAt((byte)RegisterName.EAX));            // committed register file
        Assert.Equal(1500, hw.GetInstructionPointer());        // IP from EIP slot
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void DispatchOsRoutine_CapturesContext_JumpsToIvtSlot_InKernelAtomic()
    {
        Hardware hw = NewHw();
        hw.ReserveOsMemory(256);
        // IVT slot 0 points at a routine living at address 200.
        WriteWord(hw, Hardware.IvtContextSwitch * 4, 200);

        hw.SetInstructionPointer(50);
        hw.WriteRegisterAt((byte)RegisterName.EAX, 42);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);

        Assert.Equal(200, hw.GetInstructionPointer());
        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        Assert.False(hw.InterruptsEnabled()); // dispatched OS routine runs atomically

        // The interrupted context (regs + the IP it was about to run) is recoverable
        // via SAVEREGS even though the routine may now clobber live registers.
        int frame = 1024;
        hw.WriteRegisterAt((byte)RegisterName.ECX, frame);
        Exec(hw, Instruction.SAVEREGS, (byte)RegisterName.ECX, 0, 0);
        Assert.Equal(42, ReadWord(hw, frame + hw.GetRegisterOffset(RegisterName.EAX)));
        Assert.Equal(50, ReadWord(hw, frame + hw.GetRegisterOffset(RegisterName.EIP)));
    }

    [Fact]
    public void OsRet_WithNoPendingContext_SetsProcessNotRunning()
    {
        // OSRET without a prior LOADREGS (pendingContext == null) takes the idle path:
        // it drops to the specified level but marks the CPU as not running a process.
        Hardware hw = NewHw();
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);

        hw.WriteRegisterAt((byte)RegisterName.ECX, (int)PrivilegeLevel.User);
        Exec(hw, Instruction.OSRET, (byte)RegisterName.ECX, 0, 0);

        Assert.False(hw.IsProcessRunning());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void SaveRegs_WithoutPriorCapture_PersistsLiveRegisters()
    {
        // When no DispatchOsRoutine has been called (trapFrame is empty), SAVEREGS
        // falls back to persisting the live register file rather than a captured frame.
        Hardware hw = NewHw();
        hw.WriteRegisterAt((byte)RegisterName.EAX, 321);
        int target = 1024;
        hw.WriteRegisterAt((byte)RegisterName.ECX, target);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        // No CaptureInterruptedContext: trapFrame is still Array.Empty<byte>().

        Exec(hw, Instruction.SAVEREGS, (byte)RegisterName.ECX, 0, 0);

        Assert.Equal(321, ReadWord(hw, target + hw.GetRegisterOffset(RegisterName.EAX)));
    }

    [Fact]
    public void SetLayout_RebuildsProcessLayoutFromEntry()
    {
        Hardware hw = NewHw();
        int entry = 1024;
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 300);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramSize, 16);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredMemory, 64);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredStackSize, 32);

        hw.WriteRegisterAt((byte)RegisterName.EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Exec(hw, Instruction.SETLAYOUT, (byte)RegisterName.EBX, 0, 0);

        // In user mode the program base is the program address the entry described.
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        Assert.Equal(300, hw.GetProgramBase());
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
