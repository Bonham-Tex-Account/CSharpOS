---
name: feedback-test-all-cases
description: "When writing tests, cover all cases including edge cases that may fail — never avoid a case to keep the suite green"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 80dde628-8cf8-4b55-b3dd-f36e5d0584b0
---

When writing tests, exercise ALL cases — including edge cases and ones likely to fail or reveal bugs. Do not steer tests around suspected bugs to keep the suite green. Avoiding a case is pointless; a failing test that surfaces a real bug is the goal.

**Why:** User explicitly emphasized this ("REMEMBER THIS"). Tests exist to discover problems, which aligns with the project goal of discovering problems naturally. [[project-csharpos]] [[feedback-design-decisions]]
**How to apply:** Write tests asserting intended/correct behavior for every case, including known-risky edges (e.g. divide-by-zero, empty collections, uninitialized state). Let them fail if the code is wrong; report failures, do not fix production code unless asked.
