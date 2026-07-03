// os-facts dumper: prints ground-truth constants straight from the compiled CSharpOS types,
// so the layout / IVT / process-entry / paging / FS numbers never have to be read out of a
// (drift-prone) CLAUDE.md table or recomputed by hand. Reflection over the referenced types
// auto-discovers every `public const int`, so newly-added constants appear with no edits here.
//
// Usage: dotnet run --project .claude/skills/os-facts/dump -- [section]
//   section = layout | ivt | entry | paging | fs | all (default: all)

using System.Reflection;
using CSharpOS;

string section = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "all";

void Dump(string title, Type type, Func<string, bool>? filter = null)
{
    List<(string Name, int Value)> fields = type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(int))
        .Where(f => filter == null || filter(f.Name))
        .Select(f => (f.Name, (int)f.GetRawConstantValue()!))
        .OrderBy(x => x.Item2)
        .ThenBy(x => x.Item1)
        .ToList();

    Console.WriteLine($"== {title} ({fields.Count}) ==");
    foreach ((string name, int value) in fields)
    {
        Console.WriteLine($"  {value,10}  {name}");
    }
    Console.WriteLine();
}

bool Want(string s) => section == "all" || section == s;

if (Want("layout"))
{
    // OsLayout constants are absolute offsets from the OS-image base (they already fold in
    // DataBase); sorted by value they read as a memory map from the IVT up to TotalSize.
    Dump("OsLayout (absolute offsets; sorted = memory map)", typeof(OsLayout));
}
if (Want("ivt"))
{
    Dump("Hardware IVT slots", typeof(Hardware), n => n.StartsWith("Ivt"));
}
if (Want("entry"))
{
    Dump("Hardware ProcessEntry field offsets", typeof(Hardware), n => n.StartsWith("ProcessEntry"));
}
if (Want("paging"))
{
    // Everything else on Hardware that isn't an IVT slot or a ProcessEntry offset: paging,
    // disk geometry, kernel-stack, device ids, cache/fs op selectors, key codes, etc.
    Dump("Hardware other constants (paging / disk / kernel-stack / op selectors / ...)",
        typeof(Hardware), n => !n.StartsWith("Ivt") && !n.StartsWith("ProcessEntry"));
}
if (Want("fs"))
{
    Dump("FsLayout (on-disk filesystem structure)", typeof(FsLayout));
}
