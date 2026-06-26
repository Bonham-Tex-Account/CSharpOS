using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers the privileged disk instructions DREAD / DWRITE on a bare Hardware backed
/// by a known Bin: slot→RAM and RAM→slot transfers, the byte-count result register,
/// a DWRITE/DREAD round trip, the free-slot error, assembling/disassembling, and the
/// user-mode privilege guard.
/// </summary>
public class DiskInstructionTests
{
    private const byte EAX = (byte)RegisterName.EAX;
    private const byte EBX = (byte)RegisterName.EBX;
    private const byte ECX = (byte)RegisterName.ECX;
    private const byte EDX = (byte)RegisterName.EDX;

    // Bare Hardware whose disk is the supplied Bin, raised to Privileged so the disk
    // instructions are allowed (they trap in user mode).
    private static Hardware PrivilegedHardwareWithDisk(Bin disk)
    {
        Hardware hw = new Hardware(512, Test.AllRegisters(), new FakeOS(), disk);
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        return hw;
    }

    private static void Exec(Hardware hw, byte opcode, byte b1, byte b2, byte b3)
    {
        int at = 256;
        hw.WriteBytes(at, Test.Word(opcode, b1, b2, b3));
        Instruction.Execute(at, hw);
    }

    [Fact]
    public void DRead_CopiesSlotIntoRamAndReportsLength()
    {
        Bin disk = new Bin(4, 32);
        byte[] image = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x10 };
        int slot = disk.Store(image);

        Hardware hw = PrivilegedHardwareWithDisk(disk);
        int destAddress = 100;
        hw.WriteRegisterAt(EAX, destAddress);
        hw.WriteRegisterAt(EBX, slot);

        Exec(hw, Instruction.DREAD, EAX, EBX, ECX);

        Assert.Equal(image, ReadRam(hw, destAddress, image.Length));
        Assert.Equal(image.Length, hw.ReadRegisterAt(ECX));
    }

    [Fact]
    public void DWrite_CopiesRamRangeIntoSlot()
    {
        Bin disk = new Bin(4, 32);
        Hardware hw = PrivilegedHardwareWithDisk(disk);
        int srcAddress = 120;
        byte[] payload = new byte[] { 1, 2, 3, 4 };
        hw.WriteBytes(srcAddress, payload);

        int slot = 2;
        hw.WriteRegisterAt(EAX, slot);
        hw.WriteRegisterAt(EBX, srcAddress);
        hw.WriteRegisterAt(ECX, payload.Length);

        Exec(hw, Instruction.DWRITE, EAX, EBX, ECX);

        Assert.True(disk.IsOccupied(slot));
        Assert.Equal(payload.Length, disk.GetLength(slot));
        Assert.Equal(payload, disk.Load(slot));
    }

    [Fact]
    public void DWriteThenDRead_RoundTripsThroughTheDisk()
    {
        Bin disk = new Bin(4, 32);
        Hardware hw = PrivilegedHardwareWithDisk(disk);

        int srcAddress = 80;
        byte[] payload = new byte[] { 7, 6, 5, 4, 3 };
        hw.WriteBytes(srcAddress, payload);
        hw.WriteRegisterAt(EAX, 1);             // slot
        hw.WriteRegisterAt(EBX, srcAddress);    // src
        hw.WriteRegisterAt(ECX, payload.Length);
        Exec(hw, Instruction.DWRITE, EAX, EBX, ECX);

        int destAddress = 200;
        hw.WriteRegisterAt(EAX, destAddress);   // dest
        hw.WriteRegisterAt(EBX, 1);             // slot
        Exec(hw, Instruction.DREAD, EAX, EBX, EDX);

        Assert.Equal(payload, ReadRam(hw, destAddress, payload.Length));
        Assert.Equal(payload.Length, hw.ReadRegisterAt(EDX));
    }

    [Fact]
    public void DRead_OfAFreeSlot_SurfacesTheEmptyError()
    {
        Bin disk = new Bin(4, 32);
        Hardware hw = PrivilegedHardwareWithDisk(disk);
        hw.WriteRegisterAt(EAX, 100);
        hw.WriteRegisterAt(EBX, 0); // never stored

        Assert.Throws<InvalidOperationException>(() => Exec(hw, Instruction.DREAD, EAX, EBX, ECX));
    }

    [Fact]
    public void DRead_InUserMode_TrapsAsInvalidAndDoesNotCopy()
    {
        Bin disk = new Bin(4, 32);
        byte[] image = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        int slot = disk.Store(image);

        Hardware hw = new Hardware(512, Test.AllRegisters(), new FakeOS(), disk);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        byte trappedOpcode = 0;
        bool trapped = false;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) =>
        {
            trapped = true;
            trappedOpcode = e.Opcode;
        };

        int destAddress = 100;
        hw.WriteRegisterAt(EAX, destAddress);
        hw.WriteRegisterAt(EBX, slot);
        Exec(hw, Instruction.DREAD, EAX, EBX, ECX);

        Assert.True(trapped);
        Assert.Equal(Instruction.DREAD, trappedOpcode);
        // The disk was never read: RAM at the destination is still zero.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, ReadRam(hw, destAddress, 4));
    }

    [Fact]
    public void DWrite_InUserMode_TrapsAsInvalidAndDoesNotWriteTheDisk()
    {
        Bin disk = new Bin(4, 32);
        Hardware hw = new Hardware(512, Test.AllRegisters(), new FakeOS(), disk);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        byte trappedOpcode = 0;
        bool trapped = false;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) =>
        {
            trapped = true;
            trappedOpcode = e.Opcode;
        };

        int srcAddress = 64;
        hw.WriteBytes(srcAddress, new byte[] { 1, 2, 3, 4 });
        hw.WriteRegisterAt(EAX, 0);
        hw.WriteRegisterAt(EBX, srcAddress);
        hw.WriteRegisterAt(ECX, 4);
        Exec(hw, Instruction.DWRITE, EAX, EBX, ECX);

        Assert.True(trapped);
        Assert.Equal(Instruction.DWRITE, trappedOpcode);
        Assert.False(disk.IsOccupied(0));
    }

    [Fact]
    public void DRead_AssemblesAndDisassembles()
    {
        Assembler asm = new Assembler();
        asm.DRead(RegisterName.EBX, RegisterName.ECX, RegisterName.EDX);
        byte[] code = asm.Build();

        Assert.Equal(Instruction.DREAD, code[0]);
        Assert.Equal("DREAD [EBX], ECX, EDX", Disassembler.Decode(code[0], code[1], code[2], code[3]));
    }

    [Fact]
    public void DWrite_AssemblesAndDisassembles()
    {
        Assembler asm = new Assembler();
        asm.DWrite(RegisterName.ECX, RegisterName.EBX, RegisterName.EDX);
        byte[] code = asm.Build();

        Assert.Equal(Instruction.DWRITE, code[0]);
        Assert.Equal("DWRITE ECX, [EBX], EDX", Disassembler.Decode(code[0], code[1], code[2], code[3]));
    }

    private static byte[] ReadRam(Hardware hw, int address, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = hw.ReadBytes(address + i)[0];
        }
        return result;
    }
}
