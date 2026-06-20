using CSharpOS;

namespace OSTests;

public class HardwareTests
{
    [Fact]
    public void Constructor_SetsMemorySize()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Assert.Equal(512, hw.GetMemorySize());
    }

    [Fact]
    public void Constructor_AttachesItselfToOs()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Assert.Equal(1, os.AttachHardwareCount);
        Assert.Same(hw, os.LastAttachedHardware);
    }

    [Fact]
    public void Constructor_StartsWithZeroInstructionCount()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_IMM, 0, 1, 0));
        hw.SetInstructionPointer(0);
        hw.Run();
        Assert.Equal(0, os.ContextSwitchCount);
    }

    [Fact]
    public void WriteBytes_ThenReadBytes_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        hw.WriteBytes(100, data);
        byte[] read = hw.ReadBytes(100);
        Assert.Equal(data, read);
    }

    [Fact]
    public void ReadBytes_AlwaysReturnsFourBytes()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        byte[] read = hw.ReadBytes(0);
        Assert.Equal(4, read.Length);
    }

    [Fact]
    public void WriteBytes_WritesOnlyProvidedLength()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(10, new byte[] { 1, 2 });
        byte[] read = hw.ReadBytes(10);
        Assert.Equal(1, read[0]);
        Assert.Equal(2, read[1]);
        Assert.Equal(0, read[2]);
        Assert.Equal(0, read[3]);
    }

    [Fact]
    public void SetInstructionPointer_ThenGet_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.SetInstructionPointer(1234);
        Assert.Equal(1234, hw.GetInstructionPointer());
    }

    [Fact]
    public void WriteRegisterAt_ThenReadRegisterAt_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, 0x12345678);
        Assert.Equal(0x12345678, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void WriteRegisterAt_StoresLittleEndian()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, 0x12345678);
        byte[] registers = hw.ReadRegisters();
        Assert.Equal(0x78, registers[0]);
        Assert.Equal(0x56, registers[1]);
        Assert.Equal(0x34, registers[2]);
        Assert.Equal(0x12, registers[3]);
    }

    [Fact]
    public void ReadRegisterAt_InterpretsValueAsSignedInt()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, -1);
        Assert.Equal(-1, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void WriteRegister_ByName_ThenReadRegister_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegister(RegisterName.ESP, 999);
        Assert.Equal(999, hw.ReadRegister(RegisterName.ESP));
    }

    [Fact]
    public void RegisterAtIndex_AndRegisterByName_ShareStorage()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        // EAX is declared first, so its index is 0.
        hw.WriteRegisterAt(0, 42);
        Assert.Equal(42, hw.ReadRegister(RegisterName.EAX));
    }

    [Fact]
    public void WriteRegisters_OverwritesEntireRegisterFile()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        byte[] state = new byte[Test.AllRegisters().Length * 4];
        state[0] = 0x01;
        state[4] = 0x02;
        hw.WriteRegisters(state);
        Assert.Equal(1, hw.ReadRegisterAt(0));
        Assert.Equal(2, hw.ReadRegisterAt(1));
    }

    [Fact]
    public void LoadProcess_WritesProgramIntoMemory()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Process process = new Process("ignored", 16, 16);
        process.ProgramAddress = 0;
        byte[] program = new byte[] { 9, 8, 7, 6 };
        hw.LoadProcess(process, program);
        Assert.Equal(program, hw.ReadBytes(0));
    }

    [Fact]
    public void GetCurrentProcessRanges_MergesContiguousRegions()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Process process = new Process("ignored", 16, 16);
        process.ProgramAddress = 0;
        byte[] program = new byte[] { 0, 0, 0, 0 };
        hw.LoadProcess(process, program);

        List<MemoryRange> ranges = hw.GetCurrentProcessRanges();

        // program (4) + kernel section (128) + memory (16) + user stack (16) +
        // kernel stack (64) are contiguous from 0.
        Assert.Single(ranges);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(4 + (Hardware.KernelHeaderSize + os.KernelImage.Length) + 16 + 16 + Hardware.KernelStackSize, ranges[0].Size);
    }

    [Fact]
    public void TrapInvalidInstruction_FiresHardwareEvent()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };

        hw.TrapInvalidInstruction(0xAB, 1, 2, 3);

        Assert.NotNull(captured);
        Assert.Equal(0xAB, captured!.Opcode);
        Assert.Equal(1, captured.B1);
        Assert.Equal(2, captured.B2);
        Assert.Equal(3, captured.B3);
    }

    [Fact]
    public void Run_AdvancesInstructionPointerByFour()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_IMM, 0, 5, 0));
        hw.SetInstructionPointer(0);
        hw.Run();
        Assert.Equal(4, hw.GetInstructionPointer());
    }

    [Fact]
    public void Run_ExecutesInstructionAtCurrentPointer()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(0, Test.Word(Instruction.MOV_REG_IMM, 0, 42, 0));
        hw.SetInstructionPointer(0);
        hw.Run();
        Assert.Equal(42, hw.ReadRegisterAt(0));
    }

    [Fact]
    public void Run_InvalidOpcode_FiresFaultEventAndRaisesToPrivileged()
    {
        // Without an OS image, an invalid opcode fires the fault event and raises to
        // Privileged (the teardown level); with an OS image the routine would run.
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };
        hw.WriteBytes(0, Test.Word(0xFF, 0, 0, 0));
        hw.SetInstructionPointer(0);

        hw.Run();

        Assert.NotNull(captured);
        Assert.Equal(0xFF, captured!.Opcode);
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void Constructor_BootsInUserMode()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void SetPrivilegeLevel_RoundTrips()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void LoadProcess_SeedsStackPointerToTopOfUserStack()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", 16, 16);
        process.ProgramAddress = 0;
        // Mimic the OS: register state begins after the program + kernel section.
        process.RegisterStateAddress = 4 + (Hardware.KernelHeaderSize + os.KernelImage.Length);
        byte[] program = new byte[] { 0, 0, 0, 0 };
        hw.LoadProcess(process, program);

        // ESP is seeded into the saved register state; load it into the registers
        // as a context switch would, then read it back.
        byte[] savedState = hw.ReadRegisterState(process.RegisterStateAddress);
        hw.WriteRegisters(savedState);

        int expectedTop = 4 + (Hardware.KernelHeaderSize + os.KernelImage.Length) + process.RequiredMemory + process.RequiredStackSize;
        Assert.Equal(expectedTop, hw.ReadRegister(RegisterName.ESP));
    }

    [Fact]
    public void GetCurrentProcessRanges_IncludesKernelStackInTotal()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", 16, 16);
        process.ProgramAddress = 0;
        process.RegisterStateAddress = 4 + (Hardware.KernelHeaderSize + os.KernelImage.Length);
        hw.LoadProcess(process, new byte[] { 0, 0, 0, 0 });

        List<MemoryRange> ranges = hw.GetCurrentProcessRanges();

        // The reserved kernel stack (64) is part of the process's footprint.
        Assert.Single(ranges);
        Assert.Equal(4 + (Hardware.KernelHeaderSize + os.KernelImage.Length) + 16 + 16 + Hardware.KernelStackSize, ranges[0].Size);
    }

    [Fact]
    public void Halt_WithoutOsImage_RaisesToPrivileged()
    {
        // HLT is an OS-level teardown request. Without an OS image the machine just
        // raises to Privileged (with an image, the Halt routine frees the process).
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteBytes(0, Test.Word(Instruction.HLT, 0, 0, 0));
        hw.SetInstructionPointer(0);

        hw.Run();

        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
    }
}
