using CSharpOS;

namespace OSTests;

/// <summary>
/// End-to-end test for the FSYS instruction and its IvtFsSyscall routine: a real user
/// process executes FSYS(OPEN) and receives the resulting fd back in EAX, proving the
/// wrapper's user-pointer translation, the atomic dispatch, and the SAVEREGS/deliver/resume
/// path all work through a live scheduler (not just the directly-dispatched cores).
/// </summary>
public class FsSyscallTests
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

    // A program that FSYS-opens (with create) a path stored at offset 64 in its own image,
    // then OUTs the returned fd and halts.
    private static byte[] OpenThenPrintFd(string path)
    {
        Assembler asm = new Assembler();
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.MovImm16(RegisterName.EBX, 64);                 // user pointer to the path (offset in image)
        asm.MovImm(RegisterName.ECX, Hardware.FsysCreateFlag);
        asm.Fsys();
        asm.Out(RegisterName.EAX);                          // report the fd
        asm.Hlt();
        byte[] code = asm.Build();

        byte[] image = new byte[64 + (path.Length + 1) * 4];
        Array.Copy(code, image, code.Length);               // 64-byte code/pad region
        for (int i = 0; i < path.Length; i++)
        {
            image[64 + i * 4] = (byte)path[i];              // word-per-char path at offset 64
        }
        return image;
    }

    [Fact]
    public void Fsys_Open_FromAUserProcess_ReturnsAnFdInEax()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        // Format the filesystem (fresh disk) before the process runs.
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, Hardware.FsOpFormat);

        int slot = hw.Disk.Store(OpenThenPrintFd("/f"));
        os.LoadProcess(new Process(slot, 256, 64));

        for (int i = 0; i < 20000 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.False(os.HasProcesses);
        Assert.Contains(2, outputs);   // OPEN returned fd 2, delivered in EAX, then OUT
    }
}
