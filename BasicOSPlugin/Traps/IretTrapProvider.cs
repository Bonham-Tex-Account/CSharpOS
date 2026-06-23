namespace CSharpOS;

public sealed class IretTrapProvider : ITrapProvider
{
    public Trap GetTrap()
    {
        return new Trap
        {
            Opcode = Instruction.IRET,
            Reason = "IRET is a privileged instruction",
            Condition = (hw, b1, b2, b3) =>
            {
                if (hw.GetPrivilegeLevel() == PrivilegeLevel.User)
                {
                    return true;
                }
                return false;
            }
        };
    }
}
