namespace CSharpOS;

/// <summary>
/// The CPU's register file. The enum value doubles as the register's index: an
/// instruction's operand byte selects a register by ordinal, and a register's
/// byte offset within the saved register file is its index times the word size.
/// EIP and EFLAGS are the program counter and status flags; CS..SS are segment
/// registers; R8-R15 are general-purpose scratch used by the OS routines.
/// </summary>
public enum RegisterName
{
    EAX, EBX, ECX, EDX,
    ESI, EDI, ESP, EBP,
    EIP, EFLAGS,
    CS, DS, ES, FS, GS, SS,
    R8, R9, R10, R11, R12, R13, R14, R15
}
