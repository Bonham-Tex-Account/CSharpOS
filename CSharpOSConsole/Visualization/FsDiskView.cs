using System;
using System.Collections.Generic;
using System.Text;
using CSharpOS;

namespace CSharpOSConsole.Visualization;

/// <summary>
/// Reconstructs the filesystem's on-disk state into structured, render-agnostic data:
/// the superblock stats, a per-block allocation map, and the directory tree (files and
/// nested dirs with sizes). It mirrors <see cref="BuddyHeapView"/> — a static reader that
/// turns emulator state into an immutable snapshot the renderers can hold across frames.
///
/// Reads are <b>cache-first</b>: a just-created dir entry or a freshly-written file lives
/// dirty in the OS RAM write-back cache (<see cref="OsLayout.CacheSlotTableBase"/>) and has
/// not yet been flushed to the <see cref="Bin"/>. So each block is looked up in the cache
/// slots first and only falls back to <see cref="Bin.ReadFileBlock"/> on a miss — otherwise
/// the view would lag until the periodic flush. All layout constants come from the public
/// <see cref="FsLayout"/> / <see cref="OsLayout"/> so the reader stays in lock-step.
/// </summary>
public static class FsDiskView
{
    public enum BlockRole
    {
        Free,
        Super,
        Bitmap,
        Used
    }

    /// <summary>One node of the reconstructed FS tree: a file (leaf) or a directory
    /// (with children). Immutable once built — the bridge swaps the whole snapshot.</summary>
    public sealed class DiskNode
    {
        public string Name { get; init; } = "";
        public bool IsDir { get; init; }
        public int FirstBlock { get; init; }
        public int Size { get; init; }     // file size in bytes; 0 for a directory
        public List<DiskNode> Children { get; } = new List<DiskNode>();
    }

    /// <summary>A flattened tree row: depth-first order with an indentation depth,
    /// so a renderer can draw the tree as a simple indented list.</summary>
    public sealed class TreeRow
    {
        public int Depth { get; init; }
        public DiskNode Node { get; init; } = new DiskNode();
    }

    /// <summary>The immutable per-frame disk snapshot.</summary>
    public sealed class Snapshot
    {
        public bool Formatted { get; init; }
        public int BlockCount { get; init; }
        public int FreeCount { get; init; }     // from the superblock (free-block accounting)
        public int UsedCount { get; init; }      // allocated blocks derived from the bitmap
        public int RootBlock { get; init; }
        public BlockRole[] BlockRoles { get; init; } = Array.Empty<BlockRole>();
        public DiskNode Root { get; init; } = new DiskNode();
    }

    /// <summary>
    /// Rebuilds the disk snapshot from hardware, or null when there is no OS image or no
    /// filesystem region. Reads cache-first (see class remarks). An unformatted disk yields
    /// a snapshot with <see cref="Snapshot.Formatted"/> false and an empty root.
    /// </summary>
    public static Snapshot? ReadDisk(Hardware hw)
    {
        if (!BuddyHeapView.HasOs(hw))
        {
            return null;
        }
        Bin disk = hw.Disk;
        if (disk.FileBlockCount == 0)
        {
            return null;
        }
        byte[] superBytes = ReadBlock(hw, FsLayout.SuperBlock);
        int magic = Word(superBytes, FsLayout.SuperMagicOffset) & 0xFFFF;
        if (magic != FsLayout.SuperMagic)
        {
            return new Snapshot
            {
                Formatted = false,
                BlockCount = disk.FileBlockCount,
                FreeCount = 0,
                UsedCount = 0,
                RootBlock = FsLayout.FirstDataBlock,
                BlockRoles = Array.Empty<BlockRole>(),
                Root = new DiskNode { Name = "/", IsDir = true, FirstBlock = FsLayout.FirstDataBlock }
            };
        }
        int blockCount = Word(superBytes, FsLayout.SuperBlockCountOffset);
        if (blockCount <= 0 || blockCount > disk.FileBlockCount)
        {
            blockCount = disk.FileBlockCount;
        }
        int freeCount = Word(superBytes, FsLayout.SuperFreeCountOffset);
        int rootBlock = Word(superBytes, FsLayout.SuperRootDirOffset);
        BlockRole[] roles = BuildRoles(hw, blockCount);
        int used = 0;
        for (int i = 0; i < roles.Length; i++)
        {
            if (roles[i] == BlockRole.Used)
            {
                used++;
            }
        }
        DiskNode root = BuildDir(hw, rootBlock, "/", new HashSet<int>());
        return new Snapshot
        {
            Formatted = true,
            BlockCount = blockCount,
            FreeCount = freeCount,
            UsedCount = used,
            RootBlock = rootBlock,
            BlockRoles = roles,
            Root = root
        };
    }

    /// <summary>Depth-first flatten of the tree for indented rendering (root first).</summary>
    public static List<TreeRow> FlattenTree(Snapshot? snapshot)
    {
        List<TreeRow> rows = new List<TreeRow>();
        if (snapshot != null)
        {
            FlattenInto(snapshot.Root, 0, rows);
        }
        return rows;
    }

    private static void FlattenInto(DiskNode node, int depth, List<TreeRow> rows)
    {
        rows.Add(new TreeRow { Depth = depth, Node = node });
        foreach (DiskNode child in node.Children)
        {
            FlattenInto(child, depth + 1, rows);
        }
    }

    // Classifies every block: 0 = superblock, 1 = bitmap, others by the free bitmap
    // (bit set = allocated). block 1's bitmap is 256 bits packed into BitmapWords words.
    private static BlockRole[] BuildRoles(Hardware hw, int blockCount)
    {
        BlockRole[] roles = new BlockRole[blockCount];
        byte[] bitmap = ReadBlock(hw, FsLayout.BitmapBlock);
        for (int b = 0; b < blockCount; b++)
        {
            if (b == FsLayout.SuperBlock)
            {
                roles[b] = BlockRole.Super;
                continue;
            }
            if (b == FsLayout.BitmapBlock)
            {
                roles[b] = BlockRole.Bitmap;
                continue;
            }
            int word = Word(bitmap, (b / 32) * 4);
            bool allocated = ((word >> (b % 32)) & 1) != 0;
            if (allocated)
            {
                roles[b] = BlockRole.Used;
            }
            else
            {
                roles[b] = BlockRole.Free;
            }
        }
        return roles;
    }

    // Walks a directory's block chain, building a node per in-use entry and recursing into
    // subdirectories. visitedDirs breaks parent/child cycles; chainSeen breaks a corrupt
    // next-pointer loop within one directory. Both keep a damaged disk from hanging the view.
    private static DiskNode BuildDir(Hardware hw, int dirBlock, string name, HashSet<int> visitedDirs)
    {
        DiskNode node = new DiskNode
        {
            Name = name,
            IsDir = true,
            FirstBlock = dirBlock,
            Size = 0
        };
        if (dirBlock < FsLayout.FirstDataBlock || !visitedDirs.Add(dirBlock))
        {
            return node;
        }
        HashSet<int> chainSeen = new HashSet<int>();
        int block = dirBlock;
        while (block >= FsLayout.FirstDataBlock && chainSeen.Add(block))
        {
            byte[] data = ReadBlock(hw, block);
            for (int e = 0; e < FsLayout.DirEntriesPerBlock; e++)
            {
                int off = e * FsLayout.DirEntryBytes;
                int type = Word(data, off + FsLayout.DirEntryType);
                if (type == FsLayout.DirTypeFree)
                {
                    continue;
                }
                int first = Word(data, off + FsLayout.DirEntryFirstBlock);
                int size = Word(data, off + FsLayout.DirEntrySizeField);
                string childName = ReadName(data, off + FsLayout.DirEntryName);
                if (type == FsLayout.DirTypeDir)
                {
                    node.Children.Add(BuildDir(hw, first, childName, visitedDirs));
                }
                else
                {
                    node.Children.Add(new DiskNode
                    {
                        Name = childName,
                        IsDir = false,
                        FirstBlock = first,
                        Size = size
                    });
                }
            }
            block = Word(data, FsLayout.NextPtrOffset);
        }
        return node;
    }

    // Names are stored word-per-char (one char per 4-byte word, null-terminated).
    private static string ReadName(byte[] data, int offset)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < FsLayout.NameMaxChars; i++)
        {
            int c = Word(data, offset + i * 4);
            if (c == 0)
            {
                break;
            }
            builder.Append((char)(c & 0xFF));
        }
        return builder.ToString();
    }

    // Reads one file block, preferring a valid cache slot holding it (dirty = newest data)
    // over the on-disk copy in the Bin.
    private static byte[] ReadBlock(Hardware hw, int block)
    {
        for (int i = 0; i < OsLayout.CacheSlotCount; i++)
        {
            int slot = OsLayout.CacheSlotAddress(i);
            if (ReadWord(hw, slot + OsLayout.CacheValidField) != 0
                && ReadWord(hw, slot + OsLayout.CacheBlockField) == block)
            {
                return ReadRange(hw, slot + OsLayout.CacheDataField, FsLayout.BlockSize);
            }
        }
        return hw.Disk.ReadFileBlock(block);
    }

    // Copies `length` physical bytes out of hardware memory (ReadBytes yields 4 at a time).
    private static byte[] ReadRange(Hardware hw, int address, int length)
    {
        byte[] result = new byte[length];
        int pos = 0;
        while (pos < length)
        {
            byte[] word = hw.ReadBytes(address + pos);
            int n = length - pos;
            if (n > 4)
            {
                n = 4;
            }
            for (int k = 0; k < n; k++)
            {
                result[pos + k] = word[k];
            }
            pos += 4;
        }
        return result;
    }

    private static int Word(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }
}
