---
name: mlfq-boost-loop-bug
description: Confirmed off-by-one in EmitContextSwitch cs_boost_loop: exits when count-i < 0 (Js) rather than count-i <= 0 (Jz+Js), so i runs 0..count instead of 0..count-1
metadata:
  type: project
---

In `OsRoutines.EmitContextSwitch`, the periodic boost loop uses `asm.Js("cs_boost_done")` on
`CMP(count, i)`. This fires only when `count - i < 0` (i.e., `i > count`), so the loop body
also executes when `i == count` — one iteration past the end of the valid process table.

**Effect:** The "ghost" entry at index `count` gets its Priority and TicksUsed fields zeroed
if its State word (at whatever memory happens to be there) reads as non-Terminated (0 != 2).

**When it hurts:**
- count = 1: ghost at slot 1, still inside the 8-slot process table region — low impact.
- count = 2: ghost at slot 2, still inside the table.
- count = MaxProcesses=8: ghost at FreeRangeTableOffset. ProcessEntryPriority (128) and
  ProcessEntryTicksUsed (132) bytes INTO FreeRangeTableOffset land at PendingQueueOffset+0
  and PendingQueueOffset+4, corrupting the pending queue.

**Fix:** Replace `asm.Js("cs_boost_done")` with `asm.Jz("cs_boost_done"); asm.Js("cs_boost_done")`
(exit when i >= count, not just i > count).

**Tests added (MlfqTests.cs):**
- `ContextSwitch_PeriodicBoost_DoesNotWriteBeyondProcessTable` — WILL FAIL with current code
- `ContextSwitch_PeriodicBoost_WithTwoProcesses_DoesNotWriteToSlotTwo` — WILL FAIL
- `ContextSwitch_PeriodicBoost_WithOneProcess_DoesNotWriteToSlotOne` — WILL FAIL

**Why:** `Js` = "jump if sign", fires when result is negative. For "exit loop when i >= count",
we need to fire on both negative AND zero results. `Jz` handles the zero case.
**How to apply:** Any loop in OsRoutines that exits via `Js` on a `CMP(count, i)` pattern is
suspect. Always check: does the exit fire for `i == count`?
