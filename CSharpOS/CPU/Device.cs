namespace CSharpOS;

/// <summary>
/// The kind of device in the hardware device table. Character devices buffer a
/// stream of integer values (a terminal-like keyboard/screen) and track an
/// output-busy flag; Block is reserved for the disk effort (random-access storage).
/// </summary>
public enum DeviceType
{
    Character,
    Block
}

/// <summary>
/// A first-class I/O device, independent of any process. Devices are addressed by
/// a device-id namespace that is no longer tied to the process-table index; a
/// process reaches a device through its file-descriptor table (see
/// <see cref="Hardware.ProcessEntryFdTable"/>). A character device owns an input
/// buffer, a wait queue (process indices currently blocked on its input), and an
/// output-busy flag. The block-device backing store (the disk's Bin) is added by
/// the Bin filesystem effort.
/// </summary>
public class Device
{
    public int Id;
    public DeviceType Type;
    public Queue<int> Input;
    public List<int> Waiters;
    public Queue<string> StringInput;
    public List<int> StringWaiters;
    public bool OutputBusy;
    // Backing store for a block device (the disk's flat slot store); null for a
    // character device.
    public Bin? Block;

    public Device(int id, DeviceType type)
    {
        Id = id;
        Type = type;
        Input = new Queue<int>();
        Waiters = new List<int>();
        StringInput = new Queue<string>();
        StringWaiters = new List<int>();
        OutputBusy = false;
        Block = null;
    }

    // Block-device constructor: a device whose backing store is the given Bin.
    public Device(int id, Bin block)
    {
        Id = id;
        Type = DeviceType.Block;
        Input = new Queue<int>();
        Waiters = new List<int>();
        StringInput = new Queue<string>();
        StringWaiters = new List<int>();
        OutputBusy = false;
        Block = block;
    }
}
