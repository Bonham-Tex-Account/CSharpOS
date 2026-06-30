---
name: feedback-efficiency-tracking
description: "At the end of each session, log token usage in the Session Cost Log table in root CLAUDE.md"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 501efc32-3af8-4c94-8d16-a5adac7686c3
---

At the end of every session, update the **Session Cost Log** table in `CLAUDE.md` (root of the project) with a one-line entry: date, task description, token estimate, and any notes about what drove the cost.

**Why:** The user wants to notice efficiency regressions early — if a task suddenly costs much more than expected, it signals something is being re-scanned that should be in CLAUDE.md or section markers instead.

**How to apply:**
- Fork/agent token counts come from the task notification (`subagent_tokens` field).
- Inline work: estimate based on context (large file reads = ~2–5K tokens each; planning sessions = ~10–30K typical).
- Red flag threshold: >50K tokens for a single planning/implementation task → investigate what was re-scanned.
- If a new pattern of re-scanning emerges, add the missing info to the relevant CLAUDE.md file or section markers rather than accepting the cost repeatedly.
