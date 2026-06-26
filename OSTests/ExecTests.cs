using CSharpOS;
using Xunit;

namespace OSTests;

/// <summary>
/// End-to-end tests for the EXEC instruction and its privileged ISA routine: a process
/// replaces its own image with another program loaded from a disk slot (freeing the old
/// region, reallocating, and re-loading both the new program and the kernel image),
/// keeping its PID and continuing to run the new code.
/// </summary>
public class ExecTests : IDisposable
{
    private static int Memory => Test.MachineWithHeap(16384);
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

    private static void RunSteps(BasicOS os, Hardware hw, int steps)
    {
        for (int i = 0; i < steps && os.HasProcesses; i++)
        {
            hw.Run();
        }
    }

    private static byte[] PrintThenHalt(int value)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, value);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        return asm.Build();
    }

    // MOV EAX, slot; EXEC EAX  — replace this image with the program in `slot`.
    private static byte[] ExecCaller(int slot)
    {
        Assembler asm = new Assembler();
        asm.MovImm16(RegisterName.EAX, slot);
        asm.Exec(RegisterName.EAX);
        asm.Hlt(); // unreached: EXEC replaces the image
        return asm.Build();
    }

    [Fact]
    public void Exec_ReplacesImage_AndRunsTheNewProgram()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os); // ctor stages kernel image
        List<int> outputs = CaptureOutputs(hw);

        int targetSlot = hw.Disk.Store(PrintThenHalt(77)); // the program to exec into
        int callerSlot = hw.Disk.Store(ExecCaller(targetSlot));
        os.LoadProcess(new Process(callerSlot, 256, 64));

        RunSteps(os, hw, 20000);

        // The caller execed into the target, which printed 77 and halted.
        Assert.Contains(77, outputs);
        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Exec_PreservesTheProcessPid()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        CaptureOutputs(hw);

        int targetSlot = hw.Disk.Store(PrintThenHalt(5));
        int callerSlot = hw.Disk.Store(ExecCaller(targetSlot));
        Process process = new Process(callerSlot, 256, 64);
        os.LoadProcess(process);
        Assert.Equal(1, process.Pid); // first process

        // The slot-0 entry's PID must remain 1 after the exec (identity is preserved).
        for (int i = 0; i < 20000 && os.HasProcesses; i++)
        {
            int pid = Test.ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPid);
            Assert.Equal(1, pid);
            hw.Run();
        }
    }

    [Fact]
    public void ForkThenChildExec_RunsBothParentAndExecedChild()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        int targetSlot = hw.Disk.Store(PrintThenHalt(200)); // what the child execs into

        // Parent: FORK; if child (EAX==0) EXEC(target); else OUT 100; HLT.
        Assembler asm = new Assembler();
        asm.Fork();
        asm.MovImm(RegisterName.EBX, 0);
        asm.Cmp(RegisterName.EAX, RegisterName.EBX);
        asm.Jz("child");
        asm.MovImm(RegisterName.EAX, 100);   // parent
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        asm.Label("child");
        asm.MovImm16(RegisterName.EAX, targetSlot);
        asm.Exec(RegisterName.EAX);          // child becomes the target program
        asm.Hlt();
        int callerSlot = hw.Disk.Store(asm.Build());
        os.LoadProcess(new Process(callerSlot, 256, 64));

        RunSteps(os, hw, 40000);

        Assert.Contains(100, outputs); // parent
        Assert.Contains(200, outputs); // child, after exec
        Assert.False(os.HasProcesses);
    }
}
