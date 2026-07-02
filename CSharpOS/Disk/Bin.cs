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

    // The file-block region: a second, independent backing store addressed as fixed-size
    // blocks (block-addressed) rather than as variable-length slots. The filesystem effort
    // layers a directory tree + cache over these raw blocks. Unlike a slot, a block has no
    // occupied/length directory: it reads back its full fileBlockSize at all times (zeros
    // until first written), which is what a raw block device needs. Empty (count 0) unless
    // the four-argument constructor is used.
    private readonly byte[] fileBlocks; // fileBlockCount * fileBlockSize, blocks back to back
    private readonly int fileBlockSize;
    private readonly int fileBlockCount;

    private const int PersistMagic = 0x43534653; // "CSFS" — file-block persistence header

    // ---- constructor -----------------------------------------------------
    public Bin(int slotCount, int slotSize)
        : this(slotCount, slotSize, 0, 0)
    {
    }

    // Four-argument form adds a file-block region of fileBlockCount blocks of
    // fileBlockSize bytes. Pass fileBlockCount = 0 for a slot-only disk.
    public Bin(int slotCount, int slotSize, int fileBlockCount, int fileBlockSize)
    {
        if (slotCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), "Bin must have at least one slot.");
        }
        if (slotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotSize), "Bin slots must be at least one byte.");
        }
        if (fileBlockCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBlockCount), "File-block count cannot be negative.");
        }
        if (fileBlockCount > 0 && fileBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileBlockSize), "File blocks must be at least one byte.");
        }
        this.slotCount = slotCount;
        this.slotSize = slotSize;
        this.fileBlockCount = fileBlockCount;
        this.fileBlockSize = fileBlockCount > 0 ? fileBlockSize : 0;
        storage = new byte[slotCount * slotSize];
        occupied = new bool[slotCount];
        lengths = new int[slotCount];
        fileBlocks = new byte[fileBlockCount * this.fileBlockSize];
    }

    // ---- accessor methods ------------------------------------------------
    public int SlotCount { get { return slotCount; } }
    public int SlotSize { get { return slotSize; } }
    public int FileBlockCount { get { return fileBlockCount; } }
    public int FileBlockSize { get { return fileBlockSize; } }

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

    // ---- file-block region -----------------------------------------------

    // Reads a whole block as a fresh copy of fileBlockSize bytes. A block never written
    // reads back all zeros (raw block-device semantics), so — unlike Load — this never
    // throws for an "empty" block. Throws only if the block index is out of range.
    public byte[] ReadFileBlock(int block)
    {
        ValidateBlock(block);
        byte[] result = new byte[fileBlockSize];
        Array.Copy(fileBlocks, block * fileBlockSize, result, 0, fileBlockSize);
        return result;
    }

    // Overwrites a whole block. The data must be exactly fileBlockSize bytes — blocks are
    // fixed-size, so partial writes are a caller error rather than a silent short write.
    public void WriteFileBlock(int block, byte[] data)
    {
        ValidateBlock(block);
        if (data.Length != fileBlockSize)
        {
            throw new ArgumentException($"Block write of {data.Length} bytes must be exactly {fileBlockSize} bytes.", nameof(data));
        }
        Array.Copy(data, 0, fileBlocks, block * fileBlockSize, fileBlockSize);
    }

    private void ValidateBlock(int block)
    {
        if (fileBlockCount == 0)
        {
            throw new InvalidOperationException("This Bin has no file-block region.");
        }
        if (block < 0 || block >= fileBlockCount)
        {
            throw new ArgumentOutOfRangeException(nameof(block), $"Block {block} is outside the range 0..{fileBlockCount - 1}.");
        }
    }

    // ---- persistence -----------------------------------------------------

    // Writes the file-block region to a host file so a filesystem survives between runs.
    // Only the file-block region is persisted: the image slots are rebuilt from the loaded
    // programs at each boot and the paging swap region is transient. The header records the
    // geometry so Load can reject a file whose dimensions no longer match.
    public void Save(string path)
    {
        if (fileBlockCount == 0)
        {
            throw new InvalidOperationException("This Bin has no file-block region to save.");
        }
        using (System.IO.BinaryWriter writer = new System.IO.BinaryWriter(System.IO.File.Create(path)))
        {
            writer.Write(PersistMagic);
            writer.Write(fileBlockCount);
            writer.Write(fileBlockSize);
            writer.Write(fileBlocks);
        }
    }

    // Restores the file-block region previously written by Save. Throws if the file is
    // missing, not a file-block image, or was saved with different geometry than this Bin.
    public void Load(string path)
    {
        if (fileBlockCount == 0)
        {
            throw new InvalidOperationException("This Bin has no file-block region to load into.");
        }
        using (System.IO.BinaryReader reader = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
        {
            int magic = reader.ReadInt32();
            if (magic != PersistMagic)
            {
                throw new InvalidDataException($"'{path}' is not a file-block image (bad magic 0x{magic:X8}).");
            }
            int savedCount = reader.ReadInt32();
            int savedSize = reader.ReadInt32();
            if (savedCount != fileBlockCount || savedSize != fileBlockSize)
            {
                throw new InvalidDataException($"'{path}' geometry {savedCount}x{savedSize} does not match this disk's {fileBlockCount}x{fileBlockSize}.");
            }
            byte[] data = reader.ReadBytes(fileBlockCount * fileBlockSize);
            if (data.Length != fileBlocks.Length)
            {
                throw new InvalidDataException($"'{path}' is truncated: expected {fileBlocks.Length} block bytes, got {data.Length}.");
            }
            Array.Copy(data, fileBlocks, fileBlocks.Length);
        }
    }
}
