namespace CSharpOS;

internal static class InstructionFunctions
{
    // ---- private constants -----------------------------------------------
    private const int ZeroFlag = 1;
    private const int SignFlag = 2;

    // Shift instructions operate on 32-bit registers, so only the low 5 bits of the
    // shift count are significant (a shift of 32+ is undefined / wraps); this masks
    // the count to the 0..31 range, matching x86 shift-count semantics.
    private const int ShiftCountMask = 0x1F;

    // ---- helper functions ------------------------------------------------
    private static void UpdateFlags(Hardware hw, int result)
    {
        int flags = hw.ReadRegister(RegisterName.EFLAGS);

        if (result == 0)
        {
            flags |= ZeroFlag;
        }
        else
        {
            flags &= ~ZeroFlag;
        }

        if (result < 0)
        {
            flags |= SignFlag;
        }
        else
        {
            flags &= ~SignFlag;
        }

        hw.WriteRegister(RegisterName.EFLAGS, flags);
    }

    // ---- integral functions (instruction implementations) ----------------

    internal static void MovRegReg(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, hw.ReadRegisterAt(b2));
    }

    internal static void MovRegImm(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, (int)b2);
    }

    // MOV reg, imm16  — b2 is the high byte, b3 the low byte.
    internal static void MovRegImm16(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, (b2 << 8) | b3);
    }

    // LOAD dest, [ptr]  — dest = 32-bit value at the (MMU-translated) address reg[ptr].
    // In user mode the pointer is a virtual address translated through the page table; in
    // kernel/OS mode it is absolute (program base 0). Translation is behavior-preserving.
    internal static void Load(Hardware hw, byte b1, byte b2, byte b3)
    {
        int address = hw.TranslateDataAddress(hw.ReadRegisterAt(b2));
        byte[] bytes = hw.ReadBytes(address);
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        hw.WriteRegisterAt(b1, value);
    }

    // STORE [ptr], src  — (MMU-translated reg[ptr]) = reg[src]
    internal static void Store(Hardware hw, byte b1, byte b2, byte b3)
    {
        int address = hw.TranslateDataAddress(hw.ReadRegisterAt(b1));
        int value = hw.ReadRegisterAt(b2);
        hw.WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8)  & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }

    internal static void Add(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) + hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Sub(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) - hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Mul(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) * hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Div(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) / hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    // CMP a, b  — sets flags from (a - b) without modifying either register.
    internal static void Cmp(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) - hw.ReadRegisterAt(b2);
        UpdateFlags(hw, result);
    }

    internal static void Inc(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) + 1;
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Dec(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) - 1;
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void And(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) & hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Or(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) | hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Xor(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) ^ hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    // NOT dest — bitwise complement. Flags updated on the result.
    internal static void Not(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = ~hw.ReadRegisterAt(b1);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    // SHL dest, src — logical shift left; shift amount taken from src register.
    internal static void Shl(Hardware hw, byte b1, byte b2, byte b3)
    {
        int shift = hw.ReadRegisterAt(b2) & ShiftCountMask;
        int result = hw.ReadRegisterAt(b1) << shift;
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    // SHR dest, src — logical shift right; shift amount taken from src register.
    internal static void Shr(Hardware hw, byte b1, byte b2, byte b3)
    {
        int shift = hw.ReadRegisterAt(b2) & ShiftCountMask;
        int result = (int)((uint)hw.ReadRegisterAt(b1) >> shift);
        hw.WriteRegisterAt(b1, result);
        UpdateFlags(hw, result);
    }

    internal static void Jmp(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
    }

    // The conditional jumps compute `taken` first, let the branch predictor score it
    // (observational only), then jump exactly as before when taken — control flow is
    // unchanged whether or not the predictor is watching.
    internal static void Jz(Hardware hw, byte b1, byte b2, byte b3)
    {
        bool taken = (hw.ReadRegister(RegisterName.EFLAGS) & ZeroFlag) != 0;
        hw.RecordBranch(taken);
        if (taken)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Jnz(Hardware hw, byte b1, byte b2, byte b3)
    {
        bool taken = (hw.ReadRegister(RegisterName.EFLAGS) & ZeroFlag) == 0;
        hw.RecordBranch(taken);
        if (taken)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Js(Hardware hw, byte b1, byte b2, byte b3)
    {
        bool taken = (hw.ReadRegister(RegisterName.EFLAGS) & SignFlag) != 0;
        hw.RecordBranch(taken);
        if (taken)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Jns(Hardware hw, byte b1, byte b2, byte b3)
    {
        bool taken = (hw.ReadRegister(RegisterName.EFLAGS) & SignFlag) == 0;
        hw.RecordBranch(taken);
        if (taken)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    // Position-independent: ESP holds an offset from the program base, and the pushed
    // return address is stored as a base-relative offset too. So a forked child (a pure
    // memory copy at a different base) returns into its own code, not the parent's.
    internal static void Call(Hardware hw, byte b1, byte b2, byte b3)
    {
        int programBase = hw.GetProgramBase();
        int returnOffset = hw.GetInstructionPointer() - programBase;
        int esp = hw.ReadRegister(RegisterName.ESP) - 4;
        // The stack slot is addressed virtually (translated); the jump target stays a
        // program-relative code address (code is not paged in Phase 1).
        hw.WriteBytes(hw.TranslateDataAddress(esp), new byte[]
        {
            (byte)(returnOffset & 0xFF),
            (byte)((returnOffset >> 8)  & 0xFF),
            (byte)((returnOffset >> 16) & 0xFF),
            (byte)((returnOffset >> 24) & 0xFF)
        });
        hw.WriteRegister(RegisterName.ESP, esp);
        hw.SetInstructionPointer(programBase + ((b1 << 8) | b2));
    }

    internal static void Ret(Hardware hw, byte b1, byte b2, byte b3)
    {
        int programBase = hw.GetProgramBase();
        int esp = hw.ReadRegister(RegisterName.ESP);
        byte[] bytes = hw.ReadBytes(hw.TranslateDataAddress(esp));
        int returnOffset = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        hw.WriteRegister(RegisterName.ESP, esp + 4);
        hw.SetInstructionPointer(programBase + returnOffset);
    }

    // OUT is privileged: in user mode it traps into the kernel (which performs
    // the real device write); the operand byte-offset locates the value in the
    // saved register file.
    internal static void Out(Hardware hw, byte b1, byte b2, byte b3)
    {
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.EnterKernel(Instruction.OUT, b1 * 4);
            return;
        }
        hw.KernelOutput(hw.ReadRegisterAt(b1));
    }

    // IN is privileged: user mode traps; the kernel performs the real read and
    // writes the result into the operand's saved-register slot for IRET to deliver.
    internal static void In(Hardware hw, byte b1, byte b2, byte b3)
    {
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.EnterKernel(Instruction.IN, b1 * 4);
            return;
        }
        hw.KernelInput(b1);
    }

    internal static void Hlt(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Halt();
    }

    internal static void Iret(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Iret();
    }

    // SAVEREGS [ptr]  — saves the full register file (with the live IP folded into
    // the EIP slot) to the absolute address in reg[b1]. Privileged-only.
    internal static void SaveRegs(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SaveRegistersTo(hw.ReadRegisterAt(b1));
    }

    // LOADREGS [ptr]  — restores the full register file from the absolute address
    // in reg[b1] and sets the IP from the restored EIP slot. Privileged-only.
    internal static void LoadRegs(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.LoadRegistersFrom(hw.ReadRegisterAt(b1));
    }

    // SETLAYOUT [ptr]  — refreshes the hardware process layout from the process
    // table entry at the absolute address in reg[b1]. Privileged-only.
    internal static void SetLayout(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SetLayoutFromEntry(hw.ReadRegisterAt(b1));
    }

    // OSRET reg  — sets the privilege level to reg[b1]; execution resumes at the
    // IP that LOADREGS restored. The OS's "return to a process" primitive.
    internal static void OsRet(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.OsReturn(hw.ReadRegisterAt(b1));
    }

    // DREAD dest, slot, lenOut — copies disk slot reg[slot] into RAM at the absolute
    // address reg[dest], writing the byte count into reg[lenOut]. The disk is an
    // OS/kernel boundary, so this traps as invalid if executed in user mode.
    internal static void DRead(Hardware hw, byte b1, byte b2, byte b3)
    {
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.TrapInvalidInstruction(Instruction.DREAD, b1, b2, b3);
            return;
        }
        int destAddress = hw.ReadRegisterAt(b1);
        int slot = hw.ReadRegisterAt(b2);
        int length = hw.DiskRead(destAddress, slot);
        hw.WriteRegisterAt(b3, length);
    }

    // DWRITE slot, src, len — copies reg[len] bytes of RAM from the absolute address
    // reg[src] into disk slot reg[slot]. Privileged-only (traps in user mode).
    internal static void DWrite(Hardware hw, byte b1, byte b2, byte b3)
    {
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.TrapInvalidInstruction(Instruction.DWRITE, b1, b2, b3);
            return;
        }
        int slot = hw.ReadRegisterAt(b1);
        int srcAddress = hw.ReadRegisterAt(b2);
        int length = hw.ReadRegisterAt(b3);
        hw.DiskWrite(slot, srcAddress, length);
    }

    // DLEN slot, lenOut — reg[lenOut] = byte length of disk slot reg[slot]. Privileged
    // (the OS uses it to size an EXEC image's allocation); traps in user mode.
    internal static void DLen(Hardware hw, byte b1, byte b2, byte b3)
    {
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.TrapInvalidInstruction(Instruction.DLEN, b1, b2, b3);
            return;
        }
        int slot = hw.ReadRegisterAt(b1);
        hw.WriteRegisterAt(b2, hw.DiskLength(slot));
    }

    // FORK — duplicate the running process; traps into the privileged OS fork routine,
    // which delivers the child's PID to the parent (in EAX) and 0 to the child.
    internal static void Fork(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Fork();
    }

    // EXEC r — replace the running process's image with the program in disk slot reg[r];
    // traps into the privileged OS exec routine (the PID is preserved).
    internal static void Exec(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Exec(hw.ReadRegisterAt(b1));
    }

    // WAIT r — block until child PID reg[r] terminates, then deliver its exit status in
    // reg[0] (EAX); returns immediately if the child is already a zombie.
    internal static void Wait(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Wait(hw.ReadRegisterAt(b1));
    }

    // EXIT r — terminate the running process with status reg[r] (HLT is status 0).
    internal static void Exit(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.Exit(hw.ReadRegisterAt(b1));
    }

    // SETFOCUS r — make the process with PID reg[r] the foreground process (the live
    // keyboard + screen follow it). Used by a shell to focus the child it launches.
    internal static void SetFocus(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SetFocus(hw.ReadRegisterAt(b1));
    }
}
