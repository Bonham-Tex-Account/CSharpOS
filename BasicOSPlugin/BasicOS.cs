using System.Reflection;

namespace CSharpOS;

/// <summary>
/// The reference operating system, loaded as a plugin by reflection. It ships the
/// syscall (kernel) image of I/O handlers and runs its scheduler, allocator, and the
/// spawning family as ISA code in the reserved OS region (see <see cref="OsRoutines"/>).
/// Its traps are gathered from every <see cref="ITrapProvider"/> in this assembly.
/// </summary>
public class BasicOS : OperatingSystem
{
    // ---- public properties -----------------------------------------------
    /// <summary>Size of the reserved OS region that holds the ISA scheduler/allocator and OS data.</summary>
    public override int OsMemorySize => OsLayout.TotalSize;
    /// <summary>Builds the OS image (IVT + assembled routines + zeroed data section).</summary>
    public override byte[] BuildOsImage(int osMemoryBase) => OsRoutines.BuildOsImage();

    /// <summary>Programs are installed into the FS at load and run FS-backed (Phase 4);
    /// BasicOS formats an FS on boot, so the filesystem is always available for the install.</summary>
    protected override bool UsesFilesystemBoot => true;

    // ---- constructor -----------------------------------------------------
    /// <summary>
    /// Creates the OS, collecting its traps by reflection. The <paramref name="log"/>
    /// receives load diagnostics. This (TextWriter) constructor is the signature the
    /// plugin loader looks for.
    /// </summary>
    public BasicOS(TextWriter log) : base(CollectTraps(), log)
    {
    }

    // ---- boot ------------------------------------------------------------

    /// <summary>
    /// Formats the filesystem on first boot so the FS is usable without a caller running the
    /// format op by hand. Skipped when the disk's superblock already carries the FS magic — so
    /// a persisted disk loaded from a .bin keeps its files rather than being wiped. The magic is
    /// read from the disk (not the cache), which is empty on a fresh machine.
    /// </summary>
    protected override void OnBooted(Hardware hw)
    {
        if (hw.Disk.FileBlockCount <= 0)
        {
            return;
        }
        byte[] superBlock = hw.Disk.ReadFileBlock(FsLayout.SuperBlock);
        int magic = superBlock[FsLayout.SuperMagicOffset] | (superBlock[FsLayout.SuperMagicOffset + 1] << 8);
        if (magic == FsLayout.SuperMagic)
        {
            return;
        }
        hw.RunOsRoutineSynchronously(Hardware.IvtFsOp, Hardware.FsOpFormat);
    }

    // ---- helper functions ------------------------------------------------

    // Discovers all ITrapProvider implementations in this assembly via reflection
    // and collects their traps, so new trap handlers can be added without touching
    // a manual registration list.
    private static List<Trap> CollectTraps()
    {
        List<Trap> traps = new List<Trap>();
        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!type.IsAbstract && typeof(ITrapProvider).IsAssignableFrom(type))
            {
                ITrapProvider provider = (ITrapProvider)Activator.CreateInstance(type)!;
                traps.Add(provider.GetTrap());
            }
        }
        return traps;
    }
}
