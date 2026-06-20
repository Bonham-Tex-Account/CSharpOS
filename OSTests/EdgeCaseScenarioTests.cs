using CSharpOS;

namespace OSTests;

/// <summary>
/// Edge-case and untested-scenario coverage across the recent additions (traps,
/// IRET, privilege levels, interrupts, blocking/scheduling) plus older instruction
/// and memory-layout corners. These assert intended/correct behavior; some are
/// expected to fail and surface real gaps (privilege holes, layout overflow,
/// missing memory protection) — that is the point.
/// </summary>
public class EdgeCaseScenarioTests : IDisposable
{
    private readonly List<string> tempFiles = new List<string>();

    private string CreateProgramFile(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "csostest_" + Guid.NewGuid().ToString("N") + ".bin");
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

    // ---- helpers ----------------------------------------------------------

    private static byte[] Print(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static byte[] ReadInto(RegisterName reg)
    {
        Assembler asm = new Assembler();
        asm.In(reg);
        asm.Out(reg);
        asm.Hlt();
        return asm.Build();
    }

    // Subscribes an output collector (modeling an instant output device) and boots.
    private static List<int> BootCollecting(BasicOS os, Hardware hw)
    {
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        os.ContextSwitch(hw);
        return outputs;
    }

    private static void Step(BasicOS os, Hardware hw, int n)
    {
        for (int i = 0; i < n && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    // ---- process state defaults ------------------------------------------

    [Fact]
    public void NewProcess_DefaultsToReadyWithNoWaitReason()
    {
        Process process = new Process("p.bin", 16, 16);
        Assert.Equal(ProcessState.Ready, process.State);
        Assert.Equal(WaitReason.None, process.WaitReason);
    }

    // ---- privilege holes (expected to surface problems) ------------------

    [Fact]
    public void Iret_InUserMode_ShouldFault()
    {
        // IRET returns from a kernel syscall; a user-mode process executing it should
        // be rejected (privilege fault), not allowed to silently restore state and
        // jump. This checks whether IRET is privilege-gated.
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = 0;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.IRET, 0, 0, 0));

        hw.Run();

        Assert.True(os.InvalidInstructionCalled, "user-mode IRET should be treated as an illegal instruction");
    }

    [Fact]
    public void UserProcess_WritingOutsideItsMemory_ShouldBeTrapped()
    {
        // No memory protection is enforced: a user STORE can scribble anywhere. A
        // write outside the process's own ranges should be trapped. (Documents the
        // missing protection — expected to fail.)
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", 16, 16);
        process.ProgramAddress = 0;
        process.ProgramSize = 4;
        hw.LoadProcessLayout(process);
        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        hw.WriteRegisterAt(1, 900); // pointer well outside this process's footprint
        hw.WriteRegisterAt(0, 1234);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.STORE, 1, 0, 0));

        hw.Run();

        Assert.True(os.InvalidInstructionCalled, "an out-of-bounds user write should be trapped");
    }

    // ---- syscall transparency --------------------------------------------

    [Fact]
    public void OutSyscall_PreservesRegistersTheKernelUsesAsScratch()
    {
        // The kernel I/O handler clobbers EBX/ECX/EDX/ESI as scratch; a syscall must
        // be transparent to the user's registers. EBX=11, EDX=33 should survive.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EBX, 11);
        asm.MovImm(RegisterName.EDX, 33);
        asm.MovImm(RegisterName.EAX, 7);
        asm.Out(RegisterName.EAX); // syscall
        asm.Out(RegisterName.EBX); // should still be 11
        asm.Out(RegisterName.EDX); // should still be 33
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 2000);

        Assert.Equal(new List<int> { 7, 11, 33 }, outputs);
    }

    // ---- interrupts & buffering ------------------------------------------

    [Fact]
    public void BufferedInputs_AreDeliveredFifo()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.RaiseInputInterrupt(10);
        hw.RaiseInputInterrupt(20);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.IN, 0, 0, 0)); // IN EAX
        hw.WriteBytes(4, Test.Word(Instruction.IN, 1, 0, 0)); // IN EBX

        hw.Run(); // drains both interrupts, then IN EAX
        hw.Run(); // IN EBX

        Assert.Equal(10, hw.ReadRegisterAt(0));
        Assert.Equal(20, hw.ReadRegisterAt(1));
    }

    [Fact]
    public void RaiseOutputComplete_WithNothingBlocked_IsHarmless()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.RaiseOutputComplete();
        hw.RaiseOutputComplete();
        hw.Run(); // drains the spurious completions without error
        Assert.Equal(2, os.WakeCount);
        Assert.Equal(WaitReason.Output, os.LastWakeReason);
    }

    [Fact]
    public void InputArrivingBeforeRead_IsBufferedAndConsumedWithoutBlocking()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        hw.RaiseInputInterrupt(13); // arrives before the process even runs
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 2000);

        Assert.Equal(new List<int> { 13 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void OutputComplete_DoesNotWakeAProcessBlockedOnInput()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 200); // blocks on input

        Assert.False(os.HasRunningProcess);

        hw.RaiseOutputComplete(); // wrong device — must not wake the input waiter
        Step(os, hw, 200);

        Assert.Empty(outputs);
        Assert.True(os.HasProcesses);

        hw.RaiseInputInterrupt(5);
        Step(os, hw, 200);

        Assert.Equal(new List<int> { 5 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void OneInputInterrupt_WakesExactlyOneOfTwoWaiters()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(8192, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 400); // both block on input

        Assert.False(os.HasRunningProcess);

        hw.RaiseInputInterrupt(7);
        Step(os, hw, 400);

        Assert.Single(outputs);             // exactly one waiter woke and finished
        Assert.True(os.HasProcesses);       // the other is still blocked

        hw.RaiseInputInterrupt(8);
        Step(os, hw, 400);

        Assert.Equal(2, outputs.Count);
        Assert.False(os.HasProcesses);
    }

    // ---- scheduling / idle -----------------------------------------------

    [Fact]
    public void AllProcessesBlocked_CpuIdles_AndExecutesNothing()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        int executed = 0;
        hw.InstructionExecuted += (object? sender, InstructionExecutedArgs e) => { executed++; };
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64));
        os.ContextSwitch(hw);
        Step(os, hw, 200); // runs until it blocks on input

        Assert.False(os.HasRunningProcess);
        int afterBlock = executed;

        Step(os, hw, 50); // idle ticks — nothing should execute

        Assert.Equal(afterBlock, executed);
    }

    [Fact]
    public void BlockedProcess_IsSkipped_OthersRunToCompletion()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(8192, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(Print(1)), 128, 64));               // P1 printer
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EAX)), 128, 64)); // P2 reader (blocks)
        os.LoadProcess(new Process(CreateProgramFile(Print(3)), 128, 64));               // P3 printer
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 600);

        // The two printers complete while the reader is blocked (round-robin skips it).
        Assert.Contains(1, outputs);
        Assert.Contains(3, outputs);
        Assert.True(os.HasProcesses);

        hw.RaiseInputInterrupt(2);
        Step(os, hw, 400);

        Assert.Contains(2, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Input_IsDeliveredToTheRequestedNonEaxRegister()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(ReadInto(RegisterName.EDX)), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 200);

        hw.RaiseInputInterrupt(88);
        Step(os, hw, 200);

        Assert.Equal(new List<int> { 88 }, outputs);
    }

    [Fact]
    public void SequentialInputSyscalls_ReadValuesInOrder()
    {
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        hw.RaiseInputInterrupt(5);
        hw.RaiseInputInterrupt(6);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));
        List<int> outputs = BootCollecting(os, hw);
        Step(os, hw, 2000);

        Assert.Equal(new List<int> { 5, 6 }, outputs);
    }

    // ---- kernel-mode faults ----------------------------------------------

    [Fact]
    public void InvalidOpcodeInKernelMode_TerminatesWithoutReEnteringKernel()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(1024, os);
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(0xFE, 0, 0, 0)); // not a real opcode

        hw.Run();

        Assert.True(os.InvalidInstructionCalled);
        Assert.Equal(0, os.BlockCount);                     // did not block / re-trap
        Assert.Equal(PrivilegeLevel.Privileged, hw.GetPrivilegeLevel());
    }

    // ---- memory-layout flaw (expected to surface a problem) --------------

    [Fact]
    public void LoadedProcess_MemoryRegion_MustHoldTheRegisterStateBlock()
    {
        // The register state (a full register file) is saved at the start of the
        // process's memory region. If RequiredMemory is smaller than the register
        // file, the saved state overflows into the user stack and corrupts it.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(4096, Test.AllRegisters(), os);
        Process process = new Process(CreateProgramFile(new byte[] { 0, 0, 0, 0 }), 16, 16);
        os.LoadProcess(process);
        os.ContextSwitch(hw);

        int registerFileSize = Test.AllRegisters().Length * 4;
        Assert.True(process.RequiredMemory >= registerFileSize,
            "RequiredMemory must be large enough to hold the saved register state");
    }

    // ---- arithmetic edges ------------------------------------------------

    [Fact]
    public void Div_IntMinByNegativeOne_Throws()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, int.MinValue);
        hw.WriteRegisterAt(1, -1);
        hw.WriteBytes(0, Test.Word(Instruction.DIV, 0, 1, 0));
        Assert.Throws<OverflowException>(() => Instruction.Execute(0, hw));
    }

    [Fact]
    public void Inc_AtMaxValue_WrapsToMinValueAndSetsSign()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, int.MaxValue);
        hw.WriteBytes(0, Test.Word(Instruction.INC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MinValue, hw.ReadRegisterAt(0));
        Assert.Equal(2, hw.ReadRegister(RegisterName.EFLAGS) & 2); // sign flag set
        Assert.Equal(0, hw.ReadRegister(RegisterName.EFLAGS) & 1); // zero flag clear
    }

    [Fact]
    public void Dec_AtMinValue_WrapsToMaxValueAndClearsSign()
    {
        FakeOS os = new FakeOS();
        Hardware hw = Test.NewHardware(512, os);
        hw.WriteRegisterAt(0, int.MinValue);
        hw.WriteBytes(0, Test.Word(Instruction.DEC, 0, 0, 0));
        Instruction.Execute(0, hw);
        Assert.Equal(int.MaxValue, hw.ReadRegisterAt(0));
        Assert.Equal(0, hw.ReadRegister(RegisterName.EFLAGS) & 2); // sign flag clear
    }

    // ---- assembler origin (Phase A kernel support) -----------------------

    [Fact]
    public void Assembler_BuildWithOrigin_ShiftsJumpTargets()
    {
        Assembler asm = new Assembler();
        asm.Jmp("target");      // offset 0
        asm.MovImm(RegisterName.EAX, 1); // offset 4
        asm.Label("target");    // offset 8
        asm.Hlt();
        byte[] code = asm.Build(80);

        // JMP encodes its target as a 16-bit operand (b1<<8 | b2). With origin 80 the
        // label at code position 8 resolves to 88.
        int target = (code[1] << 8) | code[2];
        Assert.Equal(88, target);
    }

    [Fact]
    public void Assembler_BuildWithOrigin_OverflowingEightBitLabel_Throws()
    {
        Assembler asm = new Assembler();
        asm.MovImmLabel(RegisterName.EAX, "data"); // 8-bit immediate label
        asm.DataInt("data");                       // at code position 4
        // origin 252 pushes the label to 256, past the 8-bit immediate range.
        Assert.Throws<InvalidOperationException>(() => asm.Build(252));
    }
}
