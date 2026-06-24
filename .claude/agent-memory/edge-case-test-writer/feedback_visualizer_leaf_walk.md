---
name: feedback-visualizer-leaf-walk
description: ConsoleVisualizer leaf-walk behavior: initial state shows (none) because only root bit is set; leaf bits only exist after alloc splits or free merges
metadata:
  type: feedback
---

RenderFreeMemoryIfChanged walks ONLY leaf nodes (firstLeaf..firstLeaf+leafCount-1). The initial seeded state has only the root bit (bit 0) set — no leaf bits are set. So before any allocation, the map always reads "(none)" even though the whole heap is free.

**Why:** The visualizer does not propagate ancestor free-bits down; it reads leaf bits verbatim. This is by design — the visual represents the granularity at which blocks are actually available to processes (min-block-sized chunks), not the buddy tree's logical "free at root" state.

**How to apply:** When testing the free map, never assert a range appears for the initial state. Only assert ranges after at least one allocation-and-free cycle, which splits the root into leaf-level bits. After a full cascade merge back to root, leaf bits are cleared again and the map returns to "(none)".
