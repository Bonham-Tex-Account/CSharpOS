namespace CSharpOS;

/// <summary>
/// How an instruction's three operand bytes (b1/b2/b3) are laid out, so the assembler
/// text parser (`/bin/as`, §4.2) knows how to read each line's operands and pack them.
/// One shape per instruction; the packing is fixed by the ISA (see the opcode table).
/// </summary>
public enum OperandShape
{
    None = 0,       // no operands:            0, 0, 0     (RET, HLT, IRET, FSYS, FORK, SIGRETURN)
    Reg = 1,        // one register:           b1=reg      (INC, OUT, EXEC, REAP, …)
    RegReg = 2,     // two registers:          b1,b2=reg   (ADD, LOAD, CMP, KILL, OUTS, …)
    RegRegReg = 3,  // three registers:        b1,b2,b3=reg (DREAD, DWRITE)
    RegImm8 = 4,    // register + 8-bit imm:   b1=reg, b2=imm8
    RegImm16 = 5,   // register + 16-bit imm:  b1=reg, b2=hi, b3=lo
    Addr16 = 6,     // 16-bit address/label:   b1=hi, b2=lo (JMP/JZ/JNZ/JS/JNS/CALL)

    // MOV is overloaded in the text language: `MOV r, r2` | `MOV r, imm`. The assembler picks the
    // concrete opcode from operand 2 — a register name → MOV_REG_REG; a decimal that fits 8 bits →
    // MOV_REG_IMM; a larger decimal → MOV_REG_IMM16. The table stores the MOV_REG_REG opcode as the
    // base; the parser substitutes MovImm8Opcode / MovImm16Opcode when operand 2 is an immediate.
    Mov = 7,
}

/// <summary>
/// The assembler's ground-truth tables: the text mnemonic → (opcode, operand shape) mapping and the
/// register name → index mapping. This is the single source of truth shared by (a) the host-side
/// tests and (b) the ISA-side `/bin/as` program, which embeds the serialized word-per-char image
/// produced by <see cref="BuildMnemonicTableImage"/> / <see cref="BuildRegisterTableImage"/> and
/// scans it with the same fixed-width record layout described by the constants below.
///
/// The serialized layout is deliberately fixed-width (constant record stride) so the ISA scanner
/// advances by a compile-time constant instead of parsing variable-length records. Every field is a
/// 4-byte little-endian word; strings are word-per-char (the OUTS/INS/file convention) and
/// null-terminated within a padded fixed field. A record whose first name word is 0 terminates the
/// table.
/// </summary>
public static class AsmTable
{
    // ---- serialized-record layout (words; ×4 for byte offsets) -----------

    // Mnemonic record: [name: MnemonicNameWords][opcode: 1][shape: 1].
    public const int MnemonicMaxChars = 9;                              // longest: SIGACTION/SIGRETURN/SETLAYOUT
    public const int MnemonicNameWords = MnemonicMaxChars + 1;          // + null terminator
    public const int MnemonicOpcodeWord = MnemonicNameWords;            // word index of the opcode field
    public const int MnemonicShapeWord = MnemonicNameWords + 1;         // word index of the shape field
    public const int MnemonicRecordWords = MnemonicNameWords + 2;       // total words per record

    // Register record: [name: RegisterNameWords][index: 1].
    public const int RegisterMaxChars = 6;                             // longest: EFLAGS
    public const int RegisterNameWords = RegisterMaxChars + 1;         // + null terminator
    public const int RegisterIndexWord = RegisterNameWords;            // word index of the register-index field
    public const int RegisterRecordWords = RegisterNameWords + 1;      // total words per record

    // Returned by a lookup that finds no match (host helpers return null; the constant mirrors the
    // sentinel the ISA scanner will deliver).
    public const int NotFound = -1;

    // The concrete opcodes the parser substitutes when a `MOV`'s second operand is an immediate
    // (see OperandShape.Mov). The table's MOV entry carries the register-to-register opcode as base.
    public const byte MovImm8Opcode = Instruction.MOV_REG_IMM;
    public const byte MovImm16Opcode = Instruction.MOV_REG_IMM16;

    /// <summary>One text mnemonic and how it encodes.</summary>
    public readonly struct MnemonicEntry
    {
        public string Mnemonic { get; }
        public byte Opcode { get; }
        public OperandShape Shape { get; }

        public MnemonicEntry(string mnemonic, byte opcode, OperandShape shape)
        {
            Mnemonic = mnemonic;
            Opcode = opcode;
            Shape = shape;
        }
    }

    // ---- the tables ------------------------------------------------------

    // Every user-writable instruction, in opcode order. MOV_REG_IMM and MOV_REG_IMM16 have no entry
    // of their own: the single `MOV` mnemonic (shape Mov) reaches all three MOV opcodes.
    private static readonly MnemonicEntry[] mnemonics = new MnemonicEntry[]
    {
        new MnemonicEntry("MOV",       Instruction.MOV_REG_REG,   OperandShape.Mov),
        new MnemonicEntry("LOAD",      Instruction.LOAD,          OperandShape.RegReg),
        new MnemonicEntry("STORE",     Instruction.STORE,         OperandShape.RegReg),
        new MnemonicEntry("ADD",       Instruction.ADD,           OperandShape.RegReg),
        new MnemonicEntry("SUB",       Instruction.SUB,           OperandShape.RegReg),
        new MnemonicEntry("MUL",       Instruction.MUL,           OperandShape.RegReg),
        new MnemonicEntry("DIV",       Instruction.DIV,           OperandShape.RegReg),
        new MnemonicEntry("CMP",       Instruction.CMP,           OperandShape.RegReg),
        new MnemonicEntry("INC",       Instruction.INC,           OperandShape.Reg),
        new MnemonicEntry("DEC",       Instruction.DEC,           OperandShape.Reg),
        new MnemonicEntry("AND",       Instruction.AND,           OperandShape.RegReg),
        new MnemonicEntry("OR",        Instruction.OR,            OperandShape.RegReg),
        new MnemonicEntry("XOR",       Instruction.XOR,           OperandShape.RegReg),
        new MnemonicEntry("NOT",       Instruction.NOT,           OperandShape.Reg),
        new MnemonicEntry("SHL",       Instruction.SHL,           OperandShape.RegReg),
        new MnemonicEntry("SHR",       Instruction.SHR,           OperandShape.RegReg),
        new MnemonicEntry("JMP",       Instruction.JMP,           OperandShape.Addr16),
        new MnemonicEntry("JZ",        Instruction.JZ,            OperandShape.Addr16),
        new MnemonicEntry("JNZ",       Instruction.JNZ,           OperandShape.Addr16),
        new MnemonicEntry("CALL",      Instruction.CALL,          OperandShape.Addr16),
        new MnemonicEntry("RET",       Instruction.RET,           OperandShape.None),
        new MnemonicEntry("JS",        Instruction.JS,            OperandShape.Addr16),
        new MnemonicEntry("JNS",       Instruction.JNS,           OperandShape.Addr16),
        new MnemonicEntry("OUT",       Instruction.OUT,           OperandShape.Reg),
        new MnemonicEntry("IN",        Instruction.IN,            OperandShape.Reg),
        new MnemonicEntry("HLT",       Instruction.HLT,           OperandShape.None),
        new MnemonicEntry("IRET",      Instruction.IRET,          OperandShape.None),
        new MnemonicEntry("FORK",      Instruction.FORK,          OperandShape.None),
        new MnemonicEntry("EXEC",      Instruction.EXEC,          OperandShape.Reg),
        new MnemonicEntry("WAIT",      Instruction.WAIT,          OperandShape.Reg),
        new MnemonicEntry("EXIT",      Instruction.EXIT,          OperandShape.Reg),
        new MnemonicEntry("SETFOCUS",  Instruction.SETFOCUS,      OperandShape.Reg),
        new MnemonicEntry("KILL",      Instruction.KILL,          OperandShape.RegReg),
        new MnemonicEntry("REAP",      Instruction.REAP,          OperandShape.Reg),
        new MnemonicEntry("SIGACTION", Instruction.SIGACTION,     OperandShape.RegReg),
        new MnemonicEntry("SIGRETURN", Instruction.SIGRETURN,     OperandShape.None),
        new MnemonicEntry("SAVEREGS",  Instruction.SAVEREGS,      OperandShape.Reg),
        new MnemonicEntry("LOADREGS",  Instruction.LOADREGS,      OperandShape.Reg),
        new MnemonicEntry("SETLAYOUT", Instruction.SETLAYOUT,     OperandShape.Reg),
        new MnemonicEntry("OSRET",     Instruction.OSRET,         OperandShape.Reg),
        new MnemonicEntry("DREAD",     Instruction.DREAD,         OperandShape.RegRegReg),
        new MnemonicEntry("DWRITE",    Instruction.DWRITE,        OperandShape.RegRegReg),
        new MnemonicEntry("DLEN",      Instruction.DLEN,          OperandShape.RegReg),
        new MnemonicEntry("OUTS",      Instruction.OUTS,          OperandShape.RegReg),
        new MnemonicEntry("INS",       Instruction.INS,           OperandShape.RegReg),
        new MnemonicEntry("INK",       Instruction.INK,           OperandShape.Reg),
        new MnemonicEntry("INPOLL",    Instruction.INPOLL,        OperandShape.Reg),
        new MnemonicEntry("FBREAD",    Instruction.FBREAD,        OperandShape.RegReg),
        new MnemonicEntry("FBWRITE",   Instruction.FBWRITE,       OperandShape.RegReg),
        new MnemonicEntry("FSYS",      Instruction.FSYS,          OperandShape.None),
    };

    /// <summary>The mnemonic table, in opcode order.</summary>
    public static IReadOnlyList<MnemonicEntry> Mnemonics
    {
        get { return mnemonics; }
    }

    /// <summary>Register names in index order (EAX=0 … R15=23), taken from the RegisterName enum.</summary>
    public static IReadOnlyList<string> RegisterNames
    {
        get { return registerNames; }
    }

    private static readonly string[] registerNames = BuildRegisterNames();

    private static string[] BuildRegisterNames()
    {
        RegisterName[] values = Enum.GetValues<RegisterName>();
        string[] names = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            // The enum's underlying value is the register file index; the array is index-ordered.
            names[(int)values[i]] = values[i].ToString();
        }
        return names;
    }

    // ---- host-side lookups (mirror what the ISA scanner will do) ---------

    /// <summary>
    /// Resolves a text mnemonic (canonical UPPERCASE) to its opcode and operand shape.
    /// Returns false for an unknown mnemonic.
    /// </summary>
    public static bool TryLookupMnemonic(string mnemonic, out byte opcode, out OperandShape shape)
    {
        for (int i = 0; i < mnemonics.Length; i++)
        {
            if (mnemonics[i].Mnemonic == mnemonic)
            {
                opcode = mnemonics[i].Opcode;
                shape = mnemonics[i].Shape;
                return true;
            }
        }
        opcode = 0;
        shape = OperandShape.None;
        return false;
    }

    /// <summary>
    /// Resolves a register name (canonical UPPERCASE, e.g. "EAX", "R8") to its register-file index.
    /// Returns <see cref="NotFound"/> for an unknown name.
    /// </summary>
    public static int LookupRegister(string name)
    {
        for (int i = 0; i < registerNames.Length; i++)
        {
            if (registerNames[i] == name)
            {
                return i;
            }
        }
        return NotFound;
    }

    // ---- serialization to the ISA word-per-char image --------------------

    /// <summary>
    /// Serializes the mnemonic table to the fixed-width word-per-char image the ISA `/bin/as` scans:
    /// one <see cref="MnemonicRecordWords"/>-word record per mnemonic (name / opcode / shape),
    /// followed by an all-zero terminator record.
    /// </summary>
    public static byte[] BuildMnemonicTableImage()
    {
        int recordBytes = MnemonicRecordWords * 4;
        byte[] image = new byte[(mnemonics.Length + 1) * recordBytes]; // trailing record left zeroed = terminator
        for (int i = 0; i < mnemonics.Length; i++)
        {
            int baseOffset = i * recordBytes;
            WriteName(image, baseOffset, mnemonics[i].Mnemonic, MnemonicMaxChars);
            WriteWord(image, baseOffset + MnemonicOpcodeWord * 4, mnemonics[i].Opcode);
            WriteWord(image, baseOffset + MnemonicShapeWord * 4, (int)mnemonics[i].Shape);
        }
        return image;
    }

    /// <summary>
    /// Serializes the register table to the fixed-width word-per-char image: one
    /// <see cref="RegisterRecordWords"/>-word record per register (name / index), then an all-zero
    /// terminator record.
    /// </summary>
    public static byte[] BuildRegisterTableImage()
    {
        int recordBytes = RegisterRecordWords * 4;
        byte[] image = new byte[(registerNames.Length + 1) * recordBytes];
        for (int i = 0; i < registerNames.Length; i++)
        {
            int baseOffset = i * recordBytes;
            WriteName(image, baseOffset, registerNames[i], RegisterMaxChars);
            WriteWord(image, baseOffset + RegisterIndexWord * 4, i);
        }
        return image;
    }

    // Writes a name word-per-char into a zeroed fixed field (one char per 4-byte word). The field is
    // already zero, so the char after the last one is the null terminator and the remainder is pad.
    private static void WriteName(byte[] image, int byteOffset, string name, int maxChars)
    {
        if (name.Length > maxChars)
        {
            throw new InvalidOperationException($"Name '{name}' exceeds {maxChars} chars for the assembler table.");
        }
        for (int i = 0; i < name.Length; i++)
        {
            WriteWord(image, byteOffset + i * 4, name[i]);
        }
    }

    private static void WriteWord(byte[] image, int byteOffset, int value)
    {
        image[byteOffset]     = (byte)(value & 0xFF);
        image[byteOffset + 1] = (byte)((value >> 8) & 0xFF);
        image[byteOffset + 2] = (byte)((value >> 16) & 0xFF);
        image[byteOffset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
