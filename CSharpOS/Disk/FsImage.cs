namespace CSharpOS;

/// <summary>
/// Host-side helper for populating the filesystem with files (e.g. program images) before
/// they are run. It drives the same ISA filesystem cores the FSYS syscalls use, through the
/// <see cref="Hardware.IvtFsOp"/> testing selectors, so the on-disk result is identical to a
/// file a user process would have written.
///
/// Intended for boot-time population: it stages the path and content in the free heap just
/// above the OS region, so calling it after processes have been allocated may clobber their
/// memory. Content is copied word-per-4-bytes, so a file's raw bytes end up identical to the
/// supplied buffer (padded up to a multiple of four) — exactly what <c>fs_exec_core</c> reads
/// back into RAM when launching the file.
/// </summary>
public static class FsImage
{
    // The owning process index used for the transient open/write/close. Close clears the fd and
    // open-file-table slot it touched, so this leaves no lasting state in that process entry.
    private const int ScratchProc = OsLayout.MaxProcesses - 1;

    /// <summary>
    /// Writes <paramref name="content"/> into the filesystem as a file at the absolute
    /// <paramref name="path"/> (for example "/prog"), creating it when absent.
    /// </summary>
    public static void WriteFile(Hardware hw, string path, byte[] content)
    {
        int pathAddr = OsLayout.TotalSize;          // scratch staging, just above the OS region
        int dataAddr = OsLayout.TotalSize + 4096;

        for (int i = 0; i < path.Length; i++)
        {
            WriteWord(hw, pathAddr + i * 4, path[i]);
        }
        WriteWord(hw, pathAddr + path.Length * 4, 0);

        int words = (content.Length + 3) / 4;
        byte[] padded = new byte[words * 4];
        Array.Copy(content, padded, content.Length);
        hw.WriteBytes(dataAddr, padded);

        int fd = FsOp(hw, Hardware.FsOpOpen, pathAddr, Hardware.FsysCreateFlag, ScratchProc, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"FsImage.WriteFile: could not open '{path}' (fd={fd}).");
        }
        int written = FsOp(hw, Hardware.FsOpWrite, fd, dataAddr, words, ScratchProc);
        if (written != words)
        {
            throw new InvalidOperationException($"FsImage.WriteFile: short write for '{path}' ({written}/{words} words).");
        }
        FsOp(hw, Hardware.FsOpClose, fd, ScratchProc, 0, 0);
    }

    /// <summary>
    /// Installs <paramref name="content"/> into the filesystem as a file at absolute
    /// <paramref name="path"/>, creating it when absent (Phase 4: boot-from-FS program install).
    /// Unlike <see cref="WriteFile"/>, this stages the path and each write chunk in the OS
    /// region (below <see cref="OsLayout.TotalSize"/>), so it is safe to call at runtime after
    /// processes have been allocated — the staging never overlaps the buddy heap. The content
    /// is written in block-sized chunks to keep the staging buffer one block wide. It drives the
    /// FsOp cores with the caller-supplied <paramref name="procIndex"/> for the transient
    /// open/write/close (LoadProcess passes the not-yet-spawned target slot, whose fd table is
    /// free and is re-seeded afterward).
    /// </summary>
    public static void InstallProgram(Hardware hw, string path, byte[] content, int procIndex)
    {
        int pathAddr = OsLayout.InstallPathBase;
        for (int i = 0; i < path.Length; i++)
        {
            WriteWord(hw, pathAddr + i * 4, path[i]);
        }
        WriteWord(hw, pathAddr + path.Length * 4, 0);

        int fd = FsOp(hw, Hardware.FsOpOpen, pathAddr, Hardware.FsysCreateFlag, procIndex, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"FsImage.InstallProgram: could not open '{path}' (fd={fd}).");
        }

        int totalWords = (content.Length + 3) / 4;
        int bufAddr = OsLayout.InstallBufBase;
        int chunkCap = OsLayout.InstallBufWords;
        int writtenWords = 0;
        while (writtenWords < totalWords)
        {
            int chunk = Math.Min(chunkCap, totalWords - writtenWords);
            byte[] buf = new byte[chunk * 4];
            int srcOffset = writtenWords * 4;
            int copy = Math.Min(buf.Length, content.Length - srcOffset);
            if (copy > 0)
            {
                Array.Copy(content, srcOffset, buf, 0, copy);
            }
            hw.WriteBytes(bufAddr, buf);
            int w = FsOp(hw, Hardware.FsOpWrite, fd, bufAddr, chunk, procIndex);
            if (w != chunk)
            {
                throw new InvalidOperationException($"FsImage.InstallProgram: short write for '{path}' ({w}/{chunk} words).");
            }
            writtenWords += chunk;
        }

        FsOp(hw, Hardware.FsOpClose, fd, procIndex, 0, 0);
    }

    /// <summary>
    /// Ensures the directory at absolute <paramref name="path"/> exists (creating it and any
    /// intermediate this-level parent is out of scope — MkdirPath resolves the parent itself).
    /// A duplicate (already-present) directory is not an error. Stages the path in the OS region.
    /// </summary>
    public static void EnsureDir(Hardware hw, string path)
    {
        int pathAddr = OsLayout.InstallPathBase;
        for (int i = 0; i < path.Length; i++)
        {
            WriteWord(hw, pathAddr + i * 4, path[i]);
        }
        WriteWord(hw, pathAddr + path.Length * 4, 0);
        // MkdirPath returns the new dir block, or -1 if it already exists (or on failure);
        // either way the directory is present afterward, so the result is intentionally ignored.
        FsOp(hw, Hardware.FsOpMkdirPath, pathAddr, 0, 0, 0);
    }

    /// <summary>
    /// Resolves absolute <paramref name="path"/> to a file and returns its first data block,
    /// or -1 if the path does not resolve. Stages the path in the OS region. Used by LoadProcess
    /// to record an installed program's FirstBlock in the process entry.
    /// </summary>
    public static int ResolveFirstBlock(Hardware hw, string path)
    {
        int pathAddr = OsLayout.InstallPathBase;
        for (int i = 0; i < path.Length; i++)
        {
            WriteWord(hw, pathAddr + i * 4, path[i]);
        }
        WriteWord(hw, pathAddr + path.Length * 4, 0);
        int entryAddr = FsOp(hw, Hardware.FsOpPathResolve, pathAddr, 0, 0, 0);
        if (entryAddr < 0)
        {
            return -1;
        }
        return ReadWord(hw, entryAddr + FsLayout.DirEntryFirstBlock);
    }

    private static int FsOp(Hardware hw, int op, int a1, int a2, int a3, int a4)
    {
        hw.WriteRegister(RegisterName.EBX, a1);
        hw.WriteRegister(RegisterName.ECX, a2);
        hw.WriteRegister(RegisterName.EDX, a3);
        hw.WriteRegister(RegisterName.ESI, a4);
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, op);
        return ReadWord(hw, OsLayout.FsResultOffset);
    }

    private static void WriteWord(Hardware hw, int address, int value)
    {
        hw.WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }
}
