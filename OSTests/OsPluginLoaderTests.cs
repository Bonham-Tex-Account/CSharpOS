using CSharpOS;
using OperatingSystem = CSharpOS.OperatingSystem;

namespace OSTests;

public class OsPluginLoaderTests
{
    private static string PluginPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(typeof(OsPluginLoaderTests).Assembly.Location)!,
            "BasicOSPlugin.dll"
        );
    }

    [Fact]
    public void Load_ReturnsOperatingSystemInstance()
    {
        OperatingSystem os = OsPluginLoader.Load(PluginPath(), TextWriter.Null);

        Assert.NotNull(os);
    }

    [Fact]
    public void Load_RunsSimpleProcessToCompletion()
    {
        string programPath = Path.GetTempFileName();
        Assembler asm = new Assembler();
        asm.Hlt();
        File.WriteAllBytes(programPath, asm.Build(0));

        OperatingSystem os = OsPluginLoader.Load(PluginPath(), TextWriter.Null);
        Hardware hw = new Hardware(4096, Enum.GetValues<RegisterName>(), os);
        os.LoadProcess(new Process(programPath, 128, 64));

        int maxSteps = 10000;
        int steps = 0;
        while (os.HasProcesses && steps < maxSteps)
        {
            hw.Run();
            steps++;
        }

        Assert.False(os.HasProcesses);
    }

    [Fact]
    public void Load_ThrowsWhenDllNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            OsPluginLoader.Load(Path.GetFullPath("NonExistent.dll"), TextWriter.Null));
    }

    // EDGE CASE: null path — Assembly.LoadFrom(null) throws ArgumentNullException,
    // not FileNotFoundException. Loader must not hide the wrong exception type.
    [Fact]
    public void Load_NullPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OsPluginLoader.Load(null!, TextWriter.Null));
    }

    // EDGE CASE: empty string path — Assembly.LoadFrom("") throws ArgumentException.
    [Fact]
    public void Load_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            OsPluginLoader.Load(string.Empty, TextWriter.Null));
    }

    // EDGE CASE: valid DLL that contains no OperatingSystem subclass must throw
    // InvalidOperationException, not silently return null.
    [Fact]
    public void Load_DllWithNoOperatingSystemSubclass_ThrowsInvalidOperationException()
    {
        // CSharpOS.dll contains only the abstract OperatingSystem base class; there is
        // no concrete subclass in it, so the loader must throw InvalidOperationException.
        string coreAssemblyPath = Path.Combine(
            Path.GetDirectoryName(typeof(OsPluginLoaderTests).Assembly.Location)!,
            "CSharpOS.dll"
        );

        Assert.Throws<InvalidOperationException>(() =>
            OsPluginLoader.Load(coreAssemblyPath, TextWriter.Null));
    }

    // EDGE CASE: calling Load twice with the same path. Assembly.LoadFrom returns
    // the cached assembly on the second call; the loader must not fault and must
    // return a distinct, valid instance each time.
    [Fact]
    public void Load_CalledTwiceWithSamePath_ReturnsTwoSeparateInstances()
    {
        // Arrange + Act
        OperatingSystem first = OsPluginLoader.Load(PluginPath(), TextWriter.Null);
        OperatingSystem second = OsPluginLoader.Load(PluginPath(), TextWriter.Null);

        // Assert: both are non-null and are not the same object reference
        // (each call must produce a fresh instance).
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    // EDGE CASE: the returned instance must be the concrete BasicOS type, not just
    // any OperatingSystem subclass. This guards against a future assembly containing
    // multiple subclasses where the loader accidentally picks the wrong one first.
    [Fact]
    public void Load_ReturnsConcreteBasicOsType()
    {
        OperatingSystem os = OsPluginLoader.Load(PluginPath(), TextWriter.Null);

        Assert.Equal("BasicOS", os.GetType().Name);
    }
}
