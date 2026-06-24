using CSharpOS;

namespace OSTests;

/// <summary>
/// Verifies that SeedOsData (called by OperatingSystem.AttachHardware) writes the
/// correct buddy-allocator parameters, process-table header, and MLFQ fields into
/// OS memory. Tests focus on the seeding math (power-of-two rounding, Log2, heap
/// boundaries) and the bitmap initial state, which are the areas most likely to
/// regress after the buddy-allocator migration.
///
/// All tests construct a real BasicOS + Hardware pair (so AttachHardware → SeedOsData
/// fires) and then read back OS memory to verify each field directly.
/// </summary>
public class OsSeedDataTests
{
    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    private static int LargestPowerOfTwoFitting(int n)
    {
        int p = 1;
        while (p * 2 <= n)
        {
            p *= 2;
        }
        return p;
    }

    private static int Log2(int n)
    {
        int k = 0;
        while (n > 1)
        {
            n >>= 1;
            k++;
        }
        return k;
    }

    // ---- Heap parameters seeded correctly -----------------------------------

    // EDGE CASE: BuddyHeapStartOffset must equal OsMemorySize (= OsLayout.TotalSize).
    // If SeedOsData uses a stale or wrong base, the allocator walks into OS memory.
    [Fact]
    public void SeedOsData_HeapStartOffset_EqualsOsMemorySize() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act: AttachHardware called the seeding; read back.
        int seededHeapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);

        // Assert
        Assert.Equal(os.OsMemorySize, seededHeapStart);
    }

    // EDGE CASE: BuddyHeapSizeOffset must equal LargestPowerOfTwo(machineSize - OsMemorySize).
    // With MinMachineSize = OsLayout.TotalSize + 4096, available = 4096, heapSize = 4096.
    [Fact]
    public void SeedOsData_HeapSizeOffset_IsLargestPowerOfTwoOfAvailableSpace() // EDGE CASE
    {
        // Arrange
        int machineSize = Test.MinMachineSize;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        int available = machineSize - os.OsMemorySize;
        int expectedHeapSize = LargestPowerOfTwoFitting(available);

        // Act
        int seededHeapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        // Assert
        Assert.Equal(expectedHeapSize, seededHeapSize);
    }

    // EDGE CASE: BuddyLevelsOffset must equal log2(heapSize / minBlock).
    // With heapSize=4096 and minBlock=256, levels = log2(16) = 4.
    [Fact]
    public void SeedOsData_LevelsOffset_IsLog2OfHeapSizeDividedByMinBlock() // EDGE CASE
    {
        // Arrange
        int machineSize = Test.MinMachineSize;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        int available = machineSize - os.OsMemorySize;
        int heapSize = LargestPowerOfTwoFitting(available);
        int expectedLevels = Log2(heapSize / OsLayout.BuddyDefaultMinBlock);

        // Act
        int seededLevels = ReadWord(hw, OsLayout.BuddyLevelsOffset);

        // Assert
        Assert.Equal(expectedLevels, seededLevels);
    }

    // EDGE CASE: BuddyMinBlockOffset must equal BuddyDefaultMinBlock.
    // This field is read by every ISA allocator call; an incorrect seed makes all
    // level computations wrong.
    [Fact]
    public void SeedOsData_MinBlockOffset_EqualsBuddyDefaultMinBlock() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int seededMinBlock = ReadWord(hw, OsLayout.BuddyMinBlockOffset);

        // Assert
        Assert.Equal(OsLayout.BuddyDefaultMinBlock, seededMinBlock);
    }

    // ---- Bitmap initial state -----------------------------------------------

    // EDGE CASE: After seeding, the root bit (bit 0 of BuddyBitmapOffset word 0)
    // must be 1 (free). All other bitmap words must be 0.
    [Fact]
    public void SeedOsData_BitmapWord0_OnlyRootBitSet() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int word0 = ReadWord(hw, OsLayout.BuddyBitmapOffset);

        // Assert: only bit 0 must be set (root node free).
        Assert.Equal(1, word0);
    }

    // EDGE CASE: All bitmap words beyond word 0 must be zero after seeding.
    // A non-zero word would mean the allocator sees spurious free nodes in the tree.
    [Fact]
    public void SeedOsData_BitmapWordsOneThrough7_AreAllZero() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act + Assert: words 1..7 must all be zero.
        for (int w = 1; w < OsLayout.BuddyBitmapWords; w++)
        {
            int word = ReadWord(hw, OsLayout.BuddyBitmapOffset + w * 4);
            Assert.True(word == 0,
                $"BitmapWord[{w}] should be 0 after seeding, got {word}");
        }
    }

    // ---- Process table header -----------------------------------------------

    // EDGE CASE: ProcessCountOffset must be 0 after seeding — no processes loaded yet.
    [Fact]
    public void SeedOsData_ProcessCountOffset_IsZero() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int count = ReadWord(hw, OsLayout.ProcessCountOffset);

        // Assert
        Assert.Equal(0, count);
    }

    // EDGE CASE: CurrentIndexOffset must be -1 after seeding (idle: no running process).
    [Fact]
    public void SeedOsData_CurrentIndexOffset_IsNegativeOne() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int current = ReadWord(hw, OsLayout.CurrentIndexOffset);

        // Assert
        Assert.Equal(-1, current);
    }

    // ---- MLFQ fields --------------------------------------------------------

    // EDGE CASE: BoostTimerOffset must equal BoostInterval after seeding.
    // If this is 0, the first context switch immediately boosts all priorities.
    [Fact]
    public void SeedOsData_BoostTimer_EqualsBoostInterval() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int boostTimer = ReadWord(hw, OsLayout.BoostTimerOffset);

        // Assert
        Assert.Equal(OsLayout.BoostInterval, boostTimer);
    }

    // EDGE CASE: QuantumTableOffset must have 4 entries at offsets 0, 4, 8, 12.
    // With values 1, 2, 4, 255 as seeded. If any is wrong, the MLFQ demotion logic
    // fires on the wrong tick count.
    [Fact]
    public void SeedOsData_QuantumTable_HasExpectedValues() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int q0 = ReadWord(hw, OsLayout.QuantumTableOffset + 0);
        int q1 = ReadWord(hw, OsLayout.QuantumTableOffset + 4);
        int q2 = ReadWord(hw, OsLayout.QuantumTableOffset + 8);
        int q3 = ReadWord(hw, OsLayout.QuantumTableOffset + 12);

        // Assert
        Assert.Equal(1,   q0);
        Assert.Equal(2,   q1);
        Assert.Equal(4,   q2);
        Assert.Equal(255, q3);
    }

    // ---- Seeding math: power-of-two rounding --------------------------------

    // EDGE CASE: When available memory is exactly a power of two, LargestPowerOfTwo
    // must return that exact value (not half of it). With MinMachineSize giving
    // exactly 4096 bytes available, heapSize must be 4096 not 2048.
    [Fact]
    public void SeedOsData_AvailableIsPowerOfTwo_HeapSizeEqualsAvailable() // EDGE CASE
    {
        // Arrange: MinMachineSize = TotalSize + 4096, so available = 4096.
        int machineSize = OsLayout.TotalSize + 4096;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        // Act
        int seededHeapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        // Assert: available=4096 is a power of two, so heapSize must be exactly 4096.
        Assert.Equal(4096, seededHeapSize);
    }

    // EDGE CASE: When available memory is one more than a power of two, the heap
    // must round DOWN to that power of two. (e.g., available = 4097 → heapSize = 4096)
    [Fact]
    public void SeedOsData_AvailableOneBeyondPowerOfTwo_HeapSizeRoundsDown() // EDGE CASE
    {
        // Arrange: give exactly one extra byte beyond a power-of-two boundary.
        int machineSize = OsLayout.TotalSize + 4097;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        // Act
        int seededHeapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        // Assert: heapSize rounds down to 4096, not up to 8192.
        Assert.Equal(4096, seededHeapSize);
    }

    // EDGE CASE: When available memory is exactly one less than a power of two,
    // heapSize must be the next lower power of two (i.e., half).
    // e.g., available = 4095 → heapSize = 2048.
    [Fact]
    public void SeedOsData_AvailableOneBelowPowerOfTwo_HeapSizeIsHalfPower() // EDGE CASE
    {
        // Arrange
        int machineSize = OsLayout.TotalSize + 4095;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        // Act
        int seededHeapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        // Assert: available=4095 → heapSize=2048 (largest power of 2 <= 4095).
        Assert.Equal(2048, seededHeapSize);
    }

    // ---- Seeding math: Log2 for levels --------------------------------------

    // EDGE CASE: heapSize=4096, minBlock=256 → levels = log2(16) = 4.
    // leafCount = 2^4 = 16. Verifies the standard test configuration.
    [Fact]
    public void SeedOsData_StandardConfig_LevelsIsFour() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(OsLayout.TotalSize + 4096, Test.AllRegisters(), os);

        // Act
        int levels = ReadWord(hw, OsLayout.BuddyLevelsOffset);

        // Assert
        Assert.Equal(4, levels);
    }

    // EDGE CASE: heapSize=8192, minBlock=256 → levels = log2(32) = 5.
    // Verifies Log2 is correct for a larger heap.
    [Fact]
    public void SeedOsData_LargerHeap_LevelsIsCorrectlyComputed() // EDGE CASE
    {
        // Arrange: 8192 bytes available → heapSize=8192, levels=log2(8192/256)=log2(32)=5.
        int machineSize = OsLayout.TotalSize + 8192;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        // Act
        int seededHeapSize = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);
        int seededLevels   = ReadWord(hw, OsLayout.BuddyLevelsOffset);

        // Assert
        Assert.Equal(8192, seededHeapSize);
        Assert.Equal(5,    seededLevels);
    }

    // ---- Heap start is above OS region, not inside it -----------------------

    // EDGE CASE: The buddy heap must start ABOVE the OS image. If heapStart < OsMemorySize,
    // the allocator would hand out memory that overlaps the OS code/data region.
    [Fact]
    public void SeedOsData_HeapStartIsAtOrAboveOsRegionEnd() // EDGE CASE
    {
        // Arrange
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);

        // Act
        int seededHeapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);

        // Assert: the heap must not overlap the OS region.
        Assert.True(seededHeapStart >= os.OsMemorySize,
            $"HeapStart ({seededHeapStart}) is inside the OS region (< {os.OsMemorySize}); would corrupt OS data.");
    }

    // EDGE CASE: The buddy heap end (heapStart + heapSize) must not exceed the
    // machine size. If it does, the allocator produces addresses in unmapped memory.
    [Fact]
    public void SeedOsData_HeapEndDoesNotExceedMachineSize() // EDGE CASE
    {
        // Arrange
        int machineSize = Test.MinMachineSize;
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(machineSize, Test.AllRegisters(), os);

        int seededHeapStart = ReadWord(hw, OsLayout.BuddyHeapStartOffset);
        int seededHeapSize  = ReadWord(hw, OsLayout.BuddyHeapSizeOffset);

        // Assert
        Assert.True(seededHeapStart + seededHeapSize <= machineSize,
            $"Heap [{seededHeapStart}, {seededHeapStart + seededHeapSize}) overruns machine size {machineSize}.");
    }

    // ---- BuildOsImage code-region overrun guard -----------------------------

    // EDGE CASE: BuildOsImage must not throw when called under normal conditions
    // (code fits inside DataBase - CodeBase = 4060 bytes). If a recent routine
    // addition caused the code to grow beyond 4060 bytes, this test surfaces it.
    [Fact]
    public void BuildOsImage_DoesNotThrow_UnderNormalConditions() // EDGE CASE
    {
        // Act + Assert: must not throw InvalidOperationException.
        byte[] image = OsRoutines.BuildOsImage();
        Assert.NotNull(image);
        Assert.Equal(OsLayout.TotalSize, image.Length);
    }

    // EDGE CASE: The assembled code section must fit strictly within
    // [CodeBase, DataBase). Any byte at offset >= DataBase in the code block
    // indicates an overrun has silently occurred (the guard threw, but catching it
    // confirms the check is present).
    [Fact]
    public void BuildOsImage_CodeRegion_DoesNotExceedDataBase() // EDGE CASE
    {
        // Arrange: build the image.
        byte[] image = OsRoutines.BuildOsImage();

        // Act: find the last non-zero byte in the code region.
        int lastCodeByte = OsLayout.CodeBase - 1;
        for (int i = OsLayout.CodeBase; i < OsLayout.DataBase; i++)
        {
            if (image[i] != 0)
            {
                lastCodeByte = i;
            }
        }

        // Assert: the last code byte must be strictly before DataBase.
        Assert.True(lastCodeByte < OsLayout.DataBase,
            $"Code region extends to byte {lastCodeByte}, overrunning DataBase at {OsLayout.DataBase}.");
    }

    // ---- IVT entries seeded by BuildOsImage ---------------------------------

    // EDGE CASE: The IVT entry for IvtLoadProcess must point into the code region
    // [CodeBase, DataBase) so that RunOsRoutineSynchronously can dispatch it.
    // If it points to 0 or outside the code region, every allocation returns -1.
    [Fact]
    public void BuildOsImage_IvtLoadProcessEntry_PointsIntoCodeRegion() // EDGE CASE
    {
        // Arrange
        byte[] image = OsRoutines.BuildOsImage();

        // Act: read the IVT entry for IvtLoadProcess (4-byte little-endian word).
        int offset = Hardware.IvtLoadProcess * 4;
        int entryAddress = image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);

        // Assert: must point into the code region.
        Assert.True(entryAddress >= OsLayout.CodeBase,
            $"IvtLoadProcess entry ({entryAddress}) is below CodeBase ({OsLayout.CodeBase}).");
        Assert.True(entryAddress < OsLayout.DataBase,
            $"IvtLoadProcess entry ({entryAddress}) is at or above DataBase ({OsLayout.DataBase}).");
    }

    // EDGE CASE: The IVT entry for IvtHalt must point into the code region.
    // If Halt jumps to 0 (null IVT entry), BuddyFree is never called and memory leaks.
    [Fact]
    public void BuildOsImage_IvtHaltEntry_PointsIntoCodeRegion() // EDGE CASE
    {
        // Arrange
        byte[] image = OsRoutines.BuildOsImage();

        // Act
        int offset = Hardware.IvtHalt * 4;
        int entryAddress = image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);

        // Assert
        Assert.True(entryAddress >= OsLayout.CodeBase && entryAddress < OsLayout.DataBase,
            $"IvtHalt entry ({entryAddress}) does not point into code region [{OsLayout.CodeBase}, {OsLayout.DataBase}).");
    }

    // EDGE CASE: The IVT entry for IvtContextSwitch must point into the code region.
    [Fact]
    public void BuildOsImage_IvtContextSwitchEntry_PointsIntoCodeRegion() // EDGE CASE
    {
        // Arrange
        byte[] image = OsRoutines.BuildOsImage();

        // Act
        int offset = Hardware.IvtContextSwitch * 4;
        int entryAddress = image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);

        // Assert
        Assert.True(entryAddress >= OsLayout.CodeBase && entryAddress < OsLayout.DataBase,
            $"IvtContextSwitch entry ({entryAddress}) does not point into code region [{OsLayout.CodeBase}, {OsLayout.DataBase}).");
    }

    // EDGE CASE: All 9 used IVT entries must point into the code region.
    // A single loop test catches any inadvertently zeroed or wrongly-ordered entry.
    [Fact]
    public void BuildOsImage_AllUsedIvtEntries_PointIntoCodeRegion() // EDGE CASE
    {
        // Arrange
        byte[] image = OsRoutines.BuildOsImage();
        int[] usedVectors = new int[]
        {
            Hardware.IvtContextSwitch,
            Hardware.IvtSchedule,
            Hardware.IvtBlockInput,
            Hardware.IvtBlockOutput,
            Hardware.IvtWakeInput,
            Hardware.IvtWakeOutput,
            Hardware.IvtHalt,
            Hardware.IvtInvalidInstruction,
            Hardware.IvtLoadProcess
        };

        // Act + Assert
        foreach (int vector in usedVectors)
        {
            int offset = vector * 4;
            int entryAddress = image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);
            Assert.True(entryAddress >= OsLayout.CodeBase && entryAddress < OsLayout.DataBase,
                $"IVT vector {vector} entry ({entryAddress}) does not point into code region [{OsLayout.CodeBase}, {OsLayout.DataBase}).");
        }
    }

    // POTENTIAL DYSFUNCTION: IvtLoadProcess must point to the BuddyAlloc entry, NOT
    // to the resume_mlfq label (which appears directly after BuddyFree). If the
    // addresses were accidentally emitted in the wrong order, IvtLoadProcess would
    // jump past the alloc logic into the scheduler tail, returning -1 every time.
    [Fact]
    public void BuildOsImage_IvtLoadProcess_PointsBeforeIvtHalt_InCodeOrder() // POTENTIAL DYSFUNCTION
    {
        // Arrange
        byte[] image = OsRoutines.BuildOsImage();

        // Act
        int loadOffset  = Hardware.IvtLoadProcess * 4;
        int haltOffset  = Hardware.IvtHalt * 4;
        int loadAddress = image[loadOffset]  | (image[loadOffset  + 1] << 8) | (image[loadOffset  + 2] << 16) | (image[loadOffset  + 3] << 24);
        int haltAddress = image[haltOffset]  | (image[haltOffset  + 1] << 8) | (image[haltOffset  + 2] << 16) | (image[haltOffset  + 3] << 24);

        // Assert: per the emit order in BuildOsImage, Halt is emitted before LoadProcess.
        // So loadAddress > haltAddress in code order.
        Assert.True(loadAddress > haltAddress,
            $"IvtLoadProcess ({loadAddress}) should be assembled after IvtHalt ({haltAddress}); emit order may be wrong.");
    }
}
