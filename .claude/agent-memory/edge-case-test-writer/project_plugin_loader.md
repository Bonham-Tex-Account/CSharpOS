---
name: plugin-loader-architecture
description: OsPluginLoader uses Assembly.LoadFrom + Activator.CreateInstance(type, new object[]{log}); BasicOS uses CollectTraps reflection to discover ITrapProvider implementations
metadata:
  type: project
---

`OsPluginLoader.Load(string dllPath, TextWriter log)` finds the first non-abstract, non-interface `OperatingSystem` subclass in the assembly and instantiates it with `Activator.CreateInstance(type, new object[] { log })`. If no match is found, throws `InvalidOperationException`.

`BasicOS.CollectTraps()` uses `Assembly.GetExecutingAssembly().GetTypes()` to discover all non-abstract `ITrapProvider` implementations, instantiates each via `Activator.CreateInstance(type)` (parameterless), calls `GetTrap()`, and returns the list.

**Why:** Plugin architecture — OS implementations live in separate assemblies loaded at runtime.

**How to apply:** When loading, `null` path throws `ArgumentNullException`, empty path throws `ArgumentException`, missing file throws `FileNotFoundException`, valid DLL with no subclass throws `InvalidOperationException`. The test assembly (`OSTests.dll`) contains `TrappingOS` and `KernelImageOS` which ARE `OperatingSystem` subclasses but have mismatched constructors — use `CSharpOS.dll` (no concrete subclass) for the "no OperatingSystem subclass" test case instead.

See [[internal-types-not-directly-testable]] for provider testing constraints.
