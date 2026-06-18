namespace CSharpOS;

internal static class InstructionFunctions
{
    internal static void MovRegReg(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, hw.ReadRegisterAt(b2));
    }

    internal static void MovRegImm(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.WriteRegisterAt(b1, (int)b2);
    }

    internal static void Add(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) + hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateZeroFlag(hw, result);
    }

    internal static void Sub(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) - hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateZeroFlag(hw, result);
    }

    internal static void Mul(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) * hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateZeroFlag(hw, result);
    }

    internal static void Div(Hardware hw, byte b1, byte b2, byte b3)
    {
        int result = hw.ReadRegisterAt(b1) / hw.ReadRegisterAt(b2);
        hw.WriteRegisterAt(b1, result);
        UpdateZeroFlag(hw, result);
    }

    internal static void Jmp(Hardware hw, byte b1, byte b2, byte b3)
    {
        hw.SetInstructionPointer((b1 << 8) | b2);
    }

    internal static void Jz(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & 1) != 0)
        {
            hw.SetInstructionPointer((b1 << 8) | b2);
        }
    }

    internal static void Jnz(Hardware hw, byte b1, byte b2, byte b3)
    {
        if ((hw.ReadRegister(RegisterName.EFLAGS) & 1) == 0)
        {
            hw.SetInstructionPointer((b1 << 8) | b2);
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
        hw.SetInstructionPointer((b1 << 8) | b2);
    }

    internal static void Ret(Hardware hw, byte b1, byte b2, byte b3)
    {
        int esp = hw.ReadRegister(RegisterName.ESP);
        byte[] bytes = hw.ReadBytes(esp);
        int returnAddress = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
        hw.WriteRegister(RegisterName.ESP, esp + 4);
        hw.SetInstructionPointer(returnAddress);
    }

    private static void UpdateZeroFlag(Hardware hw, int result)
    {
        int flags = hw.ReadRegister(RegisterName.EFLAGS);
        if (result == 0)
        {
            flags |= 1;
        }
        else
        {
            flags &= ~1;
        }
        hw.WriteRegister(RegisterName.EFLAGS, flags);
    }
}
