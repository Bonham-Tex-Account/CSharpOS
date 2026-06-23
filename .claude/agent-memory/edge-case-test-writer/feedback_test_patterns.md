---
name: feedback-test-patterns
description: Validated test patterns for CSharpOS: isolation (FakeOS+OsRoutines), end-to-end (BasicOS+LoadProcess), visualizer (NewRun helper)
metadata:
  type: feedback
---

## Isolation tests (MlfqTests / OsContextSwitchRoutineTests pattern)
Use `Test.NewHardware(8192, new FakeOS())` + `ReserveOsMemory` + `WriteBytes(BuildOsImage())`.
SeedMlfqDefaults for quantum table + boost timer. Use WriteWord helper for header fields.
Use SeedEntry helper (state, level, priority, ticksUsed, programAddress). Call
`DispatchOsRoutine` then `RunRoutine` (step until !Privileged).

**Why:** FakeOS has no kernel image so no process sections exist; memory is flat. Entry fields
beyond the register file (State, Priority, etc.) survive SaveRegs because SaveRegs only writes
bytes 0..99 (register file + level).

## End-to-end tests (BasicOS)
Use `BasicOS os = new BasicOS(new StringWriter())` + `Test.NewHardware(16384, os)`.
Call `os.LoadProcess(new Process(filePath, 128, 64))`. Run with `hw.Run()` in a loop.
Add `hw.ProgramOutput += (_, e) => hw.RaiseOutputComplete(e.Device)` for non-blocking output.

## Visualizer tests (ConsoleVisualizerTests)
Use `NewRun(VisualizerMode)` helper which wires up BasicOS + Hardware + StringWriter sink.
Check `sink.ToString()` for rendered text. Always pass `useColor: false, interactive: false`.

## Sentinel patterns
Pre-seed memory beyond active bounds with distinct non-zero values. Mark target slots
as `ProcessState.Ready` (not Terminated) so the boost loop body doesn't skip them.
Use `unchecked((int)0xDEADBEEF)` for overflow-safe hex sentinels.

**How to apply:** For any "does not write outside boundary" test: place the sentinel at the
exact address the bug would write to, not just a nearby address. Calculate: ghost_addr =
ProcessEntryAddress(count) + fieldOffset.
