---
name: feedback-github-workflow
description: Always check for existing issues/PRs before creating new ones to avoid duplicates
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 501efc32-3af8-4c94-8d16-a5adac7686c3
---

Before creating any GitHub issue or PR, run `gh issue list --state all` and `gh pr list --state all` to check for existing ones covering the same topic.

**Why:** Created duplicate issue #26 when #25 "Improve Claude workflow efficiency" already existed.

**How to apply:** Any time the user asks to create an issue or PR — check first, then either create new or reference the existing one. If an existing issue is close but not identical, mention it to the user and ask whether to reuse it or create a separate one.
