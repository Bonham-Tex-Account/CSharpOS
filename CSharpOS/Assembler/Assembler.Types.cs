namespace CSharpOS;

public sealed partial class Assembler
{
    private struct Fixup
    {
        public int Position;
        public string Label;
        public bool Imm8;
    }
}
