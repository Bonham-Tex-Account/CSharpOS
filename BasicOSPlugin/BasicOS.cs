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

    // ---- constructor -----------------------------------------------------
    /// <summary>
    /// Creates the OS, collecting its traps by reflection. The <paramref name="log"/>
    /// receives load diagnostics. This (TextWriter) constructor is the signature the
    /// plugin loader looks for.
    /// </summary>
    public BasicOS(TextWriter log) : base(CollectTraps(), log)
    {
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
