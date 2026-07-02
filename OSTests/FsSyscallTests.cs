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

    // A program that FSYS-opens "/f" (create), writes "hi" to it, closes, reopens, reads the
    // two chars back, and OUTs each. Proves the full write/read syscall path — user-pointer
    // translation of the buffer, atomic dispatch, and the resume-with-result idiom — through a
    // live scheduler. The fd is kept in EBX across syscalls (FSYS restores the captured user
    // registers, overriding only EAX with the result), so no user-space scratch is needed.
    // All data lives in the first page (< PageSize), which is RAM-home program image, so the
    // kernel's absolute buffer write and the user's later MMU read resolve to the same bytes.
    private static byte[] WriteThenReadRoundTrip()
    {
        const int PathOff = 128;   // "/f"
        const int DataOff = 160;   // "hi" source for the write
        const int ReadOff = 192;   // destination the read fills

        Assembler asm = new Assembler();
        // open("/f", create) → fd in EAX
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.MovImm16(RegisterName.EBX, PathOff);
        asm.MovImm(RegisterName.ECX, Hardware.FsysCreateFlag);
        asm.Fsys();
        asm.Mov(RegisterName.EBX, RegisterName.EAX);        // fd → EBX (survives later FSYS calls)
        // write("hi", 2)
        asm.MovImm16(RegisterName.ECX, DataOff);
        asm.MovImm(RegisterName.EDX, 2);
        asm.MovImm(RegisterName.EAX, Hardware.FsysWrite);
        asm.Fsys();                                         // EBX restored to fd afterward
        // close(fd)
        asm.MovImm(RegisterName.EAX, Hardware.FsysClose);
        asm.Fsys();
        // reopen("/f") → fd (offset resets to 0)
        asm.MovImm(RegisterName.EAX, Hardware.FsysOpen);
        asm.MovImm16(RegisterName.EBX, PathOff);
        asm.MovImm(RegisterName.ECX, 0);
        asm.Fsys();
        asm.Mov(RegisterName.EBX, RegisterName.EAX);        // new fd → EBX
        // read(2) into ReadOff
        asm.MovImm16(RegisterName.ECX, ReadOff);
        asm.MovImm(RegisterName.EDX, 2);
        asm.MovImm(RegisterName.EAX, Hardware.FsysRead);
        asm.Fsys();
        // OUT the two chars read back
        asm.MovImm16(RegisterName.EDX, ReadOff);
        asm.Load(RegisterName.EAX, RegisterName.EDX);
        asm.Out(RegisterName.EAX);
        asm.MovImm16(RegisterName.EDX, ReadOff + 4);
        asm.Load(RegisterName.EAX, RegisterName.EDX);
        asm.Out(RegisterName.EAX);
        asm.Hlt();
        byte[] code = asm.Build();

        byte[] image = new byte[ReadOff + 2 * 4];
        Array.Copy(code, image, code.Length);
        image[PathOff] = (byte)'/';
        image[PathOff + 4] = (byte)'f';
        // PathOff + 8 stays 0 → null terminator
        image[DataOff] = (byte)'h';
        image[DataOff + 4] = (byte)'i';
        return image;
    }

    [Fact]
    public void Fsys_WriteThenRead_FromAUserProcess_RoundTripsData()
    {
        BasicOS os = new BasicOS(new StringWriter());
        Hardware hw = new Hardware(Memory, Test.AllRegisters(), os);
        List<int> outputs = CaptureOutputs(hw);

        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, Hardware.FsOpFormat);

        int slot = hw.Disk.Store(WriteThenReadRoundTrip());
        os.LoadProcess(new Process(slot, 512, 64));

        for (int i = 0; i < 40000 && os.HasProcesses; i++)
        {
            hw.Run();
        }

        Assert.False(os.HasProcesses);
        Assert.Equal(new List<int> { 'h', 'i' }, outputs);
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
