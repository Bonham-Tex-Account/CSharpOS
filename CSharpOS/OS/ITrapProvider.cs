namespace CSharpOS;

/// <summary>
/// Supplies a single instruction trap (an opcode plus the condition that makes it
/// fault). Implementations are discovered by reflection at OS load time, so a new
/// trap handler can be added simply by implementing this interface — no manual
/// registration list. See <see cref="OperatingSystem"/> and the plugin's trap
/// collection for how providers are gathered.
/// </summary>
public interface ITrapProvider
{
    /// <summary>Returns the trap (opcode, reason, and fault condition) this provider defines.</summary>
    Trap GetTrap();
}
