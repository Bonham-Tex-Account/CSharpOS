# Memory Index

- [CSharpOS MLFQ Boost Loop Bug](mlfq_boost_loop_bug.md) — Confirmed off-by-one in EmitContextSwitch boost loop: iterates i=0..count instead of 0..count-1; ghost slot[count] gets written
- [CSharpOS Architecture](csos_architecture.md) — ISA OS emulator: OsLayout, ProcessEntry fields, IVT, MLFQ scheduler, ISA code paths
- [Test Patterns](feedback_test_patterns.md) — Isolation test pattern: NewHardware+FakeOS+ReserveOsMemory+WriteBytes(BuildOsImage)+seed+DispatchOsRoutine+RunRoutine
- [Internal Types Not Directly Testable](feedback_internal_types.md) — BasicOSPlugin trap providers are internal; test via BasicOS+Hardware+InvalidInstruction event
- [Plugin Loader Architecture](project_plugin_loader.md) — OsPluginLoader/CollectTraps reflection details; use CSharpOS.dll (not OSTests.dll) for "no subclass" test case
- [Buddy Allocator Patterns](feedback_buddy_allocator_patterns.md) — Confirmed behaviors: zero/1-byte→MinBlock, root alloc/free, word-boundary node 33, guards, idempotent cycles
- [Buddy Seeding Math](project_buddy_seeding.md) — SeedOsData invariants: heapStart=OsMemorySize, heapSize=LargestPowerOfTwo(available), levels=Log2(heapSize/minBlock), root bit=1
- [Visualizer Leaf Walk Behavior](feedback_visualizer_leaf_walk.md) — Initial free map is always "(none)": root bit not visible to leaf walk; ranges appear only after alloc splits root
