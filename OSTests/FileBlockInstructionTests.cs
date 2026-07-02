using CSharpOS;

namespace OSTests;

/// <summary>
/// Covers the privileged file-block instructions FBREAD / FBWRITE on a bare Hardware
/// backed by a Bin with a file-block region: whole-block RAM↔block transfers of the
/// fixed FileBlockSize, a round trip, the read-anywhere (zeros) semantics of an
/// unwritten block, assembling/disassembling, and the user-mode privilege guard.
/// </summary>
public class FileBlockInstructionTests
{
    private const byte EAX = (byte)RegisterName.EAX;
    private const byte EBX = (byte)RegisterName.EBX;
    private const int BlockSize = 16;

    // Bare Hardware whose disk is the supplied Bin, raised to Kernel so the file-block
    // instructions are allowed (they trap in user mode, exactly like DREAD/DWRITE).
    private static Hardware KernelHardwareWithDisk(Bin disk)
    {
        Hardware hw = new Hardware(512, Test.AllRegisters(), new FakeOS(), disk);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        return hw;
    }

    private static void Exec(Hardware hw, byte opcode, byte b1, byte b2, byte b3)
    {
        int at = 256;
        hw.WriteBytes(at, Test.Word(opcode, b1, b2, b3));
        Instruction.Execute(at, hw);
    }

    private static byte[] Pattern(int size, byte start)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)(start + i);
        }
        return data;
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

    [Fact]
    public void FbRead_CopiesWholeBlockIntoRam()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
        byte[] block = Pattern(BlockSize, 100);
        disk.WriteFileBlock(2, block);

        Hardware hw = KernelHardwareWithDisk(disk);
        int destAddress = 100;
        hw.WriteRegisterAt(EAX, destAddress);
        hw.WriteRegisterAt(EBX, 2);

        Exec(hw, Instruction.FBREAD, EAX, EBX, 0);

        Assert.Equal(block, ReadRam(hw, destAddress, BlockSize));
    }

    [Fact]
    public void FbWrite_CopiesWholeRamBlockIntoTheFileBlock()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
        Hardware hw = KernelHardwareWithDisk(disk);
        int srcAddress = 64;
        byte[] payload = Pattern(BlockSize, 1);
        hw.WriteBytes(srcAddress, payload);

        hw.WriteRegisterAt(EAX, 3);          // block
        hw.WriteRegisterAt(EBX, srcAddress); // src
        Exec(hw, Instruction.FBWRITE, EAX, EBX, 0);

        Assert.Equal(payload, disk.ReadFileBlock(3));
    }

    [Fact]
    public void FbWriteThenFbRead_RoundTripsThroughTheFileBlock()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
        Hardware hw = KernelHardwareWithDisk(disk);

        int srcAddress = 64;
        byte[] payload = Pattern(BlockSize, 200);
        hw.WriteBytes(srcAddress, payload);
        hw.WriteRegisterAt(EAX, 1);          // block
        hw.WriteRegisterAt(EBX, srcAddress); // src
        Exec(hw, Instruction.FBWRITE, EAX, EBX, 0);

        int destAddress = 128;
        hw.WriteRegisterAt(EAX, destAddress); // dest
        hw.WriteRegisterAt(EBX, 1);           // block
        Exec(hw, Instruction.FBREAD, EAX, EBX, 0);

        Assert.Equal(payload, ReadRam(hw, destAddress, BlockSize));
    }

    [Fact]
    public void FbRead_OfNeverWrittenBlock_DeliversZeros()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
        Hardware hw = KernelHardwareWithDisk(disk);
        int destAddress = 100;
        // Pre-dirty the destination so we can see the read overwrite it with zeros.
        hw.WriteBytes(destAddress, Pattern(BlockSize, 77));

        hw.WriteRegisterAt(EAX, destAddress);
        hw.WriteRegisterAt(EBX, 0); // never written
        Exec(hw, Instruction.FBREAD, EAX, EBX, 0);

        Assert.Equal(new byte[BlockSize], ReadRam(hw, destAddress, BlockSize));
    }

    [Fact]
    public void FbRead_InUserMode_TrapsAsInvalidAndDoesNotCopy()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
        disk.WriteFileBlock(0, Pattern(BlockSize, 1));

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
        hw.WriteRegisterAt(EBX, 0);
        Exec(hw, Instruction.FBREAD, EAX, EBX, 0);

        Assert.True(trapped);
        Assert.Equal(Instruction.FBREAD, trappedOpcode);
        Assert.Equal(new byte[BlockSize], ReadRam(hw, destAddress, BlockSize));
    }

    [Fact]
    public void FbWrite_InUserMode_TrapsAsInvalidAndDoesNotWriteTheBlock()
    {
        Bin disk = new Bin(4, 32, 4, BlockSize);
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
        hw.WriteBytes(srcAddress, Pattern(BlockSize, 1));
        hw.WriteRegisterAt(EAX, 0);
        hw.WriteRegisterAt(EBX, srcAddress);
        Exec(hw, Instruction.FBWRITE, EAX, EBX, 0);

        Assert.True(trapped);
        Assert.Equal(Instruction.FBWRITE, trappedOpcode);
        Assert.Equal(new byte[BlockSize], disk.ReadFileBlock(0));
    }

    [Fact]
    public void FbRead_AssemblesAndDisassembles()
    {
        Assembler asm = new Assembler();
        asm.FbRead(RegisterName.EBX, RegisterName.ECX);
        byte[] code = asm.Build();

        Assert.Equal(Instruction.FBREAD, code[0]);
        Assert.Equal("FBREAD [EBX], ECX", Disassembler.Decode(code[0], code[1], code[2], code[3]));
    }

    [Fact]
    public void FbWrite_AssemblesAndDisassembles()
    {
        Assembler asm = new Assembler();
        asm.FbWrite(RegisterName.ECX, RegisterName.EBX);
        byte[] code = asm.Build();

        Assert.Equal(Instruction.FBWRITE, code[0]);
        Assert.Equal("FBWRITE ECX, [EBX]", Disassembler.Decode(code[0], code[1], code[2], code[3]));
    }
}
