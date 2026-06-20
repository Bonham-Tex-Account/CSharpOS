namespace CSharpOS;

public partial class Hardware
{
    private enum InterruptKind { InputReady, OutputComplete }

    private readonly struct Interrupt
    {
        public readonly InterruptKind Kind;
        public readonly int Value;
        public Interrupt(InterruptKind kind, int value) { Kind = kind; Value = value; }
    }
}
