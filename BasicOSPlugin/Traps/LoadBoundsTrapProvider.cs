namespace CSharpOS;

public sealed class LoadBoundsTrapProvider : ITrapProvider
{
    public Trap GetTrap()
    {
        return new Trap
        {
            Opcode = Instruction.LOAD,
            Reason = "Memory read outside process bounds",
            Condition = (hw, b1, b2, b3) =>
            {
                if (hw.GetPrivilegeLevel() == PrivilegeLevel.User
                    && !hw.IsAddressInProcessRanges(hw.GetProgramBase() + hw.ReadRegisterAt(b2)))
                {
                    return true;
                }
                return false;
            }
        };
    }
}
