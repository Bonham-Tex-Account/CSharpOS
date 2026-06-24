using CSharpOS;

namespace OSTests;

/// <summary>
/// Phase A of the OS-as-process work: I/O (IN/OUT) traps into an ISA kernel in the
/// process's kernel section; HLT and invalid opcodes are atomic privileged
/// terminations. Covers the trap mechanism, IRET, privilege gating, preemption,
/// and end-to-end syscalls through BasicOS's kernel image.
/// </summary>
public class SyscallTests : IDisposable
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

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    // Lays out a single process directly on the hardware (no OS), so the kernel
    // section / stacks are positioned for the trap-mechanism tests.
    private static Hardware HardwareWithLayout(IOperatingSystem os, int programAddress, int programSize)
    {
        Hardware hw = Test.NewHardware(1024, os);
        Process process = new Process("ignored", 64, 64);
        process.ProgramAddress = programAddress;
        process.ProgramSize = programSize;
        hw.LoadProcessLayout(process);
        return hw;
    }

    // ---- Privilege gating -------------------------------------------------

    [Fact]
    public void Out_InUserMode_TrapsWithoutProducingOutput()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 0, 4);
        bool fired = false;
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { fired = true; };
        hw.WriteRegisterAt(0, 42);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.OUT, 0, 0, 0));

        hw.Run();

        Assert.False(fired);
        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
    }

    [Fact]
    public void In_InUserMode_Traps()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 0, 4);
        hw.RaiseInputInterrupt(5); // buffered, but a user-mode IN must trap, not read it
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.IN, 0, 0, 0));

        hw.Run();

        // Trapped into the kernel (the kernel handler does the real read); the
        // user-mode IN did not consume input or block.
        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        Assert.Equal(0, os.BlockCount);
    }

    [Fact]
    public void GetProgramBase_IsLevelAware()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 100, 8);

        hw.SetPrivilegeLevel(PrivilegeLevel.User);
        Assert.Equal(100, hw.GetProgramBase());                 // user program
        hw.SetPrivilegeLevel(PrivilegeLevel.Kernel);
        Assert.Equal(108, hw.GetProgramBase());                 // kernel section (after program)
        hw.SetPrivilegeLevel(PrivilegeLevel.Privileged);
        Assert.Equal(0, hw.GetProgramBase());                   // privileged OS code => absolute
    }

    // ---- Trap entry / IRET mechanism -------------------------------------

    [Fact]
    public void Out_InUserMode_SavesStateAndEntersKernel()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 0, 4);
        int section = 4; // ProgramAddress + ProgramSize
        hw.WriteRegisterAt(0, 99); // EAX is the operand of OUT EAX
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.OUT, 0, 0, 0));

        hw.Run(); // Run advances IP to 4 before OUT executes

        Assert.Equal(PrivilegeLevel.Kernel, hw.GetPrivilegeLevel());
        Assert.Equal(section + Hardware.KernelHeaderSize, hw.GetInstructionPointer());
        // trap-info: opcode, operand byte-offset (EAX => 0), return IP (after OUT => 4)
        Assert.Equal(Instruction.OUT, hw.ReadBytes(section + Hardware.KernelTrapInfoOffset)[0]);
        Assert.Equal(0, ReadWord(hw, section + Hardware.KernelTrapInfoOffset + 4));
        Assert.Equal(4, ReadWord(hw, section + Hardware.KernelTrapInfoOffset + 8));
        // user EAX saved into the kernel-section save area
        Assert.Equal(99, ReadWord(hw, section + Hardware.KernelSaveAreaOffset));
        // running on the process's kernel stack
        int kernelStackTop = section + Hardware.KernelHeaderSize + 64 + 64 + Hardware.KernelStackSize;
        Assert.Equal(kernelStackTop, hw.ReadRegister(RegisterName.ESP));
    }

    [Fact]
    public void Out_InUserMode_DoesNotTickQuantum()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 0, 4);
        hw.SetInstructionPointer(0);
        hw.WriteBytes(0, Test.Word(Instruction.OUT, 0, 0, 0));

        hw.Run();

        // The trapping instruction is not reported executed nor counted.
        Assert.Equal(0, os.ContextSwitchCount);
    }

    [Fact]
    public void Iret_RestoresRegistersLevelAndReturnIp()
    {
        FakeOS os = new FakeOS();
        Hardware hw = HardwareWithLayout(os, 0, 4);
        int section = 4;
        hw.WriteRegisterAt(0, 7);      // user EAX
        hw.SetInstructionPointer(40);  // the user instruction to resume at
        hw.EnterKernel(Instruction.IN, 0);

        // Kernel writes an IN result into the operand's save-area slot (EAX => 0).
        hw.WriteBytes(section + Hardware.KernelSaveAreaOffset, new byte[] { 55, 0, 0, 0 });

        hw.Iret();

        Assert.Equal(PrivilegeLevel.User, hw.GetPrivilegeLevel());
        Assert.Equal(40, hw.GetInstructionPointer());
        Assert.Equal(55, hw.ReadRegisterAt(0)); // delivered from the save area
    }

    // ---- Preemption ------------------------------------------------------

    // Quantum preemption of kernel-mode code is exercised end-to-end by
    // TwoProcesses_EachUseIoSyscalls_AcrossKernelPreemption (the kernel I/O handler
    // is longer than one quantum, so it is preempted and resumed).

    // ---- End-to-end through BasicOS's kernel image -----------------------

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    private static List<int> RunToCompletion(BasicOS os, Hardware hw, int stepCap)
    {
        List<int> outputs = new List<int>();
        // Model an instant output device: signal completion as soon as it delivers.
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        int steps = 0;
        while (os.HasProcesses && steps < stepCap)
        {
            hw.Run();
            steps++;
        }
        return outputs;
    }

    // Runs a fixed number of ticks (each Run drains interrupts, schedules if idle,
    // and may execute one instruction), stopping early once all processes finish.
    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    [Fact]
    public void OutSyscall_PrintsValueThroughKernel_ThenProcessExits()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(7)), 128, 64));

        List<int> outputs = RunToCompletion(os, hw, 2000);

        Assert.Equal(new List<int> { 7 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void InSyscall_DeliversInputIntoUserRegister()
    {
        // IN EAX; OUT EAX; HLT — the printed value proves IN delivered the input.
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        hw.RaiseInputInterrupt(55); // input available before the read, so it doesn't block
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        List<int> outputs = RunToCompletion(os, hw, 2000);

        Assert.Equal(new List<int> { 55 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void OutSyscall_WithNonEaxRegister_PrintsTheRightValue()
    {
        // OUT EDX exercises the kernel reading the operand index from trap-info.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EDX, 123);
        asm.Out(RegisterName.EDX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        List<int> outputs = RunToCompletion(os, hw, 2000);

        Assert.Equal(new List<int> { 123 }, outputs);
    }

    [Fact]
    public void Hlt_TerminatesTheProcess()
    {
        Assembler asm = new Assembler();
        asm.Hlt();

        StringWriter log = new StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunToCompletion(os, hw, 2000);

        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void InvalidInstruction_TerminatesProcessAndFiresEvent()
    {
        // 0xFF is not a known opcode; it faults and terminates the process.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        InvalidInstructionArgs? captured = null;
        hw.InvalidInstruction += (object? sender, InvalidInstructionArgs e) => { captured = e; };
        os.LoadProcess(new Process(CreateProgramFile(new byte[] { 0xFF, 0, 0, 0 }), 128, 64));

        RunToCompletion(os, hw, 2000);

        Assert.NotNull(captured);
        Assert.Equal(0xFF, captured!.Opcode);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void TwoProcesses_EachUseIoSyscalls_AcrossKernelPreemption()
    {
        // Each process prints a distinct value then halts. The kernel I/O handler
        // is longer than one quantum, so handlers are preempted and resumed; both
        // values must still print correctly.
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(16384, Test.AllRegisters(), os);
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(100)), 128, 64));
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(200)), 128, 64));

        List<int> outputs = RunToCompletion(os, hw, 5000);

        Assert.Contains(100, outputs);
        Assert.Contains(200, outputs);
        Assert.False(os.HasProcesses);
    }

    // ---- Blocking I/O (Phase B) -----------------------------------------

    [Fact]
    public void Input_BlocksUntilInterrupt_ThenDelivers()
    {
        // IN EAX; OUT EAX; HLT — with no input, the process blocks; an input
        // interrupt wakes it and the IN re-runs and delivers the value.
        Assembler asm = new Assembler();
        asm.In(RegisterName.EAX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 8000);

        Assert.False(os.HasRunningProcess); // blocked on input → CPU idle
        Assert.Empty(outputs);
        Assert.True(os.HasProcesses);

        hw.RaiseInputInterrupt(42);
        RunSteps(os, hw, 8000);

        Assert.Equal(new List<int> { 42 }, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void BlockedProcess_LetsAnotherRun_ThenWakes()
    {
        // P1 reads input (blocks); P2 prints and halts while P1 waits; then an input
        // interrupt wakes P1, which reads the value, prints it, and finishes.
        Assembler reader = new Assembler();
        reader.In(RegisterName.EAX);
        reader.Out(RegisterName.EAX);
        reader.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(16384, Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); hw.RaiseOutputComplete(); };
        os.LoadProcess(new Process(CreateProgramFile(reader.Build()), 128, 64));   // P1
        os.LoadProcess(new Process(CreateProgramFile(PrintThenHalt(7)), 128, 64)); // P2

        RunSteps(os, hw, 8000);

        Assert.Contains(7, outputs);        // P2 ran to completion while P1 blocked
        Assert.True(os.HasProcesses);       // P1 still around (blocked)
        Assert.False(os.HasRunningProcess); // idle: P1 blocked, P2 gone

        hw.RaiseInputInterrupt(99);
        RunSteps(os, hw, 8000);

        Assert.Contains(99, outputs);       // P1 woke, read 99, printed it
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Output_BlocksWhenDeviceBusy_UntilOutputComplete()
    {
        // Two outputs with no completion between them: the first delivers and marks
        // the device busy; the second blocks until an output-complete interrupt.
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, 1);
        asm.Out(RegisterName.EAX);
        asm.MovImm(RegisterName.EAX, 2);
        asm.Out(RegisterName.EAX);
        asm.Hlt();

        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Test.MinMachineSize, Test.AllRegisters(), os);
        List<int> outputs = new List<int>();
        hw.ProgramOutput += (object? sender, ProgramOutputArgs e) => { outputs.Add(e.Value); }; // no auto-complete
        os.LoadProcess(new Process(CreateProgramFile(asm.Build()), 128, 64));

        RunSteps(os, hw, 8000);

        Assert.Equal(new List<int> { 1 }, outputs); // first delivered, second blocked
        Assert.False(os.HasRunningProcess);

        hw.RaiseOutputComplete();
        RunSteps(os, hw, 8000);

        Assert.Equal(new List<int> { 1, 2 }, outputs);
        Assert.False(os.HasProcesses);
    }
}
