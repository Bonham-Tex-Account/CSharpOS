---
name: project-visualizer-improvements
description: Active work on VisualizerImprovements branch — fixes and enhancements to the Spectre.Console dashboard
metadata: 
  node_type: memory
  type: project
  originSessionId: 501efc32-3af8-4c94-8d16-a5adac7686c3
---

Active branch: `VisualizerImprovements` (off master, clean slate as of 2026-06-30).

**Why:** The visualizer had accumulated small bugs and the branch was repurposed after Claude workflow improvements were merged to master.

**Fixes completed (uncommitted, 2026-06-30):**
1. **UTF-8 encoding** (`Program.cs`) — `Console.OutputEncoding = Encoding.UTF8` + `Console.InputEncoding = Encoding.UTF8` added at startup; fixes `?` replacing Unicode/box-drawing characters on Windows.
2. **Zombie focus bug** (`SpectreDashboard.cs:LiveProcessIndices`) — was only excluding `ProcessState.Terminated`, now also excludes `ProcessState.Zombie`; focus no longer sticks to a dead process that can't receive input.

**Menu additions (uncommitted, 2026-06-30):**
- Option 10: Two guessing games side-by-side for testing Tab focus switching between processes.

**Next:** More visualizer issues to investigate — user is testing process switching via option 10.

**How to apply:** When continuing visualizer work, check these fixes are committed before adding new ones.
