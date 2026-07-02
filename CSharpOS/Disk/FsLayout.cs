namespace CSharpOS;

/// <summary>
/// On-disk layout of the filesystem within the disk's file-block region (distinct from
/// <see cref="OsLayout"/>, which lays out the OS's RAM region). The filesystem's ISA
/// routines read these constants to locate the superblock, the free bitmap, and the
/// per-block free-chaining link. Extended by later increments (the directory tree adds
/// entry-field offsets and the superblock's root-directory pointer).
///
/// Block map:
///   block 0            — superblock (magic + geometry; root-dir pointer added in Inc 4)
///   block 1            — free bitmap (1 bit per block, bit = 1 means allocated)
///   blocks 2..N-1      — allocatable data blocks
///
/// Each block stores payload in bytes [0, NextPtrOffset) and a next-block link in its last
/// word, so a file can chain across discontinuous blocks (-1 ends the chain).
/// </summary>
public static class FsLayout
{
    public const int BlockSize  = Hardware.DefaultFileBlockSize;   // 256
    public const int BlockCount = Hardware.DefaultFileBlockCount;  // 256
    public const int BitmapWords = BlockCount / 32;                // 8 (256 bits → 8 × 32-bit words)

    // Reserved blocks.
    public const int SuperBlock     = 0;
    public const int BitmapBlock    = 1;
    public const int FirstDataBlock = 2;

    // In-block free-chaining link: the last word of every block.
    public const int NextPtrOffset = BlockSize - 4;   // 252
    public const int PayloadBytes  = NextPtrOffset;    // 252 usable bytes per block
    public const int EndOfChain    = -1;

    // Superblock fields (16-bit magic so the ISA formatter can build it with one MovImm16;
    // the .bin file header uses a separate 32-bit "CSFS" magic in Bin). FreeCount is written
    // by format but not yet maintained per-alloc — the bitmap is the source of truth.
    public const int SuperMagic            = 0x5346;  // 'S','F'
    public const int SuperMagicOffset      = 0;
    public const int SuperBlockCountOffset = 4;
    public const int SuperFreeCountOffset  = 8;
    public const int SuperRootDirOffset    = 12;      // set by the directory layer (Inc 4)
}
