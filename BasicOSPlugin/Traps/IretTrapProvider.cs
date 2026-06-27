namespace CSharpOS;

/// <summary>
/// Traps IRET when executed in user mode: returning from an interrupt is privileged.
/// Discovered and registered by reflection at OS load.
/// </summary>
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
