using CSharpOS;

namespace OSTests;

/// <summary>
/// End-to-end tests for Phase 3 rectification: kernel-mediated user-memory access (OUTS/INS)
/// now translates through the process's own page table (Hardware.UserToPhysical) instead of a
/// flat ProgramAddress+ptr read. This is a real regression test for the staleness bug: DATA-
/// region pages are swap-backed and never touch the raw ProgramAddress+offset RAM at all — only
/// a resident frame or a swap slot ever holds their true contents — so the old linear math
/// would read whatever garbage/zero happened to sit in that unused RAM region instead of the
/// process's actual data.
/// </summary>
public class KernelUserMemoryTests
{
    private static int Memory => Test.MachineWithHeap(16384);

    // A data-region virtual address: page 2 (512..767), well past a tiny program's code but
    // safely inside RequiredMemory (callers size memory >= 1024 so this stays a DATA page per
    // OsLayout.IsDataPage, and comfortably resident-page-table-mapped).
    private const int DataVa = 512;

    private static (BasicOS os, Hardware hw, List<string?> stringOutputs) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<string?> stringOutputs = new List<string?>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            stringOutputs.Add(e.StringValue);
            hw.RaiseOutputComplete(e.Device);
        };
        return (os, hw, stringOutputs);
    }

    private static void Run(BasicOS os, Hardware hw)
    {
        for (int i = 0; i < 20000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    // STORE 'h','i' to the DATA-region address (faulting the page into a frame, marking it
    // dirty — the true content now lives ONLY in that frame, never in raw ProgramAddress+DataVa
    // RAM), then OUTS from the SAME address and HLT.
    private static byte[] StoreThenOuts()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EBX, DataVa);
        asm.MovImm(RegisterName.EAX, 'h');
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.MovImm16(RegisterName.EBX, DataVa + 4);
        asm.MovImm(RegisterName.EAX, 'i');
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.MovImm16(RegisterName.EAX, DataVa);
        asm.MovImm(RegisterName.ECX, 2);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Outs_FromADataRegionBuffer_ReadsTheJustWrittenValue_NotStaleRam()
    {
        (BasicOS os, Hardware hw, List<string?> stringOutputs) = NewMachine();
        os.LoadProcess(new Process(hw.Disk.Store(StoreThenOuts()), 1024, 128));

        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains("hi", stringOutputs);
    }

    // Touch a handful of OTHER data pages first (forcing the frame pool to cycle, since
    // FrameCount=4 is tiny), so the DataVa page is very likely evicted-and-refilled at least
    // once by the time OUTS reads it — proving the translation is correct after eviction too,
    // not just on the very first fault-in.
    private static byte[] ThrashThenStoreThenOuts()
    {
        Assembler asm = new Assembler();
        for (int p = 0; p < 8; p++)
        {
            int va = 256 * (2 + p); // pages 2..9, all within a large-enough RequiredMemory
            asm.MovImm16(RegisterName.EBX, va);
            asm.MovImm(RegisterName.EAX, 'x');
            asm.Store(RegisterName.EBX, RegisterName.EAX);
        }
        asm.MovImm16(RegisterName.EBX, DataVa);
        asm.MovImm(RegisterName.EAX, 'h');
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.MovImm16(RegisterName.EBX, DataVa + 4);
        asm.MovImm(RegisterName.EAX, 'i');
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        for (int p = 0; p < 8; p++)
        {
            int va = 256 * (10 + p); // pages 10..17: more churn after writing DataVa
            asm.MovImm16(RegisterName.EBX, va);
            asm.MovImm(RegisterName.EAX, 'y');
            asm.Store(RegisterName.EBX, RegisterName.EAX);
        }
        asm.MovImm16(RegisterName.EAX, DataVa);
        asm.MovImm(RegisterName.ECX, 2);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Outs_FromADataRegionBuffer_CorrectAfterFramePoolChurn()
    {
        (BasicOS os, Hardware hw, List<string?> stringOutputs) = NewMachine();
        // RequiredMemory must cover pages 2..17 (18*256 = 4608 bytes of data region).
        os.LoadProcess(new Process(hw.Disk.Store(ThrashThenStoreThenOuts()), 5120, 128));

        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains("hi", stringOutputs);
    }

    // INS writes into the DATA-region buffer, then OUTS reads it back from the same address —
    // proves the write path (isWrite=true, resolving COW if needed) and read path agree.
    private static byte[] InsThenOuts()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, DataVa);
        asm.MovImm(RegisterName.ECX, 8);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm16(RegisterName.EAX, DataVa);
        asm.MovImm(RegisterName.ECX, 8);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Ins_ThenOuts_OnADataRegionBuffer_RoundTrips()
    {
        (BasicOS os, Hardware hw, List<string?> stringOutputs) = NewMachine();
        os.LoadProcess(new Process(hw.Disk.Store(InsThenOuts()), 1024, 128));

        for (int i = 0; i < 5000 && os.HasProcesses && os.HasRunningProcess; i++)
        {
            hw.Run();
        }
        Assert.True(os.HasProcesses);
        Assert.False(os.HasRunningProcess); // blocked on INS

        hw.RaiseStringInputInterrupt("go");
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains("go", stringOutputs);
    }

    // A wildly out-of-range pointer must not crash the emulator (no unhandled
    // IndexOutOfRangeException from a raw memory[address] access) — UserToPhysical fails
    // cleanly and KernelOutputString just produces no output.
    private static byte[] OutsFromGarbagePointer()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, 60000); // page 234, far beyond MaxPagesPerProcess (128)
        asm.MovImm(RegisterName.ECX, 4);
        asm.Outs(RegisterName.EAX, RegisterName.ECX);
        asm.MovImm(RegisterName.EAX, 42);
        asm.Out(RegisterName.EAX); // proves the process survives and keeps running
        asm.Hlt();
        return asm.Build();
    }

    // Parent writes 'A' to the DATA-region address then FORKs (the page becomes COW-shared);
    // both processes then block on IN forever (deterministic — no interrupt-delivery timing
    // needed) so their state is frozen and inspectable. Used by the two IvtEnsureUserPage
    // isolation tests below, which together caught a real bug: page_in clobbers R11 internally,
    // and ensure_user_page re-read R11 (isWrite) right after calling it. The write-focused test
    // alone did NOT catch this (a resolved COW page still reads back the same data either way,
    // and page_in's clobbered leftover happened to be a nonzero frame-base address, which
    // coincidentally also satisfies "isWrite != 0"); the read-focused test does, because the
    // same coincidence makes a READ get mistreated as a WRITE and eagerly resolve a share that
    // should have stayed shared.
    private static byte[] ForkThenBothBlockForever()
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EBX, DataVa);
        asm.MovImm(RegisterName.EAX, 'A');
        asm.Store(RegisterName.EBX, RegisterName.EAX);
        asm.Fork();
        asm.In(RegisterName.EAX); // both parent and child block here forever (device stays empty)
        asm.Hlt();
        return asm.Build();
    }

    private const int DataPage = DataVa / OsLayout.PageSize;

    // Loads ForkThenBothBlockForever, runs it to the deterministic "both processes blocked on
    // IN" state, and returns (childSlot, parentSlot). The child's DataPage PTE starts
    // non-resident + COW-encoded at this point (fork never eagerly faults pages in).
    private static (Hardware hw, int childSlot, int parentSlot) ForkAndFreezeBothBlocked()
    {
        (BasicOS os, Hardware hw, List<string?> _) = NewMachine();
        os.LoadProcess(new Process(hw.Disk.Store(ForkThenBothBlockForever()), 1024, 128));

        // Run a bounded number of ticks — both processes block on IN forever (never delivered),
        // so this settles into "both blocked" well before the bound and then stays that way.
        for (int i = 0; i < 20000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
        Assert.True(os.HasProcesses);
        Assert.False(os.HasRunningProcess);

        // Find the child's process-table slot (the one with a real ParentPid; the parent's is -1).
        int childSlot = -1;
        int parentSlot = -1;
        for (int slot = 0; slot < OsLayout.MaxProcesses; slot++)
        {
            int entry = OsLayout.ProcessEntryAddress(slot);
            int state = Test.ReadWord(hw, entry + Hardware.ProcessEntryState);
            if (state != (int)ProcessState.Blocked)
            {
                continue;
            }
            int parentPid = Test.ReadWord(hw, entry + Hardware.ProcessEntryParentPid);
            if (parentPid >= 0)
            {
                childSlot = slot;
            }
            else
            {
                parentSlot = slot;
            }
        }
        Assert.True(childSlot >= 0 && parentSlot >= 0 && childSlot != parentSlot);

        int pteBefore = Test.ReadWord(hw, OsLayout.PageTableAddress(childSlot) + DataPage * OsLayout.PageTableEntryBytes);
        Assert.True(pteBefore <= OsLayout.NonResidentPage); // non-resident, COW-encoded

        return (hw, childSlot, parentSlot);
    }

    [Fact]
    public void EnsureUserPage_WriteToAResidentCowPage_ResolvesItPrivately()
    {
        (Hardware hw, int childSlot, int parentSlot) = ForkAndFreezeBothBlocked();

        // Drive IvtEnsureUserPage directly against the child, isWrite=1.
        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, childSlot);
        Test.WriteWord(hw, OsLayout.EnsureUserPageIsWrite, 1);
        hw.RunOsRoutineSynchronously(Hardware.IvtEnsureUserPage, DataPage);

        Assert.Equal(0, Test.ReadWord(hw, OsLayout.EnsureUserPageResult));
        int childPteAfter = Test.ReadWord(hw, OsLayout.PageTableAddress(childSlot) + DataPage * OsLayout.PageTableEntryBytes);
        Assert.True(childPteAfter >= 0); // page_in faulted it in (was non-resident going in)
        int childFrame = (childPteAfter - OsLayout.FramePoolBase) / OsLayout.PageSize;
        Assert.Equal(0, Test.ReadWord(hw, OsLayout.FrameTableEntry(childFrame) + OsLayout.FrameCowField)); // resolved: no longer shared

        // The parent's own COW partnership was cleared too (pair_resolve finalises both sides);
        // its PTE may be resident or evicted back to its own private swap slot — either is a
        // correct outcome with only OsLayout.FrameCount shared frames — but it must not be the
        // stale COW encoding (that would mean the parent is still "sharing" a slot nobody owns).
        int parentPte = Test.ReadWord(hw, OsLayout.PageTableAddress(parentSlot) + DataPage * OsLayout.PageTableEntryBytes);
        Assert.False(OsLayout.IsCowPte(parentPte));
    }

    // The discriminating regression case for the R11-clobber bug: page_in uses R11 as scratch
    // while filling a non-resident page, so ensure_user_page must spill/reload isWrite across
    // that call. A READ access (isWrite=0) to a first-touch COW page must fault it in WITHOUT
    // resolving the share — leaving it resident but still COW (FrameCowField=1). Without the
    // spill fix, R11 would hold page_in's leftover scratch value (a nonzero frame base) by the
    // time the write-check runs, so this read would be misread as a write and eagerly (and
    // needlessly) resolve the COW share — which the earlier write-focused test could not catch,
    // since a resolved COW page still reads back the same (correct) data either way.
    [Fact]
    public void EnsureUserPage_ReadOfAFirstTouchCowPage_DoesNotResolveIt()
    {
        (Hardware hw, int childSlot, int _) = ForkAndFreezeBothBlocked();

        Test.WriteWord(hw, OsLayout.CurrentIndexOffset, childSlot);
        Test.WriteWord(hw, OsLayout.EnsureUserPageIsWrite, 0); // read access
        hw.RunOsRoutineSynchronously(Hardware.IvtEnsureUserPage, DataPage);

        Assert.Equal(0, Test.ReadWord(hw, OsLayout.EnsureUserPageResult));
        int childPteAfter = Test.ReadWord(hw, OsLayout.PageTableAddress(childSlot) + DataPage * OsLayout.PageTableEntryBytes);
        Assert.True(childPteAfter >= 0); // faulted in (page_in ran)
        int childFrame = (childPteAfter - OsLayout.FramePoolBase) / OsLayout.PageSize;
        Assert.Equal(1, Test.ReadWord(hw, OsLayout.FrameTableEntry(childFrame) + OsLayout.FrameCowField)); // still shared
    }

    [Fact]
    public void Outs_FromAnOutOfRangePointer_FailsCleanly_ProcessSurvives()
    {
        (BasicOS os, Hardware hw, List<string?> stringOutputs) = NewMachine();
        List<int> intOutputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => intOutputs.Add(e.Value);

        os.LoadProcess(new Process(hw.Disk.Store(OutsFromGarbagePointer()), 1024, 128));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains("", stringOutputs); // the bad-pointer OUTS produced an empty string, not a crash
        Assert.Contains(42, intOutputs);    // process kept running past the bad OUTS
    }
}
