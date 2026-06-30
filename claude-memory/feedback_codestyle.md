---
name: feedback-codestyle
description: "Code style preferences for this project — no curly-brace-less control flow, no var, no ternary expressions"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 09a94611-7bfe-4d15-a526-9337cb287bbc
---

Always use curly braces for all loops and conditionals, even single-line bodies. Never use `var` — always use explicit types. Do not use the ternary (`?:`) operator — use a full `if`/`else` statement (assign to a local declared beforehand, or return early) instead. The user asked to convert all existing ternaries to if statements (2026-06-20), so prefer if-statements in new code too.

**Why:** User's explicit preference for this project.
**How to apply:** Apply to all C# code written in this project. Fix existing violations when asked.
