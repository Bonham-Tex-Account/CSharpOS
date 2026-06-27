using System.Reflection;

namespace CSharpOS;

/// <summary>
/// Loads an operating system from a plugin assembly. Selected by the <c>--os-plugin</c>
/// CLI flag, it reflects over the DLL for the first concrete
/// <see cref="OperatingSystem"/> subclass with a <c>(TextWriter)</c> constructor and
/// instantiates it.
/// </summary>
public static class OsPluginLoader
{
    /// <summary>
    /// Loads and instantiates the operating system from <paramref name="dllPath"/>,
    /// passing <paramref name="log"/> to its constructor.
    /// </summary>
    /// <exception cref="InvalidOperationException">No suitable OperatingSystem subclass was found.</exception>
    public static OperatingSystem Load(string dllPath, TextWriter log)
    {
        Assembly assembly = Assembly.LoadFrom(dllPath);
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }
            if (!typeof(OperatingSystem).IsAssignableFrom(type))
            {
                continue;
            }
            ConstructorInfo? ctor = type.GetConstructor(new Type[] { typeof(TextWriter) });
            if (ctor == null)
            {
                continue;
            }
            return (OperatingSystem)ctor.Invoke(new object[] { log });
        }
        throw new InvalidOperationException($"No OperatingSystem subclass found in {dllPath}");
    }
}
