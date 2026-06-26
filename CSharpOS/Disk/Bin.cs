namespace CSharpOS;

/// <summary>
/// A flat, fixed-slot block store — the backing store for the disk device. The
/// disk is divided into <see cref="SlotCount"/> equal slots of <see cref="SlotSize"/>
/// bytes; a small directory (occupied flag + real content length per slot) lets a
/// short blob occupy a large slot and be read back at its true length. Naming
/// follows the ISA convention used elsewhere: <c>Store</c> writes, <c>Load</c>
/// reads, <c>Free</c> empties.
/// </summary>
public class Bin
{
    // ---- private fields --------------------------------------------------
    private readonly byte[] storage;   // slotCount * slotSize, slots laid out back to back
    private readonly bool[] occupied;  // directory: slot in use?
    private readonly int[] lengths;    // directory: real content length per slot
    private readonly int slotSize;
    private readonly int slotCount;

    // ---- constructor -----------------------------------------------------
    public Bin(int slotCount, int slotSize)
    {
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Bin must have at least one slot.");
        }
        if (slotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotSize), "Bin slots must be at least one byte.");
        }
        this.slotCount = slotCount;
        this.slotSize = slotSize;
        storage = new byte[slotCount * slotSize];
        occupied = new bool[slotCount];
        lengths = new int[slotCount];
    }

    // ---- accessor methods ------------------------------------------------
    public int SlotCount { get { return slotCount; } }
    public int SlotSize { get { return slotSize; } }

    // Number of currently free slots.
    public int FreeSlotCount
    {
        get
        {
            int free = 0;
            for (int i = 0; i < slotCount; i++)
            {
                if (!occupied[i])
                {
                    free++;
                }
            }
            return free;
        }
    }

    public bool IsOccupied(int slot)
    {
        ValidateSlot(slot);
        return occupied[slot];
    }

    // ---- integral functions ----------------------------------------------

    // Stores data in the first free slot. Returns the slot index, or -1 if the disk
    // is full. Throws if the data does not fit in a single slot.
    public int Store(byte[] data)
    {
        if (data.Length > slotSize)
        {
            throw new ArgumentException($"Data of {data.Length} bytes does not fit in a {slotSize}-byte slot.", nameof(data));
        }
        for (int slot = 0; slot < slotCount; slot++)
        {
            if (!occupied[slot])
            {
                WriteSlot(slot, data);
                return slot;
            }
        }
        return -1;
    }

    // Stores data into a specific slot, overwriting whatever was there. Throws if the
    // slot is out of range or the data does not fit.
    public void Store(int slot, byte[] data)
    {
        ValidateSlot(slot);
        if (data.Length > slotSize)
        {
            throw new ArgumentException($"Data of {data.Length} bytes does not fit in a {slotSize}-byte slot.", nameof(data));
        }
        WriteSlot(slot, data);
    }

    // Reads back the content of an occupied slot as a fresh copy of its true length.
    // Throws if the slot is out of range or free.
    public byte[] Load(int slot)
    {
        ValidateSlot(slot);
        if (!occupied[slot])
        {
            throw new InvalidOperationException($"Slot {slot} is free; nothing to load.");
        }
        int length = lengths[slot];
        byte[] result = new byte[length];
        Array.Copy(storage, slot * slotSize, result, 0, length);
        return result;
    }

    // The real content length of an occupied slot (used by the load orchestration to
    // size the allocation). Throws if the slot is out of range or free.
    public int GetLength(int slot)
    {
        ValidateSlot(slot);
        if (!occupied[slot])
        {
            throw new InvalidOperationException($"Slot {slot} is free; it has no length.");
        }
        return lengths[slot];
    }

    // Empties a slot: zeroes its storage and marks it free. Idempotent — freeing an
    // already-free slot is a no-op (but still range-checked).
    public void Free(int slot)
    {
        ValidateSlot(slot);
        ZeroSlot(slot);
        occupied[slot] = false;
        lengths[slot] = 0;
    }

    // ---- helper functions ------------------------------------------------

    // Zeroes the whole slot then writes the data at its front; zeroing first means a
    // shorter overwrite leaves no stale tail from a previous occupant.
    private void WriteSlot(int slot, byte[] data)
    {
        ZeroSlot(slot);
        Array.Copy(data, 0, storage, slot * slotSize, data.Length);
        occupied[slot] = true;
        lengths[slot] = data.Length;
    }

    private void ZeroSlot(int slot)
    {
        Array.Clear(storage, slot * slotSize, slotSize);
    }

    private void ValidateSlot(int slot)
    {
        if (slot < 0 || slot >= slotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} is outside the range 0..{slotCount - 1}.");
        }
    }
}
