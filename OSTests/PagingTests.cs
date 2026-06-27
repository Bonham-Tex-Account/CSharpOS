using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Paging Phase 1: the MMU translates user-mode data/stack addresses through a per-process
/// page table stored in the OS region, while each process still occupies one contiguous
/// buddy block (linear mapping). These tests prove the translation is real (the MMU reads
/// the table, not just a linear fallback), exact for any block alignment, page-offset
/// correct, independent per process, and that kernel/OS and non-OS-managed access stays
/// absolute. Behavior-preservation of real programs is covered by the rest of the suite.
/// </summary>
public class PagingTests : IDisposable
{
    private static int Memory => Test.MinMachineSize;
    private readonly List<string> tempFiles = new List<string>();

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csospaging_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, bytes);
        tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // Boots the OS, loads one process with the given memory/stack, and sets its layout
    // (which seeds the page table, exactly as a scheduler resume would). BasicOS reserves
    // the OS region at base 0, so OsLayout offsets are absolute addresses.
    private (Hardware hw, Process process, int index) LoadAndLayout(int requiredMemory, int requiredStackSize, int index = 0)
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), requiredMemory, requiredStackSize);
        os.LoadProcess(process);
        hw.SetLayoutFromEntry(OsLayout.ProcessEntryAddress(index));
        return (hw, process, index);
    }

    private static int ProcessPageCount(Process process)
    {
        int totalSize = process.ProgramSize + process.RequiredMemory + process.RequiredStackSize + Hardware.KernelStackSize;
        return (totalSize + OsLayout.PageSize - 1) / OsLayout.PageSize;
    }

    [Fact]
    public void PageTable_IsSeededLinearly_OverTheContiguousBlock()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);

        Assert.Equal(process.ProgramAddress, hw.PageTableEntry(index, 0));
        Assert.Equal(process.ProgramAddress + OsLayout.PageSize, hw.PageTableEntry(index, 1));
        Assert.Equal(process.ProgramAddress + 2 * OsLayout.PageSize, hw.PageTableEntry(index, 2));
    }

    [Fact]
    public void PageTable_MarksPagesBeyondTheProcessUnmapped()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        int pageCount = ProcessPageCount(process);

        // The last covered page is mapped; the first page past the block is not.
        Assert.NotEqual(OsLayout.UnmappedPage, hw.PageTableEntry(index, pageCount - 1));
        Assert.Equal(OsLayout.UnmappedPage, hw.PageTableEntry(index, pageCount));
    }

    [Fact]
    public void Translate_UserMode_MapsThroughPageTable_ExactForAnyAlignment()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        // Across page boundaries and at page-internal offsets, the physical address is
        // exactly ProgramAddress + virtual (regardless of whether ProgramAddress is
        // PageSize-aligned).
        int[] virtuals = { 0, 5, OsLayout.PageSize - 1, OsLayout.PageSize, OsLayout.PageSize + 10, 3 * OsLayout.PageSize + 7 };
        foreach (int v in virtuals)
        {
            Assert.Equal(process.ProgramAddress + v, hw.TranslateDataAddress(v));
        }
    }

    [Fact]
    public void Translate_FollowsThePageTable_NotALinearFormula()
    {
        // Strongest proof the MMU is real: rewrite one PTE to a non-linear physical base
        // and confirm translation follows it rather than computing base+offset.
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        int remappedBase = process.ProgramAddress + 10 * OsLayout.PageSize;
        Test.WriteWord(hw, OsLayout.PageTableAddress(index) + 2 * OsLayout.PageTableEntryBytes, remappedBase);

        // A virtual address in page 2 now lands in the remapped physical page, with the
        // in-page offset preserved.
        Assert.Equal(remappedBase + 7, hw.TranslateDataAddress(2 * OsLayout.PageSize + 7));
        // Page 0 is untouched and still maps linearly.
        Assert.Equal(process.ProgramAddress + 7, hw.TranslateDataAddress(7));
    }

    [Fact]
    public void Translate_KernelMode_IsAbsoluteIdentity()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);

        // Kernel/OS code addresses memory absolutely (program base 0): no translation.
        Assert.Equal(1000, hw.TranslateDataAddress(1000));
        Assert.Equal(0, hw.TranslateDataAddress(0));
    }

    [Fact]
    public void Translate_UnmappedPage_FallsBackToLinear()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        int pageCount = ProcessPageCount(process);
        int v = pageCount * OsLayout.PageSize + 3; // first unmapped page (PTE == -1)
        Assert.Equal(process.ProgramAddress + v, hw.TranslateDataAddress(v));
    }

    [Fact]
    public void Translate_PageBeyondTableRange_FallsBackToLinear()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        // A virtual address whose page index is outside the table is mapped linearly.
        int v = OsLayout.MaxPagesPerProcess * OsLayout.PageSize + 1;
        Assert.Equal(process.ProgramAddress + v, hw.TranslateDataAddress(v));
    }

    [Fact]
    public void Translate_NonOsManagedHardware_IsLinear()
    {
        // Bare instruction-level hardware (no OS image) has no page tables; user accesses
        // use the plain program base, so low-level instruction tests are unaffected.
        Hardware hw = Test.NewHardware(512, new FakeOS());
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        Assert.Equal(hw.GetProgramBase() + 40, hw.TranslateDataAddress(40));
    }

    [Fact]
    public void TwoProcesses_HaveIndependentPageTables()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Process first = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        Process second = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        os.LoadProcess(first);
        os.LoadProcess(second);
        hw.SetLayoutFromEntry(OsLayout.ProcessEntryAddress(0));
        hw.SetLayoutFromEntry(OsLayout.ProcessEntryAddress(1));

        Assert.Equal(first.ProgramAddress, hw.PageTableEntry(0, 0));
        Assert.Equal(second.ProgramAddress, hw.PageTableEntry(1, 0));
        Assert.NotEqual(hw.PageTableEntry(0, 0), hw.PageTableEntry(1, 0));
    }
}
