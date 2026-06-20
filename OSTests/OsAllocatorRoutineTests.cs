using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Isolation tests for the first-fit allocator (LoadProcess routine) and the
/// free-list reclaim performed by Halt, driven synchronously against a hand-seeded
/// free list in OS memory.
/// </summary>
public class OsAllocatorRoutineTests
{
    private const byte EAX = 0;
    private const byte EBX = 1;

    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(16384, new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

    private static void SeedFreeRange(Hardware hw, int index, int start, int size)
    {
        int slot = OsLayout.FreeRangeTableOffset + index * OsLayout.FreeRangeSize;
        WriteWord(hw, slot, start);
        WriteWord(hw, slot + 4, size);
    }

    private static int FreeRangeStart(Hardware hw, int index)
    {
        return ReadWord(hw, OsLayout.FreeRangeTableOffset + index * OsLayout.FreeRangeSize);
    }

    private static int FreeRangeSizeAt(Hardware hw, int index)
    {
        return ReadWord(hw, OsLayout.FreeRangeTableOffset + index * OsLayout.FreeRangeSize + 4);
    }

    [Fact]
    public void LoadProcess_FirstFit_AllocatesAndSplitsRange()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        SeedFreeRange(hw, 0, 4096, 8000);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 500);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 0);

        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);

        Assert.Equal(4096, ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress));
        Assert.Equal(4096 + 500, FreeRangeStart(hw, 0));
        Assert.Equal(8000 - 500, FreeRangeSizeAt(hw, 0));
    }

    [Fact]
    public void LoadProcess_SkipsTooSmallRange_PicksFirstThatFits()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 2);
        SeedFreeRange(hw, 0, 4096, 100);   // too small
        SeedFreeRange(hw, 1, 9000, 2000);  // fits

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 500);

        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);

        Assert.Equal(9000, ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress));
        Assert.Equal(100, FreeRangeSizeAt(hw, 0));        // untouched
        Assert.Equal(9000 + 500, FreeRangeStart(hw, 1));  // split
        Assert.Equal(2000 - 500, FreeRangeSizeAt(hw, 1));
    }

    [Fact]
    public void LoadProcess_NoRangeFits_SetsProgramAddressToFailureSentinel()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        SeedFreeRange(hw, 0, 4096, 100);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 500);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 12345);

        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);

        Assert.Equal(-1, ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress));
        Assert.Equal(100, FreeRangeSizeAt(hw, 0)); // untouched
    }

    [Fact]
    public void LoadProcess_ExactFit_ConsumesRange()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        SeedFreeRange(hw, 0, 4096, 500);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 500);

        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);

        Assert.Equal(4096, ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress));
        Assert.Equal(4096 + 500, FreeRangeStart(hw, 0));
        Assert.Equal(0, FreeRangeSizeAt(hw, 0)); // zero-sized leftover
    }

    [Fact]
    public void Halt_ReturnsProcessMemoryToFreeList()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 0);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 5000);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 800);

        hw.DispatchOsRoutine(Hardware.IvtHalt);
        for (int step = 0; step < 2000 && hw.GetPrivilegeLevel() == PrivilegeLevel.Privileged; step++)
        {
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }

        Assert.Equal(1, ReadWord(hw, OsLayout.FreeRangeCountOffset));
        Assert.Equal(5000, FreeRangeStart(hw, 0));
        Assert.Equal(800, FreeRangeSizeAt(hw, 0));
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void Halt_AppendsToNonEmptyFreeList()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // Seed one existing free range so Halt must append, not insert.
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        SeedFreeRange(hw, 0, 1000, 200);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 5000);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 800);

        hw.DispatchOsRoutine(Hardware.IvtHalt);
        for (int step = 0; step < 2000 && hw.GetPrivilegeLevel() == PrivilegeLevel.Privileged; step++)
        {
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }

        // Free list now has 2 entries; the existing range is untouched.
        Assert.Equal(2, ReadWord(hw, OsLayout.FreeRangeCountOffset));
        Assert.Equal(1000, FreeRangeStart(hw, 0));
        Assert.Equal(200, FreeRangeSizeAt(hw, 0));
        // The reclaimed range was appended at index 1.
        Assert.Equal(5000, FreeRangeStart(hw, 1));
        Assert.Equal(800, FreeRangeSizeAt(hw, 1));
    }

    [Fact]
    public void InvalidInstruction_ReturnsProcessMemoryToFreeList()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 0);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, 6000);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 400);

        hw.DispatchOsRoutine(Hardware.IvtInvalidInstruction);
        for (int step = 0; step < 2000 && hw.GetPrivilegeLevel() == PrivilegeLevel.Privileged; step++)
        {
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }

        Assert.Equal(1, ReadWord(hw, OsLayout.FreeRangeCountOffset));
        Assert.Equal(6000, FreeRangeStart(hw, 0));
        Assert.Equal(400, FreeRangeSizeAt(hw, 0));
        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry + Hardware.ProcessEntryState));
    }

    [Fact]
    public void RunOsRoutineSynchronously_RestoresCpuStateAfterRoutine()
    {
        Hardware hw = NewSeededHardware();
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        SeedFreeRange(hw, 0, 4096, 8000);

        int entry = OsLayout.ProcessEntryAddress(0);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, 100);

        // Establish a known CPU state before the synchronous call.
        hw.WriteRegisterAt(EAX, 0xAA);
        hw.WriteRegisterAt(EBX, 0xBB);
        int savedIp = 1234;
        hw.SetInstructionPointer(savedIp);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);

        // Registers, IP, and privilege level must be exactly restored.
        Assert.Equal(0xAA, hw.ReadRegisterAt(EAX));
        Assert.Equal(0xBB, hw.ReadRegisterAt(EBX));
        Assert.Equal(savedIp, hw.GetInstructionPointer());
        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
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
