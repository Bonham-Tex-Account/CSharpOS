---
name: project-buddy-seeding
description: SeedOsData math invariants and test patterns for buddy allocator initialization in OperatingSystem.cs
metadata:
  type: project
---

SeedOsData (called from AttachHardware) computes heapSize = LargestPowerOfTwoFitting(machineSize - OsMemorySize), then levels = Log2(heapSize / minBlock). It seeds CurrentIndexOffset = -1, ProcessCountOffset = 0, BoostTimer = BoostInterval, QuantumTable = {1,2,4,255}, root bit = 1.

**Why:** The buddy allocator migration replaced a flat free list with a 1-indexed binary tree. All ISA routines read heapStart/heapSize/levels/minBlock from OS memory on every call, so a single wrong seeded value breaks all allocations silently.

**How to apply:** When testing SeedOsData, read back every seeded field from hardware memory and assert exact values. Key boundary cases: available exactly a power of two (LargestPowerOfTwo returns it unchanged), available = power+1 (rounds down), available = power-1 (returns half). levels = log2(heapSize/minBlock); for MinMachineSize (TotalSize+4096) this gives levels=4, leafCount=16.

Key finding: LargestPowerOfTwoFitting(0) returns 1 (not infinite), and Log2(0) returns 0 (not crash). So machineSize == OsMemorySize yields a degenerate 1-byte heap — no test covered this, it is not explicitly guarded, but it does not crash.
