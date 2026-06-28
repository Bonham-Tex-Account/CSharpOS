using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// Paging (Phase 2): user-mode data/stack accesses translate through a per-process page
/// table in the OS region. Pages start **non-resident** and fault in on first touch via
/// the ISA IvtPageFault handler. Increment 1 added demand fault-in; increment 2 adds a
/// small shared physical **frame pool** with **LRU eviction** and **dirty write-back**:
/// the handler maps a faulting page into a free frame, or evicts the least-recently-used
/// resident frame (writing it back to its block home first if dirty) when the pool is
/// full, and flips the evicted page's PTE back to non-resident. A resident PTE holds the
/// frame's physical base. Kernel/OS access stays absolute. Demand paging is transparent:
/// real programs run identically (covered by the rest of the suite); these tests prove the
/// seeding, the fault path, the resident transition, eviction, LRU order, and write-back.
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

        // Simulate the page-fault handler making page 0 resident (a resident PTE holds a
        // frame base in the pool).
        int residentBase = OsLayout.FrameBase(0);
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
        Test.WriteWord(hw, OsLayout.PageTableAddress(0), OsLayout.FrameBase(0));
        Assert.True(hw.IsPageResident(0, 0));
        Assert.False(hw.IsPageResident(1, 0));
        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(1, 0));
    }

    // ---- increment 2: frame pool + LRU eviction + dirty write-back --------------

    // Builds one process from `code`, runs it to completion (or a step cap), and returns
    // the hardware (for frame/page inspection) plus everything it OUT-ed.
    private (Hardware hw, List<int> outputs) BuildAndRun(byte[] code, int requiredMemory, int requiredStackSize)
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(code), requiredMemory, requiredStackSize));

        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        int steps = 0;
        while (os.HasProcesses && steps < 20000)
        {
            hw.Run();
            steps++;
        }
        return (hw, outputs);
    }

    // The frame currently holding process `proc`'s virtual page `page`, or -1 if none.
    private static int FrameHolding(Hardware hw, int proc, int page)
    {
        for (int f = 0; f < OsLayout.FrameCount; f++)
        {
            if (hw.FrameOccupied(f) && hw.FrameOwnerProcess(f) == proc && hw.FrameOwnerPage(f) == page)
            {
                return f;
            }
        }
        return -1;
    }

    // Emits "STORE [page*PageSize + 8] = value" — a write that faults `page` in on first touch.
    private static void EmitStorePage(Assembler asm, int page, int value)
    {
        asm.MovImm(RegisterName.EAX, value);
        asm.MovImm16(RegisterName.EBX, page * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX);
    }

    [Fact]
    public void Eviction_ResidentFramesNeverExceedThePool()
    {
        // Touch more distinct data pages than the pool has frames: residency must cap at
        // the pool size (the surplus pages are evicted), and the earliest-touched page goes.
        int pages = OsLayout.FrameCount + 2;
        Assembler asm = new Assembler();
        for (int p = 1; p <= pages; p++)
        {
            EmitStorePage(asm, p, p);
        }
        asm.Hlt();

        (Hardware hw, List<int> _) = BuildAndRun(asm.Build(), 2048, 512);

        Assert.Equal(OsLayout.FrameCount, hw.ResidentFrameCount());   // pool is full, never over
        Assert.False(hw.IsPageResident(0, 1));                         // earliest touched: evicted
        Assert.True(hw.IsPageResident(0, pages));                      // most recent: resident
    }

    [Fact]
    public void Lru_RecentlyUsedPageIsProtected_LeastRecentlyUsedIsEvicted()
    {
        // Fill the pool with pages 1..N, then re-touch page 1 (making it most-recently-used),
        // then fault in one more page. The victim must be page 2 (now the LRU), not page 1.
        int n = OsLayout.FrameCount;
        Assembler asm = new Assembler();
        for (int p = 1; p <= n; p++)
        {
            EmitStorePage(asm, p, p);
        }
        EmitStorePage(asm, 1, 100);   // page 1 becomes most-recently-used
        EmitStorePage(asm, n + 1, 0); // faults: must evict the LRU = page 2
        asm.Hlt();

        (Hardware hw, List<int> _) = BuildAndRun(asm.Build(), 2048, 512);

        Assert.True(hw.IsPageResident(0, 1));       // protected by recent use
        Assert.False(hw.IsPageResident(0, 2));      // the least-recently-used victim
        Assert.True(hw.IsPageResident(0, n + 1));   // the page that forced the eviction
    }

    [Fact]
    public void DirtyWriteBack_EvictedPageReloadsWithItsWrittenValue()
    {
        // Write a value to page 1, evict it under pressure (it is dirty, so it is written
        // back to its home), then read page 1 again — it faults back in from home and must
        // carry the written value. A broken write-back would yield the home's initial 0.
        int n = OsLayout.FrameCount;
        Assembler asm = new Assembler();
        EmitStorePage(asm, 1, 77);            // page 1 dirty = 77
        for (int p = 2; p <= n; p++)
        {
            EmitStorePage(asm, p, p);         // fill the rest of the pool
        }
        EmitStorePage(asm, n + 1, 88);        // faults: evicts page 1 (LRU, dirty) -> home = 77
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX); // faults page 1 back in from home
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        (Hardware hw, List<int> outputs) = BuildAndRun(asm.Build(), 2048, 512);

        Assert.Contains(77, outputs);   // the written value survived eviction + reload
    }

    [Fact]
    public void DirtyBit_SetByAWrite_ClearForAReadOnlyPage()
    {
        // A stored-to page is dirty (must be written back on eviction); a read-only page is
        // clean (can be dropped). The pool is large enough that neither is evicted here.
        Assembler asm = new Assembler();
        EmitStorePage(asm, 1, 7);                          // page 1: written -> dirty
        asm.MovImm16(RegisterName.EBX, 2 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX);      // page 2: read only -> clean
        asm.Hlt();

        (Hardware hw, List<int> _) = BuildAndRun(asm.Build(), 2048, 512);

        int dirtyFrame = FrameHolding(hw, 0, 1);
        int cleanFrame = FrameHolding(hw, 0, 2);
        Assert.True(dirtyFrame >= 0);
        Assert.True(cleanFrame >= 0);
        Assert.True(hw.FrameDirty(dirtyFrame));
        Assert.False(hw.FrameDirty(cleanFrame));
    }

    [Fact]
    public void FaultedPage_MapsToAPoolFrame_RecordedInTheCoreMap()
    {
        // A faulted-in page's PTE points into the physical frame pool, and the core map
        // records the owner/page for that frame.
        Assembler asm = new Assembler();
        EmitStorePage(asm, 1, 5);
        asm.Hlt();

        (Hardware hw, List<int> _) = BuildAndRun(asm.Build(), 2048, 512);

        int pte = hw.PageTableEntry(0, 1);
        Assert.True(pte >= OsLayout.FramePoolBase && pte < OsLayout.FramePoolBase + OsLayout.FramePoolSize);
        int frame = FrameHolding(hw, 0, 1);
        Assert.Equal(OsLayout.FrameBase(frame), pte);
    }

    // ---- increment 3: Bin-disk swap backing for data pages ----------------------

    [Fact]
    public void DataPages_SeededSwapBacked_CodeAndStackStayRamHome()
    {
        // A data-region page is seeded with a swap-slot PTE; a code page (page 0) and a
        // stack page keep the RAM-home -2 sentinel.
        (Hardware hw, Process process, int index) = LoadAndLayout(512, 512);

        // Page 0 is code (the 4-byte image) -> RAM-home.
        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(index, 0));

        // A page whose start lies in the data region is swap-backed: its PTE decodes to the
        // page's deterministic swap slot.
        int dataPage = process.ProgramSize / OsLayout.PageSize + 1; // first full page past the code, inside data
        int pte = hw.PageTableEntry(index, dataPage);
        Assert.True(OsLayout.IsSwapPte(pte), $"data page {dataPage} PTE {pte} should be swap-backed");
        Assert.Equal(OsLayout.SwapSlot(index, dataPage), OsLayout.SwapSlotFromPte(pte));

        // The last user page is in the stack region -> RAM-home.
        int stackPage = UserPageCount(process) - 1;
        Assert.Equal(OsLayout.NonResidentPage, hw.PageTableEntry(index, stackPage));
    }

    [Fact]
    public void DataPageFault_BringsItIntoAFrameTaggedWithItsSwapSlot()
    {
        Assembler asm = new Assembler();
        EmitStorePage(asm, 4, 5); // page 4 sits in the data region (reqMemory 2048 = 8 pages)
        asm.Hlt();

        (Hardware hw, List<int> _) = BuildAndRun(asm.Build(), 2048, 512);

        int frame = FrameHolding(hw, 0, 4);
        Assert.True(frame >= 0, "the data page should be resident in a frame");
        Assert.Equal(OsLayout.SwapSlot(0, 4), hw.FrameSwap(frame)); // frame knows its swap backing
    }

    [Fact]
    public void DirtyDataPage_WrittenBackToDisk_SurvivesEvictionAndReload()
    {
        // Same round-trip as the inc-2 write-back test, but the backing store is now a Bin
        // disk swap slot: the evicted dirty page is DWRITE-n to disk and DREAD back.
        int n = OsLayout.FrameCount;
        Assembler asm = new Assembler();
        EmitStorePage(asm, 1, 66);            // data page 1 dirtied with 66
        for (int p = 2; p <= n; p++)
        {
            EmitStorePage(asm, p, p);
        }
        EmitStorePage(asm, n + 1, 99);        // forces page 1 out to its swap slot
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX); // faults page 1 back in from disk
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        (Hardware hw, List<int> outputs) = BuildAndRun(asm.Build(), 2048, 512);

        Assert.Contains(66, outputs); // value round-tripped through the disk swap slot
    }

    [Fact]
    public void ForkedChild_InheritsTheParentsDataPagesThroughSwap()
    {
        // The parent writes a data page, then forks. The child must see that value in the
        // inherited page — fork flushes the parent's dirty frame to its swap slot and
        // deep-copies the parent's swap slots into the child's.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 55);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX); // data page 1 = 55 (parent, pre-fork)
        asm.Fork();
        asm.MovImm(RegisterName.ECX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.ECX);
        asm.Jz("child");
        asm.Hlt();                                     // parent: exit (no output)
        asm.Label("child");
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX);  // read the inherited data page
        asm.Out(RegisterName.EAX);                     // should be 55
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 2048, 512));
        List<int> outputs = new List<int>();
        // Two processes (parent + forked child) output on different devices, so complete the
        // specific device that produced each output (else the child blocks forever on stdout).
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(e.Device); };
        int steps = 0;
        while (os.HasProcesses && steps < 50000)
        {
            hw.Run();
            steps++;
        }

        Assert.Contains(55, outputs); // child inherited the parent's swapped data page
    }

    // ---- Phase 3: copy-on-write fork (data pages) -------------------------------

    // Runs a two-process (fork) program to completion, completing output per-device so a
    // forked child (on its own stdout device) is never left blocked.
    private List<int> RunForkProgram(byte[] code, int requiredMemory, int requiredStackSize)
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MachineWithHeap(16384), Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(code), requiredMemory, requiredStackSize));
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(e.Device); };
        int steps = 0;
        while (os.HasProcesses && steps < 100000)
        {
            hw.Run();
            steps++;
        }
        return outputs;
    }

    [Fact]
    public void Cow_ChildWrite_StaysPrivate_ParentUnaffected()
    {
        // Parent sets data page 1 = 10, forks (page shared copy-on-write), the child writes
        // 20 to it. COW must give the child a private copy: child sees 20, parent still 10.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 10);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX);     // page 1 = 10
        asm.Fork();
        asm.MovImm(RegisterName.ECX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.ECX);
        asm.Jz("child");
        // parent: wait for the child, then read page 1 (must still be 10)
        asm.Wait(RegisterName.EAX);                         // EAX = child pid
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                          // expect 10
        asm.Hlt();
        asm.Label("child");
        asm.MovImm(RegisterName.EAX, 20);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX);      // child writes 20 -> COW copy
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                          // expect 20
        asm.Hlt();

        List<int> outputs = RunForkProgram(asm.Build(), 2048, 512);

        Assert.Contains(20, outputs); // child saw its own write
        Assert.Contains(10, outputs); // parent's page was not affected by the child's write
    }

    [Fact]
    public void Cow_ParentWrite_StaysPrivate_ChildSeesSnapshot()
    {
        // Parent sets data page 1 = 10, forks, then the parent writes 99. The child must
        // still see the fork-time snapshot (10); the parent sees its own write (99).
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 10);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX);     // page 1 = 10
        asm.Fork();
        asm.MovImm(RegisterName.ECX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.ECX);
        asm.Jz("child");
        // parent: save child pid, write 99, wait, then read (must be 99)
        asm.Mov(RegisterName.EDX, RegisterName.EAX);        // EDX = child pid
        asm.MovImm(RegisterName.EAX, 99);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Store(RegisterName.EBX, RegisterName.EAX);      // parent writes 99 -> COW copy
        asm.Wait(RegisterName.EDX);
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                          // expect 99
        asm.Hlt();
        asm.Label("child");
        asm.MovImm16(RegisterName.EBX, 1 * OsLayout.PageSize + 8);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                          // expect 10 (snapshot)
        asm.Hlt();

        List<int> outputs = RunForkProgram(asm.Build(), 2048, 512);

        Assert.Contains(10, outputs); // child kept the fork-time snapshot
        Assert.Contains(99, outputs); // parent saw its own private write
    }
}
