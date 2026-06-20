using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Covers the privileged OS-support instructions (MOV_REG_IMM16, SAVEREGS,
/// LOADREGS, SETLAYOUT, OSRET) and the Privileged-mode absolute addressing that
/// underpins OS code running in its own memory region.
/// </summary>
public class OsSupportInstructionTests
{
    private const byte EAX = 0;
    private const byte EBX = 1;
    private const byte ECX = 2;
    private const byte EIP = 8;

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
        Exec(hw, Instruction.MOV_REG_IMM16, EAX, 0x12, 0x34);
        Assert.Equal(0x1234, hw.ReadRegisterAt(EAX));
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
        Assert.Equal(1000, hw.ReadRegisterAt(EBX));
    }

    [Fact]
    public void GetProgramBase_InPrivilegedMode_IsZero()
    {
        Hardware hw = NewHw();
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Assert.Equal(0, hw.GetProgramBase());
    }

    [Fact]
    public void SaveRegs_WritesCapturedContextIncludingIp()
    {
        Hardware hw = NewHw();
        hw.WriteRegisterAt(EAX, 111);
        hw.WriteRegisterAt(EBX, 222);
        hw.SetInstructionPointer(900);
        hw.CaptureInterruptedContext();

        // Routine clobbers the live registers as scratch before persisting.
        hw.WriteRegisterAt(EAX, -1);
        int target = 1024;
        hw.WriteRegisterAt(ECX, target);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Exec(hw, Instruction.SAVEREGS, ECX, 0, 0);

        // The persisted frame holds the interrupted values, not the scratch ones.
        Assert.Equal(111, ReadWord(hw, target + EAX * 4));
        Assert.Equal(222, ReadWord(hw, target + EBX * 4));
        Assert.Equal(900, ReadWord(hw, target + EIP * 4));
    }

    [Fact]
    public void LoadRegs_DoesNotDisturbLiveRegisters_UntilOsRet()
    {
        Hardware hw = NewHw();
        int entry = 1024;
        WriteWord(hw, entry + EAX * 4, 777);
        WriteWord(hw, entry + EIP * 4, 1500);

        hw.WriteRegisterAt(EAX, 5);
        hw.WriteRegisterAt(EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);

        Exec(hw, Instruction.LOADREGS, EBX, 0, 0);
        // Live registers are still the routine's scratch after LOADREGS.
        Assert.Equal(5, hw.ReadRegisterAt(EAX));
    }

    [Fact]
    public void OsRet_CommitsStagedContext_SetsIpAndLevel()
    {
        Hardware hw = NewHw();
        int entry = 1024;
        WriteWord(hw, entry + EAX * 4, 777);
        WriteWord(hw, entry + EIP * 4, 1500);

        hw.WriteRegisterAt(EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Exec(hw, Instruction.LOADREGS, EBX, 0, 0);

        // Routine computes the return level into a live register, then OSRETs it.
        hw.WriteRegisterAt(ECX, (int)PrivilegeLevel.User);
        Exec(hw, Instruction.OSRET, ECX, 0, 0);

        Assert.Equal(777, hw.ReadRegisterAt(EAX));            // committed register file
        Assert.Equal(1500, hw.GetInstructionPointer());        // IP from EIP slot
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void DispatchOsRoutine_CapturesContext_JumpsToIvtSlot_InPrivilegedMode()
    {
        Hardware hw = NewHw();
        hw.ReserveOsMemory(256);
        // IVT slot 0 points at a routine living at address 200.
        WriteWord(hw, Hardware.IvtContextSwitch * 4, 200);

        hw.SetInstructionPointer(50);
        hw.WriteRegisterAt(EAX, 42);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);

        Assert.Equal(200, hw.GetInstructionPointer());
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());

        // The interrupted context (regs + the IP it was about to run) is recoverable
        // via SAVEREGS even though the routine may now clobber live registers.
        int frame = 1024;
        hw.WriteRegisterAt(ECX, frame);
        Exec(hw, Instruction.SAVEREGS, ECX, 0, 0);
        Assert.Equal(42, ReadWord(hw, frame + EAX * 4));
        Assert.Equal(50, ReadWord(hw, frame + EIP * 4));
    }

    [Fact]
    public void OsRet_WithNoPendingContext_SetsProcessNotRunning()
    {
        // OSRET without a prior LOADREGS (pendingContext == null) takes the idle path:
        // it drops to the specified level but marks the CPU as not running a process.
        Hardware hw = NewHw();
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);

        hw.WriteRegisterAt(ECX, (int)PrivilegeLevel.User);
        Exec(hw, Instruction.OSRET, ECX, 0, 0);

        Assert.False(hw.IsProcessRunning());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void SaveRegs_WithoutPriorCapture_PersistsLiveRegisters()
    {
        // When no DispatchOsRoutine has been called (trapFrame is empty), SAVEREGS
        // falls back to persisting the live register file rather than a captured frame.
        Hardware hw = NewHw();
        hw.WriteRegisterAt(EAX, 321);
        int target = 1024;
        hw.WriteRegisterAt(ECX, target);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        // No CaptureInterruptedContext: trapFrame is still Array.Empty<byte>().

        Exec(hw, Instruction.SAVEREGS, ECX, 0, 0);

        Assert.Equal(321, ReadWord(hw, target + EAX * 4));
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

        hw.WriteRegisterAt(EBX, entry);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Exec(hw, Instruction.SETLAYOUT, EBX, 0, 0);

        // In user mode the program base is the program address the entry described.
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        Assert.Equal(300, hw.GetProgramBase());
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
