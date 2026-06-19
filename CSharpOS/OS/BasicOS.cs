namespace CSharpOS;

public class BasicOS : OperatingSystem
{
    public BasicOS(TextWriter log) : base(new List<Trap>(), log)
    {
    }

    // The syscall library: I/O handlers copied into each process's kernel section.
    // Built once, assembled with origin = KernelHeaderSize so its labels are
    // section-relative (the code is loaded just past the reserved header).
    private static readonly byte[] kernelImage = BuildKernelImage();

    public override byte[] KernelImage => kernelImage;

    // On entry the hardware has saved the user register file at section offset 0
    // and written trap-info at KernelTrapInfoOffset (faulting opcode, the operand's
    // byte-offset into the save area, return IP). Program base == kernel section,
    // so the handler reads trap-info / the save area with program-relative LOAD/STORE.
    private static byte[] BuildKernelImage()
    {
        Assembler asm = new Assembler();

        asm.MovImm(RegisterName.EAX, Hardware.KernelTrapInfoOffset);     // -> faulting opcode
        asm.Load(RegisterName.EBX, RegisterName.EAX);                    // EBX = opcode
        asm.MovImm(RegisterName.EAX, Hardware.KernelTrapInfoOffset + 4); // -> operand byte-offset
        asm.Load(RegisterName.ECX, RegisterName.EAX);                    // ECX = save-area slot offset

        asm.MovImm(RegisterName.EDX, Instruction.OUT);
        asm.Cmp(RegisterName.EBX, RegisterName.EDX);
        asm.Jz("do_out");
        asm.MovImm(RegisterName.EDX, Instruction.IN);
        asm.Cmp(RegisterName.EBX, RegisterName.EDX);
        asm.Jz("do_in");
        asm.Iret();                                  // unknown cause — return (should not happen)

        asm.Label("do_out");
        asm.Load(RegisterName.ESI, RegisterName.ECX);  // ESI = user's operand value (save area)
        asm.Out(RegisterName.ESI);                     // real device write (kernel level)
        asm.Iret();

        asm.Label("do_in");
        asm.In(RegisterName.ESI);                      // real device read (kernel level)
        asm.Store(RegisterName.ECX, RegisterName.ESI); // write result back into the save-area slot
        asm.Iret();

        return asm.Build(Hardware.KernelHeaderSize);
    }
}
