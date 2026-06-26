namespace CSharpOS;

/// <summary>
/// Base operating system. The scheduling and allocation logic now lives in ISA
/// code in the OS memory region (see <see cref="OsRoutines"/>); this class only
/// boots that image, loads programs (driving the ISA allocator), and answers
/// liveness queries by reading the OS data structures from memory.
/// </summary>
public abstract class OperatingSystem : IOperatingSystem
{
    // ---- public properties -----------------------------------------------

    // Syscall functions shipped by this OS, copied into each process's kernel
    // section. Empty until overridden; subclasses supply the syscall library.
    public virtual byte[] KernelImage => Array.Empty<byte>();

    // The OS in-memory image. Defaults to none (size 0); subclasses that run their
    // routines as ISA code override these to reserve and populate the OS region.
    public virtual int OsMemorySize => 0;
    public virtual byte[] BuildOsImage(int osMemoryBase) => Array.Empty<byte>();

    public bool HasProcesses => CountLiveProcesses() > 0;
    public bool HasRunningProcess => hardware != null && hardware.IsProcessRunning();

    // ---- private fields --------------------------------------------------
    private readonly List<Trap> traps;
    private readonly TextWriter log;
    private Hardware? hardware;

    // Maps a loaded process's program base address to its file path, so consumers
    // (e.g. the visualizer) can name the process behind a Hardware context switch.
    private readonly Dictionary<int, string> namesByBase = new Dictionary<int, string>();

    // ---- constructor -----------------------------------------------------
    protected OperatingSystem(List<Trap> traps, TextWriter log)
    {
        this.traps = traps;
        this.log = log;
    }

    // ---- setup -----------------------------------------------------------
    public void AttachHardware(Hardware hw)
    {
        hardware = hw;
        hw.LoadTraps(traps);
        hw.ReserveOsMemory(OsMemorySize);
        if (OsMemorySize > 0)
        {
            hw.WriteBytes(0, BuildOsImage(0));
            SeedOsData(hw);
        }
    }

    // Initialises the OS data structures: no current process, empty process table,
    // and a buddy allocator bitmap covering the largest power-of-2 region above the
    // OS image. The root node (bit 0 of the first bitmap word) is set to 1 (free);
    // all other bits are 0. The ISA allocator reads BuddyHeapStart, BuddyHeapSize,
    // BuddyMinBlock, and BuddyLevels from the data section on every call.
    private void SeedOsData(Hardware hw)
    {
        int available = hw.GetMemorySize() - OsMemorySize;
        int heapSize = LargestPowerOfTwoFitting(available);
        int heapStart = OsMemorySize;
        int minBlock = OsLayout.BuddyDefaultMinBlock;
        int levels = Log2(heapSize / minBlock);

        WriteWord(hw, OsLayout.ProcessCountOffset, 0);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        WriteWord(hw, OsLayout.BuddyHeapStartOffset, heapStart);
        WriteWord(hw, OsLayout.BuddyHeapSizeOffset, heapSize);
        WriteWord(hw, OsLayout.BoostTimerOffset, OsLayout.BoostInterval);
        WriteWord(hw, OsLayout.NextPidOffset, 1); // PIDs start at 1 (0 = "no process")

        // Stage the syscall (kernel) image to a disk slot so the ISA EXEC routine can
        // re-load it into a re-execed process's kernel section (boot creation still
        // writes it from C#). The slot id lives in OS data for the routine to read.
        int kernelImageSlot = hw.Disk.Store(KernelImage);
        WriteWord(hw, OsLayout.KernelImageSlotOffset, kernelImageSlot);
        WriteWord(hw, OsLayout.QuantumTableOffset + 0,  1);
        WriteWord(hw, OsLayout.QuantumTableOffset + 4,  2);
        WriteWord(hw, OsLayout.QuantumTableOffset + 8,  4);
        WriteWord(hw, OsLayout.QuantumTableOffset + 12, 255);
        WriteWord(hw, OsLayout.BuddyMinBlockOffset, minBlock);
        WriteWord(hw, OsLayout.BuddyLevelsOffset, levels);

        // Zero the bitmap then set the root node free (bit 0 of the first word).
        for (int w = 0; w < OsLayout.BuddyBitmapWords; w++)
        {
            WriteWord(hw, OsLayout.BuddyBitmapOffset + w * 4, 0);
        }
        WriteWord(hw, OsLayout.BuddyBitmapOffset, 1); // root = free
    }

    // Returns the largest power of 2 that is <= n. Assumes n > 0.
    private static int LargestPowerOfTwoFitting(int n)
    {
        int p = 1;
        while (p * 2 <= n)
        {
            p *= 2;
        }
        return p;
    }

    // Returns floor(log2(n)). Assumes n is a positive power of 2.
    private static int Log2(int n)
    {
        int k = 0;
        while (n > 1)
        {
            n >>= 1;
            k++;
        }
        return k;
    }

    // ---- process loading -------------------------------------------------
    // Resolves the program's disk slot (auto-staging a file-path process on first
    // load), runs the ISA allocator — which now also DREADs the image from disk into
    // RAM — then seeds the process-table entry and marks it Ready for the scheduler.
    public void LoadProcess(Process process)
    {
        if (hardware == null)
        {
            throw new InvalidOperationException("LoadProcess requires an attached hardware.");
        }
        Hardware hw = hardware;

        // Resolve the program's disk slot. A file-path process auto-stages its bytes
        // to a slot the first time it loads, so the image always originates on disk;
        // a slot-based process already references one.
        int diskSlot = process.ProgramSlot;
        if (diskSlot < 0)
        {
            byte[] fileBytes = File.ReadAllBytes(process.ProgramFilePath);
            diskSlot = hw.Disk.Store(fileBytes);
            if (diskSlot < 0)
            {
                log.WriteLine($"[LOAD FAILED] Disk full: {process.ProgramFilePath}");
                return;
            }
            process.ProgramSlot = diskSlot;
        }

        int programLength = hw.Disk.GetLength(diskSlot);

        // The memory region must hold the register-file save block plus the mode slot.
        int minMemory = hw.GetRegisterFileSize() + 4;
        if (process.RequiredMemory < minMemory)
        {
            process.RequiredMemory = minMemory;
        }

        int kernelSection = Hardware.KernelHeaderSize + KernelImage.Length;
        int total = programLength + kernelSection + process.RequiredMemory + process.RequiredStackSize + Hardware.KernelStackSize;

        int slot = FindFreeSlot(hw);
        if (slot < 0)
        {
            log.WriteLine($"[LOAD FAILED] Process table full: {ProcessName(process, diskSlot)}");
            return;
        }

        int entry = OsLayout.ProcessEntryAddress(slot);
        ClearEntry(hw, entry);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramSize, programLength);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredMemory, process.RequiredMemory);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredStackSize, process.RequiredStackSize);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, total);
        WriteWord(hw, entry + Hardware.ProcessEntryDiskSlot, diskSlot);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Terminated); // placeholder until allocated

        // Create the process in ISA: IvtSpawn allocates the region, DREADs the image
        // from disk, and seeds the saved register file (EIP/ESP offsets), scheduling
        // state, and a fresh monotonic PID. It sets ProgramAddress = -1 if no memory
        // was available.
        hw.RunOsRoutineSynchronously(Hardware.IvtSpawn, entry);
        int programAddress = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        if (programAddress < 0)
        {
            log.WriteLine($"[LOAD FAILED] Not enough memory for process: {ProcessName(process, diskSlot)}");
            return;
        }

        // The program image is now in RAM; place the per-process kernel section after it.
        if (KernelImage.Length > 0)
        {
            hw.WriteBytes(programAddress + programLength + Hardware.KernelHeaderSize, KernelImage);
        }

        // Read back the PID the spawn routine assigned.
        process.Pid = ReadWord(hw, entry + Hardware.ProcessEntryPid);

        // Seed file descriptors: stdin (0) and stdout (1) point at the process's own
        // device (device id == slot index, the behavior-preserving shim). The focus
        // effort later rebinds these to a shared keyboard/screen.
        WriteWord(hw, entry + Hardware.ProcessEntryFdTable + Hardware.StdIn * 4, slot);
        WriteWord(hw, entry + Hardware.ProcessEntryFdTable + Hardware.StdOut * 4, slot);

        // Grow the table high-water mark when this is a fresh slot.
        int count = ReadWord(hw, OsLayout.ProcessCountOffset);
        if (slot == count)
        {
            WriteWord(hw, OsLayout.ProcessCountOffset, count + 1);
        }

        // Keep the C# descriptor and the name map in sync for callers/observers.
        process.ProgramAddress = programAddress;
        process.ProgramSize = programLength;
        process.RegisterStateAddress = programAddress + programLength + kernelSection;
        process.InstructionPointer = programAddress;
        namesByBase[programAddress] = ProcessName(process, diskSlot);
    }

    // A display name for a process: its program file path, or a disk-slot label when
    // it was created slot-based (no file path).
    private static string ProcessName(Process process, int diskSlot)
    {
        if (string.IsNullOrEmpty(process.ProgramFilePath))
        {
            return $"slot {diskSlot}";
        }
        return process.ProgramFilePath;
    }

    // Resolves the program name for a context-switch event's program base.
    public string? NameForBase(int programBase)
    {
        if (namesByBase.TryGetValue(programBase, out string? name))
        {
            return name;
        }
        return null;
    }

    // ---- helpers ---------------------------------------------------------

    // Finds a reusable (Terminated) slot, else the next fresh slot, else -1 if full.
    private int FindFreeSlot(Hardware hw)
    {
        int count = ReadWord(hw, OsLayout.ProcessCountOffset);
        for (int i = 0; i < count; i++)
        {
            int state = ReadWord(hw, OsLayout.ProcessEntryAddress(i) + Hardware.ProcessEntryState);
            if (state == (int)ProcessState.Terminated)
            {
                return i;
            }
        }
        if (count < OsLayout.MaxProcesses)
        {
            return count;
        }
        return -1;
    }

    private int CountLiveProcesses()
    {
        if (hardware == null)
        {
            return 0;
        }
        int count = ReadWord(hardware, OsLayout.ProcessCountOffset);
        int live = 0;
        for (int i = 0; i < count; i++)
        {
            int state = ReadWord(hardware, OsLayout.ProcessEntryAddress(i) + Hardware.ProcessEntryState);
            if (state != (int)ProcessState.Terminated)
            {
                live++;
            }
        }
        return live;
    }

    private static void ClearEntry(Hardware hw, int entry)
    {
        hw.WriteBytes(entry, new byte[Hardware.ProcessEntrySize]);
    }

    private static int ReadWord(Hardware hw, int address)
    {
        byte[] b = hw.ReadBytes(address);
        return b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
    }

    private static void WriteWord(Hardware hw, int address, int value)
    {
        hw.WriteBytes(address, new byte[]
        {
            (byte)(value & 0xFF),
            (byte)((value >> 8)  & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 24) & 0xFF)
        });
    }
}
