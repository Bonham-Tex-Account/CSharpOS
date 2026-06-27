namespace CSharpOS;

/// <summary>
/// Traps a user-mode STORE whose effective address falls outside the running process's
/// memory ranges. Discovered and registered by reflection at OS load.
/// </summary>
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
