using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Verifies the position-independent (relative) addressing model from Phase 0 of the
/// spawning work: ESP, the saved EIP, and CALL/RET return addresses are all offsets
/// from the program base, so a process's saved/copied state is base-independent. This
/// is the foundation that lets fork be a pure memory copy.
/// </summary>
public class PositionIndependenceTests
{
    private const byte EAX = (byte)RegisterName.EAX;
    private const byte EBX = (byte)RegisterName.EBX;

    // A process whose program image sits at a non-zero base, so relative != absolute.
    private const int Base = 100;

    private static Hardware HardwareAtBase()
    {
        Hardware hw = Test.NewHardware(1024, new FakeOS());
        Process process = new Process("x", 64, 64);
        process.ProgramAddress = Base;
        process.ProgramSize = 32;
        hw.LoadProcessLayout(process);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        return hw;
    }

    // CALL sub / MOV EAX,7 / HLT / sub: MOV EBX,9 / RET  (offsets 0,4,8,12,16)
    private static byte[] CallRetProgram()
    {
        Assembler asm = new Assembler();
        asm.Call("sub");
        asm.MovImm(RegisterName.EAX, 7);
        asm.Hlt();
        asm.Label("sub");
        asm.MovImm(RegisterName.EBX, 9);
        asm.Ret();
        return asm.Build();
    }

    [Fact]
    public void Call_PushesBaseRelativeReturnOffset()
    {
        Hardware hw = HardwareAtBase();
        hw.WriteRegister(RegisterName.ESP, 200); // ESP is an offset from the base
        hw.WriteBytes(Base, CallRetProgram());
        hw.SetInstructionPointer(Base);

        hw.Run(); // executes the CALL at offset 0

        // Jumped to the subroutine at base + 12, and ESP decremented by one word.
        Assert.Equal(Base + 12, hw.GetInstructionPointer());
        Assert.Equal(196, hw.ReadRegister(RegisterName.ESP));
        // The pushed return address is the base-relative offset (4), not the absolute
        // address (104): proof of position independence.
        Assert.Equal(4, Test.ReadWord(hw, Base + 196));
    }

    [Fact]
    public void CallRet_RoundTripsAtNonZeroBase()
    {
        Hardware hw = HardwareAtBase();
        hw.WriteRegister(RegisterName.ESP, 200);
        hw.WriteBytes(Base, CallRetProgram());
        hw.SetInstructionPointer(Base);

        for (int i = 0; i < 20 && hw.GetPrivilegeLevel() != PrivilegeLevel.Privileged; i++)
        {
            hw.Run();
        }

        // The subroutine ran (EBX) and control returned to run MOV EAX,7 (EAX), so
        // CALL/RET round-tripped correctly even though the program is not at base 0.
        Assert.Equal(9, hw.ReadRegister(RegisterName.EBX));
        Assert.Equal(7, hw.ReadRegister(RegisterName.EAX));
        Assert.Equal(200, hw.ReadRegister(RegisterName.ESP)); // stack balanced after RET
    }

    [Fact]
    public void CaptureContext_FoldsBaseRelativeEipIntoTheSavedFrame()
    {
        Hardware hw = HardwareAtBase();
        hw.SetInstructionPointer(Base + 40); // live IP 40 bytes into the program

        hw.CaptureInterruptedContext();
        int scratch = 600;
        hw.SaveRegistersTo(scratch);

        // The saved EIP slot holds the offset from the base (40), not the absolute IP.
        Assert.Equal(40, Test.ReadWord(hw, scratch + hw.GetRegisterOffset(RegisterName.EIP)));
    }
}
