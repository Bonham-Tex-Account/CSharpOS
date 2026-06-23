namespace CSharpOS;

public sealed class StoreBoundsTrapProvider : ITrapProvider
{
    public Trap GetTrap()
    {
        return new Trap
        {
            Opcode = Instruction.STORE,
            Reason = "Memory write outside process bounds",
            Condition = (hw, b1, b2, b3) =>
            {
                if (hw.GetPrivilegeLevel() == PrivilegeLevel.User
                    && !hw.IsAddressInProcessRanges(hw.GetProgramBase() + hw.ReadRegisterAt(b1)))
                {
                    return true;
                }
                return false;
            }
        };
    }
}
