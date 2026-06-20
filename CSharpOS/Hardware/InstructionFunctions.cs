namespace CSharpOS;

internal static class InstructionFunctions
{
    private const int ZeroFlag = 1;
    private const int SignFlag = 2;

    internal static void MovRegReg(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, hw.ReadRegisterAt(b2));
    }

    internal static void MovRegImm(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, (int)b2);
    }

    // LOAD dest, [ptr]  — dest = 32-bit value at (programBase + reg[ptr])
    internal static void Load(Hardware hw, byte b1, byte b2, byte b3)
    {
        int address = hw.GetProgramBase() + hw.ReadRegisterAt(b2);
        byte[] bytes = hw.ReadBytes(address);
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        hw.WriteRegisterAt(b1, value);
    }

    // STORE [ptr], src  — (programBase + reg[ptr]) = reg[src]
    internal static void Store(Hardware hw, byte b1, byte b2, byte b3)
    {
        int address = hw.GetProgramBase() + hw.ReadRegisterAt(b1);
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User && !hw.IsAddressInProcessRanges(address))
        {
            hw.TrapInvalidInstruction(Instruction.STORE, b1, b2, b3);
            return;
        }
        int value = hw.ReadRegisterAt(b2);
        hw.WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
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

    internal static void Jmp(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
    }

    internal static void Jz(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & ZeroFlag) != 0)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Jnz(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & ZeroFlag) == 0)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Js(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & SignFlag) != 0)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Jns(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & SignFlag) == 0)
        {
            hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
        }
    }

    internal static void Call(Hardware hw, byte b1, byte b2, byte b3)
    {
        int returnAddress = hw.GetInstructionPointer();
        int esp = hw.ReadRegister(RegisterName.ESP) - 4;
        hw.WriteBytes(esp, new byte[]
        {
            (byte)(returnAddress & 0xFF),
            (byte)((returnAddress >> 8) & 0xFF),
            (byte)((returnAddress >> 16) & 0xFF),
            (byte)((returnAddress >> 24) & 0xFF)
        });
        hw.WriteRegister(RegisterName.ESP, esp);
        hw.SetInstructionPointer(hw.GetProgramBase() + ((b1 << 8) | b2));
    }

    internal static void Ret(Hardware hw, byte b1, byte b2, byte b3)
    {
        int esp = hw.ReadRegister(RegisterName.ESP);
        byte[] bytes = hw.ReadBytes(esp);
        int returnAddress = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        hw.WriteRegister(RegisterName.ESP, esp + 4);
        hw.SetInstructionPointer(returnAddress);
    }

    // OUT is privileged: in user mode it traps into the kernel (which performs the
    // real device write in kernel mode); the operand byte-offset locates the user's
    // value in the saved register file.
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
        if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
        {
            hw.TrapInvalidInstruction(Instruction.IRET, b1, b2, b3);
            return;
        }
        hw.Iret();
    }

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
}
