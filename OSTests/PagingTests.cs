using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Paging (Phase 2, increment 1 — demand fault-in): user-mode data/stack accesses
/// translate through a per-process page table in the OS region. Pages start
/// **non-resident** and fault in on first touch via the ISA IvtPageFault handler, which
/// makes the page resident (pointing its PTE at the page's physical home in the process's
/// allocated block). Kernel/OS access stays absolute. Demand paging is transparent: real
/// programs run identically (covered by the rest of the suite); these tests prove the
/// seeding, the fault path, and the resident transition.
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

    // Boots the OS, loads one process, and sets its layout (which seeds its page table as
    // a scheduler resume would). BasicOS reserves the OS region at base 0, so OsLayout
    // offsets are absolute addresses.
    private (Hardware hw, Process process, int index) LoadAndLayout(int requiredMemory, int requiredStackSize, int index = 0)
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), requiredMemory, requiredStackSize);
        os.LoadProcess(process);
        hw.SetLayoutFromEntry(OsLayout.ProcessEntryAddress(index));
        return (hw, process, index);
    }

    private static int UserPageCount(Process process)
    {
        int userExtent = process.ProgramSize + process.RequiredMemory + process.RequiredStackSize;
        return (userExtent + OsLayout.PageSize - 1) / OsLayout.PageSize;
    }

    [Fact]
    public void PageTable_SeedsUserPagesNonResident_AndBeyondUnmapped()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        int pageCount = UserPageCount(process);

        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(index, 0));
        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(index, pageCount - 1));
        Assert.Equal(OsLayout.UnmappedPage, hw.PageTableEntry(index, pageCount));
    }

    [Fact]
    public void FreshProcess_HasNoResidentPages()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        for (int p = 0; p < UserPageCount(process); p++)
        {
            Assert.False(hw.IsPageResident(index, p));
        }
    }

    [Fact]
    public void TouchingANonResidentPage_RaisesAFault()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        // A user data access to a non-resident page fails translation (a fault is raised
        // and the instruction must abort).
        bool ok = hw.TryTranslateData(OsLayout.PageSize + 8, false, out int _);
        Assert.False(ok);
    }

    [Fact]
    public void DemandFaultIn_RunsAProgramThroughThePageFaultHandler()
    {
        // STORE a value to a data page, read it back, and output it. The store/load fault
        // their data page in via the ISA handler; a correct result proves demand paging
        // works end to end, and the touched page ends up resident.
        int dataAddress = OsLayout.PageSize + 44; // page 1, in the data region (past the code)
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 99);
        asm.MovImm16(RegisterName.EBX, dataAddress);     // 16-bit: addresses exceed MovImm's 8 bits
        asm.Store(RegisterName.EBX, RegisterName.EAX);   // [data] = 99  (fault in page 1)
        asm.MovImm(RegisterName.EAX, 0);
        asm.Load(RegisterName.EAX, RegisterName.EBX);    // EAX = [data] (page 1 resident)
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 512, 512));

        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        int steps = 0;
        while (os.HasProcesses && steps < 5000)
        {
            hw.Run();
            steps++;
        }

        Assert.Contains(99, outputs);                 // round-tripped through demand paging
        Assert.True(hw.IsPageResident(0, 1));         // the data page was faulted in on touch
        // Page 0 holds only the program code, which is fetched untranslated, so a data
        // fault never brings it in — it stays non-resident.
        Assert.False(hw.IsPageResident(0, 0));
    }

    [Fact]
    public void Translate_KernelMode_IsAbsoluteIdentity_NoFault()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);

        Assert.True(hw.TryTranslateData(1000, false, out int physical));
        Assert.Equal(1000, physical);
    }

    [Fact]
    public void Translate_PageBeyondTableRange_FallsBackToLinear_NoFault()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, index);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);

        int v = OsLayout.MaxPagesPerProcess * OsLayout.PageSize + 1;
        Assert.True(hw.TryTranslateData(v, false, out int physical));
        Assert.Equal(process.ProgramAddress + v, physical);
    }

    [Fact]
    public void ResidentPage_StaysResidentAcrossResumes_SeedOnceGuard()
    {
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);

        // Simulate the page-fault handler making page 0 resident.
        int residentBase = process.ProgramAddress;
        Test.WriteWord(hw, OsLayout.PageTableAddress(index), residentBase);
        Assert.True(hw.IsPageResident(index, 0));

        // Re-resuming the same process must not clobber its resident pages back to
        // non-resident (the seed-once guard).
        hw.SetLayoutFromEntry(OsLayout.ProcessEntryAddress(index));
        Assert.Equal(residentBase, hw.PageTableEntry(index, 0));
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

        // Both seeded independently; making one resident does not affect the other.
        Test.WriteWord(hw, OsLayout.PageTableAddress(0), first.ProgramAddress);
        Assert.True(hw.IsPageResident(0, 0));
        Assert.False(hw.IsPageResident(1, 0));
        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(1, 0));
    }
}
