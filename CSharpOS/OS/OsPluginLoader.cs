using System.Reflection;

namespace CSharpOS;

public static class OsPluginLoader
{
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
