---
name: internal-types-not-directly-testable
description: Internal types in BasicOSPlugin (trap providers) cannot be instantiated directly from OSTests — test through public surface (BasicOS + Hardware) instead
metadata:
  type: feedback
---

Provider classes like `IretTrapProvider`, `LoadBoundsTrapProvider`, `StoreBoundsTrapProvider` are `internal sealed` in the `CSharpOS` namespace within `BasicOSPlugin`. There is no `InternalsVisibleTo` attribute.

**Why:** They are implementation details of the plugin, not part of the public API. Tests that try to `new IretTrapProvider()` from OSTests will fail to compile.

**How to apply:** Always exercise these providers indirectly — construct a `BasicOS`, attach it to a `Hardware`, set privilege level and register state, then call `Instruction.Execute` or `hw.EvaluateTraps` and observe the `InvalidInstruction` event. The `InvalidInstructionArgs.Opcode` and `InvalidInstructionArgs.Reason` fields expose the trap's metadata.
