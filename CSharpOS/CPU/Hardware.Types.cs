namespace CSharpOS;

public partial class Hardware
{
    private enum InterruptKind { InputReady, OutputComplete, StringInputReady, KeyInputReady }

    // An interrupt from a device. Device identifies which terminal/process it is
    // for (the device id == the owning process's table index), mirroring how real
    // hardware tags an interrupt by its source device/IRQ rather than by process.
    private readonly struct Interrupt
    {
        public readonly InterruptKind Kind;
        public readonly int Value;
        public readonly string? StringValue;
        public readonly int Device;
        public Interrupt(InterruptKind kind, int value, int device) { Kind = kind; Value = value; Device = device; StringValue = null; }
        public Interrupt(InterruptKind kind, string stringValue, int device) { Kind = kind; Value = 0; StringValue = stringValue; Device = device; }
    }
}
