namespace CSharpOS;

/// <summary>
/// The instruction set: the opcode byte constants and the dispatch table that maps
/// each opcode to its implementation in <see cref="InstructionFunctions"/>. Every
/// instruction is a 4-byte word (opcode + three operand bytes).
/// </summary>
public static class Instruction
{
    // ---- public constants (opcodes) --------------------------------------
    public const byte MOV_REG_REG = 0x01;
    public const byte MOV_REG_IMM = 0x02;
    // 16-bit immediate (b2 = high byte, b3 = low byte) for addresses/offsets that
    // exceed the 8-bit range of MOV_REG_IMM. Used heavily by OS ISA code, whose
    // memory offsets (process table, data section) run well past 255.
    public const byte MOV_REG_IMM16 = 0x03;
    public const byte LOAD        = 0x05;
    public const byte STORE       = 0x06;
    public const byte ADD         = 0x10;
    public const byte SUB         = 0x11;
    public const byte MUL         = 0x12;
    public const byte DIV         = 0x13;
    public const byte CMP         = 0x14;
    public const byte INC         = 0x15;
    public const byte DEC         = 0x16;
    public const byte AND         = 0x17;
    public const byte OR          = 0x18;
    public const byte XOR         = 0x19;
    public const byte NOT         = 0x1A;
    public const byte SHL         = 0x1B;
    public const byte SHR         = 0x1C;
    public const byte JMP         = 0x20;
    public const byte JZ          = 0x21;
    public const byte JNZ         = 0x22;
    public const byte CALL        = 0x23;
    public const byte RET         = 0x24;
    public const byte JS          = 0x25;
    public const byte JNS         = 0x26;
    public const byte OUT         = 0x30;
    public const byte IN          = 0x31;
    public const byte HLT         = 0x32;
    public const byte IRET        = 0x33;

    // ---- process control (spawning) ---------------------------------------
    // User-mode instructions that trap into a privileged OS routine (like HLT) to
    // create/replace/await processes and switch the foreground process.
    public const byte FORK        = 0x34;
    public const byte EXEC        = 0x35;
    public const byte WAIT        = 0x36;
    public const byte EXIT        = 0x37;
    public const byte SETFOCUS    = 0x38;

    // ---- privileged OS-support opcodes ------------------------------------
    // Used by OS ISA code running in Privileged mode to save/restore a process's
    // full register file, refresh the hardware memory layout from a process-table
    // entry, and return to a process at a chosen privilege level.
    public const byte SAVEREGS    = 0x40;
    public const byte LOADREGS    = 0x41;
    public const byte SETLAYOUT   = 0x42;
    public const byte OSRET       = 0x43;

    // ---- privileged disk opcodes ------------------------------------------
    // Block transfers between the disk and RAM, run by the OS load path in
    // Privileged mode (absolute addresses); they trap as invalid in user mode.
    public const byte DREAD       = 0x44;
    public const byte DWRITE      = 0x45;
    // Disk slot content length (used by EXEC to size the new image's allocation).
    public const byte DLEN        = 0x46;

    // ---- private fields --------------------------------------------------
    private static Dictionary<byte, Action<Hardware, byte, byte, byte>> opcodeTable = new();

    // ---- constructor (static initializer) --------------------------------
    static Instruction()
    {
        opcodeTable[MOV_REG_REG] = InstructionFunctions.MovRegReg;
        opcodeTable[MOV_REG_IMM] = InstructionFunctions.MovRegImm;
        opcodeTable[MOV_REG_IMM16] = InstructionFunctions.MovRegImm16;
        opcodeTable[LOAD]        = InstructionFunctions.Load;
        opcodeTable[STORE]       = InstructionFunctions.Store;
        opcodeTable[ADD]         = InstructionFunctions.Add;
        opcodeTable[SUB]         = InstructionFunctions.Sub;
        opcodeTable[MUL]         = InstructionFunctions.Mul;
        opcodeTable[DIV]         = InstructionFunctions.Div;
        opcodeTable[CMP]         = InstructionFunctions.Cmp;
        opcodeTable[INC]         = InstructionFunctions.Inc;
        opcodeTable[DEC]         = InstructionFunctions.Dec;
        opcodeTable[AND]         = InstructionFunctions.And;
        opcodeTable[OR]          = InstructionFunctions.Or;
        opcodeTable[XOR]         = InstructionFunctions.Xor;
        opcodeTable[NOT]         = InstructionFunctions.Not;
        opcodeTable[SHL]         = InstructionFunctions.Shl;
        opcodeTable[SHR]         = InstructionFunctions.Shr;
        opcodeTable[JMP]         = InstructionFunctions.Jmp;
        opcodeTable[JZ]          = InstructionFunctions.Jz;
        opcodeTable[JNZ]         = InstructionFunctions.Jnz;
        opcodeTable[CALL]        = InstructionFunctions.Call;
        opcodeTable[RET]         = InstructionFunctions.Ret;
        opcodeTable[JS]          = InstructionFunctions.Js;
        opcodeTable[JNS]         = InstructionFunctions.Jns;
        opcodeTable[OUT]         = InstructionFunctions.Out;
        opcodeTable[IN]          = InstructionFunctions.In;
        opcodeTable[HLT]         = InstructionFunctions.Hlt;
        opcodeTable[IRET]        = InstructionFunctions.Iret;
        opcodeTable[SAVEREGS]    = InstructionFunctions.SaveRegs;
        opcodeTable[LOADREGS]    = InstructionFunctions.LoadRegs;
        opcodeTable[SETLAYOUT]   = InstructionFunctions.SetLayout;
        opcodeTable[OSRET]       = InstructionFunctions.OsRet;
        opcodeTable[DREAD]       = InstructionFunctions.DRead;
        opcodeTable[DWRITE]      = InstructionFunctions.DWrite;
        opcodeTable[DLEN]        = InstructionFunctions.DLen;
        opcodeTable[FORK]        = InstructionFunctions.Fork;
        opcodeTable[EXEC]        = InstructionFunctions.Exec;
        opcodeTable[WAIT]        = InstructionFunctions.Wait;
        opcodeTable[EXIT]        = InstructionFunctions.Exit;
        opcodeTable[SETFOCUS]    = InstructionFunctions.SetFocus;
    }

    // ---- integral functions ----------------------------------------------

    /// <summary>
    /// Fetches and runs the instruction at <paramref name="address"/>: evaluates any
    /// OS traps for the opcode, then dispatches to its handler.
    /// </summary>
    /// <returns>True if a handler ran; false if the opcode trapped (OS trap or invalid opcode).</returns>
    public static bool Execute(int address, Hardware hw)
    {
        byte[] bytes = hw.ReadBytes(address);
        byte opcode = bytes[0];
        byte b1 = bytes[1];
        byte b2 = bytes[2];
        byte b3 = bytes[3];

        // Record the address so a conditional-branch handler can index the predictor's BHT.
        hw.SetCurrentInstructionAddress(address);

        if (hw.EvaluateTraps(opcode, b1, b2, b3))
        {
            return false;
        }

        if (opcodeTable.TryGetValue(opcode, out Action<Hardware, byte, byte, byte>? handler))
        {
            handler(hw, b1, b2, b3);
            return true;
        }
        else
        {
            hw.TrapInvalidInstruction(opcode, b1, b2, b3);
            return false;
        }
    }
}
