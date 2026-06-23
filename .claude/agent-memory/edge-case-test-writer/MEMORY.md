# Memory Index

- [CSharpOS MLFQ Boost Loop Bug](mlfq_boost_loop_bug.md) — Confirmed off-by-one in EmitContextSwitch boost loop: iterates i=0..count instead of 0..count-1; ghost slot[count] gets written
- [CSharpOS Architecture](csos_architecture.md) — ISA OS emulator: OsLayout, ProcessEntry fields, IVT, MLFQ scheduler, ISA code paths
- [Test Patterns](feedback_test_patterns.md) — Isolation test pattern: NewHardware+FakeOS+ReserveOsMemory+WriteBytes(BuildOsImage)+seed+DispatchOsRoutine+RunRoutine
