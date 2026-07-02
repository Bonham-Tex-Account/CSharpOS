using CSharpOS;

namespace OSTests;

/// <summary>
/// End-to-end tests that the MMU is the sole memory-protection mechanism (the LOAD/STORE bounds
/// traps and the linear fallback were removed): a user process that touches a virtual page
/// outside its address space is terminated with a protection fault, while in-bounds access works
/// and the scheduler keeps running other processes. Also covers the load-time size guard.
/// </summary>
public class MemoryProtectionTests
{
    private static int Memory => Test.MachineWithHeap(16384);

    private static List<int> CaptureOutputs(Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            outputs.Add(e.Value);
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    // A program that LOADs from virtual address `va`, then OUTs `mark` and halts. When `va` is
    // out of bounds the LOAD faults and the process is killed before the OUT ever runs.
    private static byte[] LoadThenPrint(int va, int mark)
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EBX, va);
        asm.Load(RegisterName.EAX, RegisterName.EBX);   // faults here if `va` is unmapped
        asm.MovImm(RegisterName.EAX, mark);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // A program that just OUTs `mark` and halts (a well-behaved neighbour).
    private static byte[] PrintOnly(int mark)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, mark);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static (BasicOS os, Hardware hw, List<int> outputs) NewMachine()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        return (os, hw, CaptureOutputs(hw));
    }

    private static void Run(BasicOS os, Hardware hw)
    {
        for (int i = 0; i < 20000 && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    [Fact]
    public void OutOfRangePage_TerminatesProcess_WithoutReachingTheOut()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();
        bool terminated = false;
        hw.ProcessTerminated += (object? sender, ProcessTerminatedArgs e) => terminated = true;

        // 40000 / 256 = page 156 > MaxPagesPerProcess (128): beyond the page table entirely.
        os.LoadProcess(new Process(hw.Disk.Store(LoadThenPrint(40000, 99)), 256, 64));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.True(terminated);
        Assert.DoesNotContain(99, outputs);   // killed at the LOAD, never reached the OUT
    }

    [Fact]
    public void UnmappedPageBeyondExtent_TerminatesProcess()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        // A small process: program + 256 memory + 64 stack is well under one page-table's worth,
        // so virtual address 20000 (page 78, within the table but past this process's extent) is
        // unmapped → protection fault.
        os.LoadProcess(new Process(hw.Disk.Store(LoadThenPrint(20000, 77)), 256, 64));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.DoesNotContain(77, outputs);
    }

    [Fact]
    public void InBoundsLoad_Works()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        // Virtual address 300 lands in the process's own data region (program is tiny, memory is
        // 512): a legitimate access that faults in and translates, so the OUT runs.
        os.LoadProcess(new Process(hw.Disk.Store(LoadThenPrint(300, 55)), 512, 128));
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains(55, outputs);
    }

    [Fact]
    public void MachineKeepsSchedulingOthers_AfterAProtectionFault()
    {
        (BasicOS os, Hardware hw, List<int> outputs) = NewMachine();

        os.LoadProcess(new Process(hw.Disk.Store(LoadThenPrint(40000, 99)), 256, 64));  // faults
        os.LoadProcess(new Process(hw.Disk.Store(PrintOnly(7)), 256, 64));              // well-behaved
        Run(os, hw);

        Assert.False(os.HasProcesses);
        Assert.Contains(7, outputs);          // the neighbour still ran to completion
        Assert.DoesNotContain(99, outputs);   // the faulting process was killed
    }

    [Fact]
    public void OversizedProcess_IsRejectedAtLoad()
    {
        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = new Hardware(Test.MachineWithHeap(70000), Test.AllRegisters(), os);

        int maxUserExtent = OsLayout.MaxPagesPerProcess * OsLayout.PageSize;   // 32 KiB
        // RequiredMemory alone exceeds the MMU's per-process coverage.
        os.LoadProcess(new Process(hw.Disk.Store(PrintOnly(1)), maxUserExtent + 4096, 64));

        Assert.False(os.HasProcesses);                       // never loaded
        Assert.Contains("exceeds addressable memory", log.ToString());
    }
}
