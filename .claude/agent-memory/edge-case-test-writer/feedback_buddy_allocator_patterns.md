---
name: buddy-allocator-patterns
description: Buddy allocator audit findings and test patterns for CSharpOS ISA-emulated OS
metadata:
  type: feedback
---

## Confirmed behaviors to test (from ISA-level analysis)

**Zero and sub-MinBlock requests:** The level-find loop condition `blockSize/2 < needed` is never
true for `needed <= 0` (since blockSize/2 is always positive) so alloc(0) and alloc(1) both clamp
at BuddyLevels and return a MinBlock-sized leaf — they do NOT fail. This is documented behavior.

**Root-level alloc (full heap):** When needed == heapSize, blockSize/2 is immediately < needed, so
ESI=0 and the root node (1) is consumed directly. Free path: at level 0, bf_merge immediately
exits via `Cmp(R8,0)→Jz(bf_done)`, but SetBit already ran — root is correctly left free.

**Non-leaf alloc (heapSize/2):** Allocator finds root free at level 0, splits it (ClearBit root,
SetBit right child), allocates left child. Free path correctly identifies level 1, sets the
freed node's bit, finds buddy free, and merges all the way to root.

**Alloc/free idempotence:** After alloc→free→alloc cycle, the second alloc lands at the same
leftmost-leaf address. Bitmap returns to exactly the post-first-alloc state.

**BuddyFree guards:** Two guards at entry: heapSize==0 → skip all bitmap work. TotalSize==0 → skip
all bitmap work. Both prevent divide-by-zero in the block_j computation. Test both by zeroing
the field after alloc.

**Word-boundary bitmap access (node 33):** With a 6-level tree (machineSize=21836), node 33 sits
at bitPos=32 (word 1, bit 0) — the first cross-word boundary node. Verified that SetBit, ClearBit,
and ReadBit all correctly read/write word 1 rather than word 0. Test setup: alloc 0 (leaf 64)
produces node 33 as a free right-sibling; alloc 2 splits node 33 and clears it.

**Manually freed leaf:** The allocator scans all bits unconditionally, so a leaf bit manually set
(simulating external reclaim or corruption) is picked up by the next alloc. The allocated address
will be heapStart + leafIndex * MinBlock.

**FullHeap fill + partial free + re-alloc:** With FullHeapMachineSize (3 levels, 8 leaves), after
filling all leaves and freeing one via Halt, the next alloc finds that specific freed leaf's bit
(buddy is not free, so no merge occurred) and reuses it.

## Test infrastructure notes

- 6-level tree: machineSize = OsLayout.TotalSize + 64 * MinBlock = 5452 + 16384 = 21836
- BuddyBitmapWords=8 supports up to 255 nodes (7 levels) without overflow
- For free tests: set ProcessCountOffset, CurrentIndexOffset, ProcessEntryState before DispatchOsRoutine(IvtHalt)
- For alloc tests: RunOsRoutineSynchronously — ProcessCountOffset not needed (alloc doesn't read it)
- After any free, SeedHaltState must be called to seed QuantumTableOffset and BoostTimerOffset so resume_mlfq doesn't crash scanning a null quantum table

## Patterns that DID NOT reveal bugs (behaviors confirmed correct)

- Zero/one byte request: confirmed clamps at MinBlock (by design, not a bug)
- Root alloc and free: confirmed correct bitmap state (no merge at level 0, but SetBit already ran)
- Word-boundary node 33: confirmed all three bit ops work correctly across the boundary
- Both guards (heapSize==0, TotalSize==0): confirmed skip bitmap safely

See [[test-patterns]] for the harness setup pattern.
