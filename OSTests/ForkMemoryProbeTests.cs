using CSharpOS;

namespace OSTests;

/// <summary>
/// Regression tests that FORK propagates a memory write the parent made just before forking — for
/// BOTH a user STORE and a kernel-mediated INS write. The INS case is the important one: kernel
/// writes to a user page go through `ensure_user_page`, which used to leave the frame CLEAN, so
/// fork's `flush_frames` (and frame eviction, which both only write back DIRTY frames) silently
/// dropped the write — the child (and any post-eviction reader) saw stale/zero data. `ensure_user_page`
/// now marks the frame dirty on write, mirroring `StampFrame` for STORE. This is what makes the
/// fork-based shell work (parent types a line, child inherits it and execs).
/// </summary>
public class ForkMemoryProbeTests
{
    private static int Memory => Test.MachineWithHeap(16384);

    private static List<int> CaptureOutputs(Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) =>
        {
            if (e.StringValue == null) { outputs.Add(e.Value); }
            hw.RaiseOutputComplete(e.Device);
        };
        return outputs;
    }

    // STORE 77 to a DATA-region address, FORK, then BOTH parent and child LOAD it and OUT it.
    private static byte[] StoreForkReadProgram(int va)
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EBX, va);
        asm.MovImm(RegisterName.EAX, 77);
        asm.Store(RegisterName.EBX, RegisterName.EAX);   // mem[va] = 77
        asm.Fork();
        asm.MovImm(RegisterName.ECX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.ECX);
        asm.Jnz("parent");
        // child: read va and OUT it, then halt.
        asm.MovImm16(RegisterName.EBX, va);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                       // child's view of mem[va]
        asm.Hlt();
        asm.Label("parent");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);     // child pid
        asm.MovImm16(RegisterName.EBX, va);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Add(RegisterName.EAX, RegisterName.EAX);     // parent's view * 2 (154) to distinguish
        asm.Out(RegisterName.EAX);
        asm.Wait(RegisterName.ESI);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Fork_PropagatesAPreForkDataWrite_ToTheChild()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(hw.Disk.Store(StoreForkReadProgram(512)), 2048, 128));
        for (int i = 0; i < 60000 && os.HasProcesses; i++) { hw.Run(); }

        // Parent should OUT 154 (77*2); child should OUT 77 if the write propagated.
        Assert.True(outputs.Contains(154), $"parent view missing; outputs=[{string.Join(",", outputs)}]");
        Assert.True(outputs.Contains(77), $"child did NOT see the pre-fork write; outputs=[{string.Join(",", outputs)}]");
    }

    // Parent INS's a line into a buffer, FORKs, child LOADs the buffer's first word and OUTs it.
    private static byte[] InsForkReadProgram(int va)
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, va);
        asm.MovImm(RegisterName.ECX, 8);
        asm.Ins(RegisterName.EAX, RegisterName.ECX);     // buffer[0..] = typed line (word-per-char)
        asm.Fork();
        asm.MovImm(RegisterName.ECX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.ECX);
        asm.Jnz("parent");
        asm.MovImm16(RegisterName.EBX, va);
        asm.Load(RegisterName.EAX, RegisterName.EBX);
        asm.Out(RegisterName.EAX);                       // child's view of buffer[0]
        asm.Hlt();
        asm.Label("parent");
        asm.Mov(RegisterName.ESI, RegisterName.EAX);
        asm.Wait(RegisterName.ESI);
        asm.Hlt();
        return asm.Build();
    }

    [Fact]
    public void Fork_PropagatesAPreForkInsWrite_ToTheChild()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        os.LoadProcess(new Process(hw.Disk.Store(InsForkReadProgram(512)), 2048, 128));
        hw.SetActiveProcess(0);
        for (int i = 0; i < 2000; i++) { hw.Run(); }     // reach INS
        hw.RaiseStringInputInterrupt("A");               // 'A' = 65
        for (int i = 0; i < 60000 && os.HasProcesses; i++) { hw.Run(); }

        Assert.True(outputs.Contains(65), $"child did NOT see the parent's INS write; outputs=[{string.Join(",", outputs)}]");
    }
}
