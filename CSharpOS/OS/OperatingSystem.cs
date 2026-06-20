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

    // Initialises the OS data structures: no current process, an empty process
    // table, and a single free range covering all memory above the OS region.
    private void SeedOsData(Hardware hw)
    {
        WriteWord(hw, OsLayout.ProcessCountOffset, 0);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        WriteWord(hw, OsLayout.PendingCountOffset, 0);
        WriteWord(hw, OsLayout.FreeRangeCountOffset, 1);
        WriteWord(hw, OsLayout.FreeRangeTableOffset, OsMemorySize);
        WriteWord(hw, OsLayout.FreeRangeTableOffset + 4, hw.GetMemorySize() - OsMemorySize);
    }

    // ---- process loading -------------------------------------------------
    // Reads the program, runs the ISA allocator to reserve memory, then seeds a
    // process-table entry (image bytes, kernel section, initial registers) and marks
    // it Ready for the scheduler.
    public void LoadProcess(Process process)
    {
        if (hardware == null)
        {
            throw new InvalidOperationException("LoadProcess requires an attached hardware.");
        }
        Hardware hw = hardware;

        byte[] program = File.ReadAllBytes(process.ProgramFilePath);

        // The memory region must hold the register-file save block plus the mode slot.
        int minMemory = hw.GetRegisterFileSize() + 4;
        if (process.RequiredMemory < minMemory)
        {
            process.RequiredMemory = minMemory;
        }

        int kernelSection = Hardware.KernelHeaderSize + KernelImage.Length;
        int total = program.Length + kernelSection + process.RequiredMemory + process.RequiredStackSize + Hardware.KernelStackSize;

        int slot = FindFreeSlot(hw);
        if (slot < 0)
        {
            log.WriteLine($"[LOAD FAILED] Process table full: {process.ProgramFilePath}");
            return;
        }

        int entry = OsLayout.ProcessEntryAddress(slot);
        ClearEntry(hw, entry);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramSize, program.Length);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredMemory, process.RequiredMemory);
        WriteWord(hw, entry + Hardware.ProcessEntryRequiredStackSize, process.RequiredStackSize);
        WriteWord(hw, entry + Hardware.ProcessEntryTotalSize, total);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Terminated); // placeholder until allocated

        // First-fit allocation runs as ISA code; it sets ProgramAddress or -1 on failure.
        hw.RunOsRoutineSynchronously(Hardware.IvtLoadProcess, entry);
        int programAddress = ReadWord(hw, entry + Hardware.ProcessEntryProgramAddress);
        if (programAddress < 0)
        {
            log.WriteLine($"[LOAD FAILED] Not enough memory for process: {process.ProgramFilePath}");
            return;
        }

        // Place the program image and the per-process kernel section.
        hw.WriteBytes(programAddress, program);
        if (KernelImage.Length > 0)
        {
            hw.WriteBytes(programAddress + program.Length + Hardware.KernelHeaderSize, KernelImage);
        }

        // Seed the saved register file: start at the program, with ESP at the top of
        // the user stack, in User mode and Ready to run.
        int userStackTop = programAddress + program.Length + kernelSection + process.RequiredMemory + process.RequiredStackSize;
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.EIP), programAddress);
        WriteWord(hw, entry + hw.GetRegisterOffset(RegisterName.ESP), userStackTop);
        WriteWord(hw, entry + Hardware.ProcessEntryLevel, (int)PrivilegeLevel.User);
        WriteWord(hw, entry + Hardware.ProcessEntryWaitReason, (int)WaitReason.None);
        WriteWord(hw, entry + Hardware.ProcessEntryState, (int)ProcessState.Ready);

        // Grow the table high-water mark when this is a fresh slot.
        int count = ReadWord(hw, OsLayout.ProcessCountOffset);
        if (slot == count)
        {
            WriteWord(hw, OsLayout.ProcessCountOffset, count + 1);
        }

        // Keep the C# descriptor and the name map in sync for callers/observers.
        process.ProgramAddress = programAddress;
        process.ProgramSize = program.Length;
        process.RegisterStateAddress = programAddress + program.Length + kernelSection;
        process.InstructionPointer = programAddress;
        namesByBase[programAddress] = process.ProgramFilePath;
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
