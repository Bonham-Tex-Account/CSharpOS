namespace CSharpOS;

/// <summary>
/// Tiny assembler for the CSharpOS instruction set. Supports labels for jumps
/// and a data section appended after the code. All addresses are program-relative
/// (the CPU adds the program base at execution time); pass an origin to Build to
/// shift label offsets when the code is loaded at a fixed offset (e.g. the kernel
/// section, whose code begins after a reserved header).
/// </summary>
public sealed partial class Assembler
{
    // ---- private fields --------------------------------------------------
    private readonly List<byte> code = new List<byte>();
    private readonly Dictionary<string, int> labels = new Dictionary<string, int>();
    private readonly List<Fixup> fixups = new List<Fixup>();
    private readonly List<string> dataLabels = new List<string>();

    // ---- accessor methods ------------------------------------------------

    // Current length of the emitted code in bytes. Lets a caller record where the
    // next routine will start (before applying Build's origin) when packing several
    // routines into one image.
    public int CodeLength { get { return code.Count; } }

    public void Label(string name)
    {
        labels[name] = code.Count;
    }

    // ---- helper functions ------------------------------------------------
    private void Emit(byte opcode, byte b1, byte b2, byte b3)
    {
        code.Add(opcode);
        code.Add(b1);
        code.Add(b2);
        code.Add(b3);
    }

    private void AddJump(byte opcode, string label)
    {
        int position = code.Count;
        Emit(opcode, 0, 0, 0);
        fixups.Add(new Fixup { Position = position, Label = label, Imm8 = false });
    }

    // ---- integral functions (instruction emission) -----------------------
    public void Mov(RegisterName dest, RegisterName src)
    {
        Emit(Instruction.MOV_REG_REG, (byte)dest, (byte)src, 0);
    }

    public void MovImm(RegisterName dest, int immediate)
    {
        Emit(Instruction.MOV_REG_IMM, (byte)dest, (byte)immediate, 0);
    }

    // 16-bit immediate (high byte, low byte) for values that exceed 8 bits.
    public void MovImm16(RegisterName dest, int immediate)
    {
        Emit(Instruction.MOV_REG_IMM16, (byte)dest, (byte)((immediate >> 8) & 0xFF), (byte)(immediate & 0xFF));
    }

    // Loads the resolved program-relative offset of a label as an 8-bit immediate.
    public void MovImmLabel(RegisterName dest, string label)
    {
        int position = code.Count;
        Emit(Instruction.MOV_REG_IMM, (byte)dest, 0, 0);
        fixups.Add(new Fixup { Position = position, Label = label, Imm8 = true });
    }

    public void Load(RegisterName dest, RegisterName pointer)
    {
        Emit(Instruction.LOAD, (byte)dest, (byte)pointer, 0);
    }

    public void Store(RegisterName pointer, RegisterName src)
    {
        Emit(Instruction.STORE, (byte)pointer, (byte)src, 0);
    }

    public void Add(RegisterName dest, RegisterName src)
    {
        Emit(Instruction.ADD, (byte)dest, (byte)src, 0);
    }

    public void Sub(RegisterName dest, RegisterName src)
    {
        Emit(Instruction.SUB, (byte)dest, (byte)src, 0);
    }

    public void Mul(RegisterName dest, RegisterName src)
    {
        Emit(Instruction.MUL, (byte)dest, (byte)src, 0);
    }

    public void Div(RegisterName dest, RegisterName src)
    {
        Emit(Instruction.DIV, (byte)dest, (byte)src, 0);
    }

    public void Cmp(RegisterName a, RegisterName b)
    {
        Emit(Instruction.CMP, (byte)a, (byte)b, 0);
    }

    public void Inc(RegisterName reg)
    {
        Emit(Instruction.INC, (byte)reg, 0, 0);
    }

    public void Dec(RegisterName reg)
    {
        Emit(Instruction.DEC, (byte)reg, 0, 0);
    }

    public void Jmp(string label) { AddJump(Instruction.JMP, label); }
    public void Jz(string label)  { AddJump(Instruction.JZ,  label); }
    public void Jnz(string label) { AddJump(Instruction.JNZ, label); }
    public void Js(string label)  { AddJump(Instruction.JS,  label); }
    public void Jns(string label) { AddJump(Instruction.JNS, label); }
    public void Call(string label) { AddJump(Instruction.CALL, label); }

    public void Ret()
    {
        Emit(Instruction.RET, 0, 0, 0);
    }

    public void Out(RegisterName reg)
    {
        Emit(Instruction.OUT, (byte)reg, 0, 0);
    }

    public void In(RegisterName reg)
    {
        Emit(Instruction.IN, (byte)reg, 0, 0);
    }

    public void Hlt()
    {
        Emit(Instruction.HLT, 0, 0, 0);
    }

    public void Iret()
    {
        Emit(Instruction.IRET, 0, 0, 0);
    }

    // Privileged OS-support instructions: save/restore a process's full register
    // file, refresh the hardware layout from an entry, and return to a process.
    public void SaveRegs(RegisterName pointer)
    {
        Emit(Instruction.SAVEREGS, (byte)pointer, 0, 0);
    }

    public void LoadRegs(RegisterName pointer)
    {
        Emit(Instruction.LOADREGS, (byte)pointer, 0, 0);
    }

    public void SetLayout(RegisterName pointer)
    {
        Emit(Instruction.SETLAYOUT, (byte)pointer, 0, 0);
    }

    public void OsRet(RegisterName levelRegister)
    {
        Emit(Instruction.OSRET, (byte)levelRegister, 0, 0);
    }

    // Reserves a 4-byte zero-initialized slot in the data section.
    public void DataInt(string name)
    {
        dataLabels.Add(name);
    }

    // origin is added to every resolved label offset, so code assembled here can
    // be loaded at a non-zero offset and still self-reference correctly.
    public byte[] Build(int origin = 0)
    {
        List<byte> output = new List<byte>(code);
        Dictionary<string, int> resolvedLabels = new Dictionary<string, int>(labels);

        foreach (string name in dataLabels)
        {
            resolvedLabels[name] = output.Count;
            output.Add(0);
            output.Add(0);
            output.Add(0);
            output.Add(0);
        }

        foreach (Fixup fixup in fixups)
        {
            if (!resolvedLabels.TryGetValue(fixup.Label, out int position))
            {
                throw new InvalidOperationException($"Undefined label: {fixup.Label}");
            }

            int target = origin + position;

            if (fixup.Imm8)
            {
                if (target > 255)
                {
                    throw new InvalidOperationException($"Label '{fixup.Label}' offset {target} does not fit in an 8-bit immediate.");
                }
                output[fixup.Position + 2] = (byte)target;
            }
            else
            {
                output[fixup.Position + 1] = (byte)((target >> 8) & 0xFF);
                output[fixup.Position + 2] = (byte)(target & 0xFF);
            }
        }

        return output.ToArray();
    }
}
