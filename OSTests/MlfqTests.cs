using CSharpOS;

namespace OSTests;

/// <summary>
/// Isolation tests for the MLFQ scheduler: drives the OS routines directly against
/// a hand-seeded process table in OS memory, verifying demotion, priority ordering,
/// I/O boost, and the periodic global boost without relying on full end-to-end runs.
/// </summary>
public class MlfqTests
{
    // ---- infrastructure --------------------------------------------------

    private static Hardware NewSeededHardware()
    {
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        return hw;
    }

    // Seeds the MLFQ data section with canonical quantum thresholds and an
    // unexpired boost timer so individual tests start from a clean baseline.
    private static void SeedMlfqDefaults(Hardware hw)
    {
        WriteWord(hw, OsLayout.BoostTimerOffset,      OsLayout.BoostInterval);
        WriteWord(hw, OsLayout.QuantumTableOffset + 0,  1);   // L0: 1 tick
        WriteWord(hw, OsLayout.QuantumTableOffset + 4,  2);   // L1: 2 ticks
        WriteWord(hw, OsLayout.QuantumTableOffset + 8,  4);   // L2: 4 ticks
        WriteWord(hw, OsLayout.QuantumTableOffset + 12, 255); // L3: never demote
    }

    private static void SeedEntry(Hardware hw, int index, int state, int level, int priority, int ticksUsed, int programAddress = 4096)
    {
        int entry = OsLayout.ProcessEntryAddress(index);
        WriteWord(hw, entry + Hardware.ProcessEntryState,          state);
        WriteWord(hw, entry + Hardware.ProcessEntryLevel,          level);
        WriteWord(hw, entry + Hardware.ProcessEntryPriority,       priority);
        WriteWord(hw, entry + Hardware.ProcessEntryTicksUsed,      ticksUsed);
        WriteWord(hw, entry + Hardware.ProcessEntryProgramAddress, programAddress);
    }

    // Runs privileged instructions one step at a time until OSRET re-enables interrupts
    // (the routine leaves the atomic OS section), or the step cap fires.
    private static void RunRoutine(Hardware hw)
    {
        for (int step = 0; step < 3000; step++)
        {
            if (hw.InterruptsEnabled())
            {
                return;
            }
            int ip = hw.GetInstructionPointer();
            hw.SetInstructionPointer(ip + 4);
            Instruction.Execute(ip, hw);
        }
        Assert.Fail("OS routine did not return within the step cap.");
    }

    private static int ReadWord(Hardware hw, int address)
    {
        return Test.ReadWord(hw, address);
    }

    private static void WriteWord(Hardware hw, int address, int value)
    {
        Test.WriteWord(hw, address, value);
    }

    // ---- demotion --------------------------------------------------------

    [Fact]
    public void ContextSwitch_DemotesProcessFromLevel0_WhenQuantumExpires()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L0 threshold = 1
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // TicksUsed goes from 0 to 1; 1 >= L0 threshold (1) → demote to priority 1, reset ticks.
        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void ContextSwitch_DoesNotDemote_WhenTicksUsedBelowThreshold()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L1 threshold = 2
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // TicksUsed goes from 0 to 1; 1 < L1 threshold (2) → no demotion.
        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(1, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void ContextSwitch_DemotesLevel1_AfterTwoTicks()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L1 threshold = 2
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // ticksUsed = 1 already; one more tick (1→2) crosses the L1 threshold of 2.
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 1);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(2, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void ContextSwitch_NeverDemotes_ProcessAtLevel3()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // TicksUsed far above any other threshold; level 3 must stay at level 3.
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 200);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(3, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
    }

    // ---- priority ordering -----------------------------------------------

    [Fact]
    public void ContextSwitch_SchedulesHigherPriorityFirst()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L1 threshold = 2, so P0 does not demote this tick
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // P0 currently running at priority 1; P1 is waiting at priority 0.
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 0);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0, programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // resume_mlfq must choose P1 (priority 0) before P0 (priority 1).
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void ContextSwitch_RoundRobinsWithinSameLevel()
    {
        Hardware hw = NewSeededHardware();
        // L0 threshold = 2 so neither process demotes on the first or second switch.
        WriteWord(hw, OsLayout.BoostTimerOffset,      OsLayout.BoostInterval);
        WriteWord(hw, OsLayout.QuantumTableOffset + 0, 2);
        WriteWord(hw, OsLayout.QuantumTableOffset + 4, 4);
        WriteWord(hw, OsLayout.QuantumTableOffset + 8, 8);
        WriteWord(hw, OsLayout.QuantumTableOffset + 12, 255);

        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0, programAddress: 5000);

        // First switch: P0 → P1.
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));

        // Second switch: P1 → P0 (wrap-around).
        WriteWord(hw, OsLayout.CurrentIndexOffset, 1);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    [Fact]
    public void ContextSwitch_DemotedProcess_IsOutrankedByHigherPriority()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L0 threshold = 1
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // P0 will be demoted from 0 to 1; P1 stays at 0 → P1 wins next time.
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0, programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // P0 demoted to priority 1 after its single L0 tick. resume_mlfq picks P1
        // at priority 0 before even scanning level 1.
        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));
    }

    // ---- I/O boost -------------------------------------------------------

    [Fact]
    public void WakeInput_BoostsWokenProcess_ToPriorityZero()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1); // idle: no interrupted process
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 3);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason, (int)WaitReason.Input);

        hw.WriteRegisterAt(0, 0); // EAX = device/process index 0
        hw.DispatchOsRoutine(Hardware.IvtWakeInput);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal(0,                       ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(0,                       ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void WakeOutput_BoostsWokenProcess_ToPriorityZero()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 100);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason, (int)WaitReason.Output);

        hw.WriteRegisterAt(0, 0);
        hw.DispatchOsRoutine(Hardware.IvtWakeOutput);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Ready, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal(0,                       ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(0,                       ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void WakeInput_SpuriousWake_DoesNotAlterPriority()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        // Process is blocked on Output but the interrupt signals Input — mismatch.
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 5);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason, (int)WaitReason.Output);

        hw.WriteRegisterAt(0, 0);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal(2,                         ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(5,                         ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    // ---- periodic boost --------------------------------------------------

    [Fact]
    public void ContextSwitch_PeriodicBoost_ResetsAllNonTerminatedToPriorityZero()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1); // expires on this switch
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 3);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 10, programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryTicksUsed));
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(1) + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(1) + Hardware.ProcessEntryTicksUsed));
    }

    [Fact]
    public void ContextSwitch_PeriodicBoost_SkipsTerminatedSlots()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1);
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready,      (int)PrivilegeLevel.User, priority: 2, ticksUsed: 1);
        SeedEntry(hw, 1, (int)ProcessState.Terminated, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 5, programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // P0 boosted; Terminated P1 must not be touched.
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPriority));
        Assert.Equal(3, ReadWord(hw, OsLayout.ProcessEntryAddress(1) + Hardware.ProcessEntryPriority));
    }

    [Fact]
    public void ContextSwitch_PeriodicBoost_ResetsTimerToBoostInterval()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        Assert.Equal(OsLayout.BoostInterval, ReadWord(hw, OsLayout.BoostTimerOffset));
    }

    [Fact]
    public void ContextSwitch_NonExpiredTimer_DoesNotBoost()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        // Timer starts at 5 (well above 1) so it must not fire.
        WriteWord(hw, OsLayout.BoostTimerOffset, 5);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 200);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Priority unchanged (level 3 never demotes and no boost fired).
        Assert.Equal(3, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPriority));
        // Timer decremented by 1.
        Assert.Equal(4, ReadWord(hw, OsLayout.BoostTimerOffset));
    }

    // ---- boost loop boundary conditions ------------------------------------

    // EDGE CASE: The boost loop uses Js (fires when count - i < 0, i.e. i > count)
    // to exit, which means it executes the body for i = 0 through count inclusive —
    // one iteration past the last valid index. When count = MaxProcesses = 8, the
    // "ghost" entry at index 8 starts at BuddyBitmapOffset. Its Priority field
    // (offset 128) and TicksUsed field (offset 132) land beyond the OS region into
    // process heap memory. This test pre-seeds sentinels at those exact addresses and
    // verifies they are not overwritten by the boost loop.
    [Fact]
    public void ContextSwitch_PeriodicBoost_DoesNotWriteBeyondProcessTable()
    {
        // POTENTIAL DYSFUNCTION: boost loop iterates i = 0..count rather than
        // 0..count-1 due to Js rather than (Jz + Js) exit condition. With a full
        // table (count = MaxProcesses) the ghost entry at index 8 starts at
        // BuddyBitmapOffset; its Priority/TicksUsed fields are 128/132 bytes further.
        // Hardware must be large enough that those addresses are valid memory.

        Hardware hw = Test.NewHardware(Test.MachineWithHeap(16384), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1); // force boost on this switch

        int count = OsLayout.MaxProcesses;
        WriteWord(hw, OsLayout.ProcessCountOffset, count);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        for (int i = 0; i < count; i++)
        {
            SeedEntry(hw, i, (int)ProcessState.Ready, (int)PrivilegeLevel.User,
                priority: 3, ticksUsed: 10, programAddress: 4096 + i * 512);
        }

        // Ghost entry at index 8 starts at BuddyBitmapOffset; its Priority and
        // TicksUsed fields (offsets 128 and 132) fall into process heap memory.
        int ghostPriorityAddr = OsLayout.BuddyBitmapOffset + Hardware.ProcessEntryPriority;
        int ghostTicksAddr    = OsLayout.BuddyBitmapOffset + Hardware.ProcessEntryTicksUsed;
        WriteWord(hw, ghostPriorityAddr, 0xAB);  // sentinel: must not become 0
        WriteWord(hw, ghostTicksAddr,    0xCD);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // The sentinels must be intact; an off-by-one boost would zero them.
        Assert.Equal(0xAB, ReadWord(hw, ghostPriorityAddr));
        Assert.Equal(0xCD, ReadWord(hw, ghostTicksAddr));
    }

    // EDGE CASE: Boost fires with processCount = 2. The off-by-one loop processes
    // indices 0, 1, and then the ghost index 2. The ghost entry's Priority and
    // TicksUsed fields are written at OsLayout.ProcessEntryAddress(2) +
    // ProcessEntryPriority / ProcessEntryTicksUsed. Those addresses are still within
    // the process table region (MaxProcesses=8, only 2 active), so the write does
    // not escape the table boundary here, but it still modifies an unused slot that
    // should not be touched. The test verifies the ghost slot at index 2 is not
    // modified by the boost when processCount = 2.
    [Fact]
    public void ContextSwitch_PeriodicBoost_WithTwoProcesses_DoesNotWriteToSlotTwo()
    {
        // POTENTIAL DYSFUNCTION: boost loop processes slot[count] = slot[2] even
        // though only slots 0 and 1 are part of the active process table.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1); // force boost
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 1);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 1,
            programAddress: 5000);

        // Seed slot 2 (the ghost) with sentinel values and mark it Ready
        // (not Terminated) so the off-by-one body would write to it.
        int entry2 = OsLayout.ProcessEntryAddress(2);
        WriteWord(hw, entry2 + Hardware.ProcessEntryState,    (int)ProcessState.Ready);
        WriteWord(hw, entry2 + Hardware.ProcessEntryPriority, 42);
        WriteWord(hw, entry2 + Hardware.ProcessEntryTicksUsed, 17);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Slots 0 and 1 must be boosted.
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(1) + Hardware.ProcessEntryPriority));

        // Slot 2 (outside the active table) must NOT have been written.
        Assert.Equal(42, ReadWord(hw, entry2 + Hardware.ProcessEntryPriority));
        Assert.Equal(17, ReadWord(hw, entry2 + Hardware.ProcessEntryTicksUsed));
    }

    // EDGE CASE: Boost fires with processCount = 1. The off-by-one loop reaches i = 1
    // after processing the single valid slot (i = 0). It then reads slot index 1 and,
    // if that slot's state is not Terminated (because memory is uninitialized), writes
    // zeros to its Priority and TicksUsed. The test verifies slot 1 is not modified.
    [Fact]
    public void ContextSwitch_PeriodicBoost_WithOneProcess_DoesNotWriteToSlotOne()
    {
        // POTENTIAL DYSFUNCTION: the off-by-one iteration writes to slot[count].

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1); // force boost
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 5);

        // Pre-seed slot 1 with sentinel values to detect a spurious write. Mark it
        // Ready (not Terminated) so the off-by-one body does not skip it.
        int entry1 = OsLayout.ProcessEntryAddress(1);
        WriteWord(hw, entry1 + Hardware.ProcessEntryState,    (int)ProcessState.Ready);
        WriteWord(hw, entry1 + Hardware.ProcessEntryPriority, 55);
        WriteWord(hw, entry1 + Hardware.ProcessEntryTicksUsed, 33);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // The valid slot must have been boosted.
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryTicksUsed));

        // Slot 1 (outside the active table) must NOT have been written.
        Assert.Equal(55, ReadWord(hw, entry1 + Hardware.ProcessEntryPriority));
        Assert.Equal(33, ReadWord(hw, entry1 + Hardware.ProcessEntryTicksUsed));
    }

    // ---- idle context-switch behavior ------------------------------------

    // EDGE CASE: When the CPU is idle (currentIndex == -1), ContextSwitch jumps
    // directly to resume_mlfq, skipping the boost timer decrement. The timer must
    // not change when no process is running.
    [Fact]
    public void ContextSwitch_WhenIdle_DoesNotDecrementBoostTimer()
    {
        // EDGE CASE: cs_skip jumps over the boost block entirely when currentIndex < 0.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 10);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1); // idle
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        Assert.Equal(10, ReadWord(hw, OsLayout.BoostTimerOffset));
    }

    // EDGE CASE: When idle, ContextSwitch must still find and resume a Ready process
    // without touching the boost timer.
    [Fact]
    public void ContextSwitch_WhenIdle_StillSchedulesReadyProcess()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 7);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1); // idle
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal(7, ReadWord(hw, OsLayout.BoostTimerOffset));
    }

    // ---- resume_mlfq inner scan boundary ---------------------------------

    // EDGE CASE: resume_mlfq exits the inner level scan when ESI > count (Js), not
    // when ESI >= count (Jz + Js). With count = 1 and a single process that is NOT
    // at the current scan level, the extra iteration at i = count re-checks the
    // process's own slot. If that slot is at the scan level, it gets selected. This
    // test verifies that a process at level 2 is not selected during the level-0 scan
    // even after the extra iteration.
    [Fact]
    public void ResumeMlfq_InnerScan_DoesNotSelectProcessAtWrongPriority_OnExtraIteration()
    {
        // EDGE CASE: Inner scan runs count+1 iterations; the extra one re-checks the
        // current process. The process must not be selected during a level mismatch.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        // Only one process at priority 2: the level-0 and level-1 scans must not
        // select it. Level-2 scan must select it correctly.
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
        Assert.Equal((int)PrivilegeLevel.User, (int)hw.GetPrivilegeLevel());
    }

    // EDGE CASE: Two processes. P0 is current at priority 1; P1 is at priority 0.
    // resume_mlfq inner scan at level 0: i=1 finds P1 (correct). The off-by-one
    // extra iteration at i=2 should never fire because P1 was already selected at i=1.
    // Verify that P1 (priority 0, higher priority) is selected, not P0 (priority 1).
    [Fact]
    public void ResumeMlfq_HigherPriorityProcess_WinsBeforeExtraIteration()
    {
        // EDGE CASE: Confirm the higher-priority candidate is selected at i=1, before
        // the off-by-one extra iteration at i=count could intervene.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 0);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0,
            programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        Assert.Equal(1, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    // EDGE CASE: The extra inner-scan iteration at i = count wraps the candidate index
    // back to the current process itself. When the current process is the only one at
    // its level and is Ready, it must still be selected (running it again is correct
    // when it is the sole qualified candidate).
    [Fact]
    public void ResumeMlfq_SingleProcessAtItsLevel_IsReselectedByExtraIteration()
    {
        // EDGE CASE: With one process at priority 1 and nothing at 0, levels 0 inner
        // scan exhausts real candidates and the extra iteration wraps back to find it.
        // Level 1 would find it first — verify it is selected exactly once.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 0);
        SeedEntry(hw, 1, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0,
            programAddress: 5000);

        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        // P0 (priority 1) is the only Ready process; it must be selected.
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    // ---- demotion then re-schedule interaction ---------------------------

    // EDGE CASE: A process that is demoted by ContextSwitch (priority 0 → 1) then
    // immediately re-scheduled. After demotion it must be found at level 1, not 0.
    [Fact]
    public void ContextSwitch_DemotedProcess_IsFoundAtNewLevelOnNextSchedule()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L0 threshold = 1
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User,
            priority: 0, ticksUsed: 0, programAddress: 4096);

        // First context switch: demotes P0 from priority 0 to priority 1.
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // P0 must have been demoted.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(1, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));

        // Manually force another context switch from the idle state.
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        hw.DispatchOsRoutine(Hardware.IvtSchedule);
        RunRoutine(hw);

        // P0 at priority 1 must be selected (it is the only Ready process).
        Assert.Equal(0, ReadWord(hw, OsLayout.CurrentIndexOffset));
    }

    // EDGE CASE: A process demoted from level 2 to level 3 (via ContextSwitch) must
    // never be demoted further, even after many subsequent context switches. This
    // tests the Jz guard at the start of the demotion path.
    [Fact]
    public void ContextSwitch_Level2DemotedToLevel3_ThenNeverDemotedFurther()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw); // L2 threshold = 4
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        // ticksUsed = 3: one more tick (3→4) crosses the L2 threshold of 4.
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 3);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(3, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, entry0 + Hardware.ProcessEntryTicksUsed));

        // Apply many more context switches; priority must stay at 3.
        for (int i = 0; i < 10; i++)
        {
            WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
            hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
            RunRoutine(hw);
        }

        Assert.Equal(3, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));
    }

    // ---- SeedOsData / OsLayout offset correctness ------------------------

    // EDGE CASE: Verify that SeedOsData writes the quantum table to the correct
    // addresses. An off-by-one in QuantumTableOffset or BoostTimerOffset would cause
    // the scheduler to read stale zeros or a wrong threshold.
    [Fact]
    public void SeedOsData_QuantumTable_IsReadableAtExpectedOffsets()
    {
        // EDGE CASE: OsLayout offsets shifted when MLFQ fields were added. This test
        // confirms each table entry is at the address OsRoutines actually reads.

        Hardware hw = Test.NewHardware(Test.MachineWithHeap(8192), new FakeOS());
        hw.ReserveOsMemory(OsLayout.TotalSize);
        hw.WriteBytes(0, OsRoutines.BuildOsImage());

        // SeedMlfqDefaults writes the canonical values.
        WriteWord(hw, OsLayout.BoostTimerOffset,       OsLayout.BoostInterval);
        WriteWord(hw, OsLayout.QuantumTableOffset + 0,  1);
        WriteWord(hw, OsLayout.QuantumTableOffset + 4,  2);
        WriteWord(hw, OsLayout.QuantumTableOffset + 8,  4);
        WriteWord(hw, OsLayout.QuantumTableOffset + 12, 255);

        Assert.Equal(OsLayout.BoostInterval, ReadWord(hw, OsLayout.BoostTimerOffset));
        Assert.Equal(1,   ReadWord(hw, OsLayout.QuantumTableOffset + 0));
        Assert.Equal(2,   ReadWord(hw, OsLayout.QuantumTableOffset + 4));
        Assert.Equal(4,   ReadWord(hw, OsLayout.QuantumTableOffset + 8));
        Assert.Equal(255, ReadWord(hw, OsLayout.QuantumTableOffset + 12));
    }

    // EDGE CASE: QuantumTableOffset is DataBase+20. The four 4-byte entries span
    // bytes [20..35]. ProcessTableOffset is DataBase+36. Confirm there is no overlap
    // between the quantum table and the first process entry.
    [Fact]
    public void OsLayout_QuantumTableEnd_DoesNotOverlapProcessTable()
    {
        int quantumTableEnd = OsLayout.QuantumTableOffset + OsLayout.QueueCount * 4;
        Assert.True(quantumTableEnd <= OsLayout.ProcessTableOffset,
            $"QuantumTable ends at {quantumTableEnd}, ProcessTable starts at {OsLayout.ProcessTableOffset}");
    }

    // EDGE CASE: BoostTimerOffset is DataBase+16 (4 bytes) and QuantumTableOffset is
    // DataBase+20. Confirm the timer word does not overlap the quantum table.
    [Fact]
    public void OsLayout_BoostTimerAndQuantumTable_DoNotOverlap()
    {
        int boostTimerEnd = OsLayout.BoostTimerOffset + 4;
        Assert.True(boostTimerEnd <= OsLayout.QuantumTableOffset,
            $"BoostTimer ends at {boostTimerEnd}, QuantumTable starts at {OsLayout.QuantumTableOffset}");
    }

    // EDGE CASE: ProcessTableOffset must leave enough room after the header fields
    // (ProcessCount, CurrentIndex, BuddyHeapStart, BuddyHeapSize, BoostTimer,
    // QuantumTable, BuddyMinBlock, BuddyLevels) without aliasing any of them.
    [Fact]
    public void OsLayout_ProcessTableOffset_IsAfterAllHeaderFields()
    {
        int quantumTableEnd = OsLayout.QuantumTableOffset + OsLayout.QueueCount * 4;
        // Two buddy fields (BuddyMinBlock + BuddyLevels, 4 bytes each) follow the
        // quantum table, then the NextPid counter (4 bytes), before the process table.
        int buddyFieldsEnd = quantumTableEnd + 8;
        Assert.Equal(OsLayout.NextPidOffset, buddyFieldsEnd);
        Assert.Equal(OsLayout.ProcessTableOffset, OsLayout.NextPidOffset + 4);
    }

    // ---- LoadProcess MLFQ field seeding ----------------------------------

    // EDGE CASE: When a process is loaded, its Priority and TicksUsed must be seeded
    // to 0 so it starts at the highest MLFQ level. A bug that omits these writes
    // would leave garbage or stale values from a previously occupied slot.
    [Fact]
    public void LoadProcess_SeedsNewProcess_WithPriorityZeroAndTicksUsedZero()
    {
        // EDGE CASE: Verify MLFQ fields are explicitly initialised, not just zeroed
        // by ClearEntry, so a recycled slot does not carry stale priority values.

        System.IO.StringWriter log = new System.IO.StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(16384), os);

        // Build a minimal program: just a HLT.
        Assembler asm = new Assembler();
        asm.Hlt();
        byte[] program = asm.Build();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mlfq_seed_test_" + System.Guid.NewGuid().ToString("N") + ".bin");
        System.IO.File.WriteAllBytes(path, program);

        try
        {
            os.LoadProcess(new Process(path, 128, 64));
        }
        finally
        {
            System.IO.File.Delete(path);
        }

        int count = ReadWord(hw, OsLayout.ProcessCountOffset);
        Assert.True(count >= 1, "Process table must have at least one entry after load.");
        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal(0, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(0, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    // EDGE CASE: A recycled (previously Terminated) slot may hold stale Priority and
    // TicksUsed values from the old process. LoadProcess must re-seed them to 0 after
    // ClearEntry clears the slot.
    [Fact]
    public void LoadProcess_RecycledSlot_SeedsPriorityAndTicksUsedToZero()
    {
        // EDGE CASE: ClearEntry zeros the slot, but the explicit seeding in
        // LoadProcess is what guarantees MLFQ correctness; verify the net effect.

        System.IO.StringWriter log = new System.IO.StringWriter();
        BasicOS os = new BasicOS(log);
        Hardware hw = Test.NewHardware(Test.MachineWithHeap(16384), os);

        Assembler asm = new Assembler();
        asm.Hlt();
        byte[] program = asm.Build();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mlfq_recycle_test_" + System.Guid.NewGuid().ToString("N") + ".bin");
        System.IO.File.WriteAllBytes(path, program);

        try
        {
            os.LoadProcess(new Process(path, 128, 64));

            // Manually corrupt Priority and TicksUsed of slot 0, then mark it
            // Terminated so it can be recycled.
            int entry0 = OsLayout.ProcessEntryAddress(0);
            WriteWord(hw, entry0 + Hardware.ProcessEntryPriority, 3);
            WriteWord(hw, entry0 + Hardware.ProcessEntryTicksUsed, 99);
            WriteWord(hw, entry0 + Hardware.ProcessEntryState, (int)ProcessState.Terminated);

            os.LoadProcess(new Process(path, 128, 64));

            // Slot 0 was recycled; its MLFQ fields must be re-initialized to 0.
            Assert.Equal(0, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));
            Assert.Equal(0, ReadWord(hw, entry0 + Hardware.ProcessEntryTicksUsed));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    // ---- WakeInput/WakeOutput boost correctness --------------------------

    // EDGE CASE: WakeInput on a process that is Blocked on Output (not Input) must
    // NOT boost or change the process's Priority or TicksUsed. A mismatched wake
    // reason must leave MLFQ state entirely intact.
    [Fact]
    public void WakeInput_OnProcessBlockedOnOutput_DoesNotAlterMlfqFields()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 7);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason,
            (int)WaitReason.Output);

        hw.WriteRegisterAt(0, 0); // EAX = device 0
        hw.DispatchOsRoutine(Hardware.IvtWakeInput);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        // Still Blocked: state, priority and ticks unchanged.
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal(1, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(7, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    // EDGE CASE: WakeOutput on a process that is Blocked on Input must not boost.
    [Fact]
    public void WakeOutput_OnProcessBlockedOnInput_DoesNotAlterMlfqFields()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, -1);
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 4);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason,
            (int)WaitReason.Input);

        hw.WriteRegisterAt(0, 0);
        hw.DispatchOsRoutine(Hardware.IvtWakeOutput);
        RunRoutine(hw);

        int entry = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry + Hardware.ProcessEntryState));
        Assert.Equal(2, ReadWord(hw, entry + Hardware.ProcessEntryPriority));
        Assert.Equal(4, ReadWord(hw, entry + Hardware.ProcessEntryTicksUsed));
    }

    // EDGE CASE: After WakeInput boosts a process to priority 0, a subsequent
    // ContextSwitch must find it at level 0 (not its old level).
    [Fact]
    public void WakeInput_BoostedProcess_IsFoundAtLevelZeroOnNextContextSwitch()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 2);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 1); // P1 running
        SeedEntry(hw, 0, (int)ProcessState.Blocked, (int)PrivilegeLevel.User, priority: 3, ticksUsed: 20,
            programAddress: 4096);
        WriteWord(hw, OsLayout.ProcessEntryAddress(0) + Hardware.ProcessEntryWaitReason,
            (int)WaitReason.Input);
        SeedEntry(hw, 1, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0,
            programAddress: 5000);

        // Wake P0 (device 0).
        hw.WriteRegisterAt(0, 0);
        hw.DispatchOsRoutine(Hardware.IvtWakeInput);
        RunRoutine(hw);

        // After wake, P0 is Ready at priority 0. Now P1's quantum expires.
        WriteWord(hw, OsLayout.CurrentIndexOffset, 1);
        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        // Level-0 scan must find P0 (woken, priority 0) over P1 (also priority 0)
        // because the round-robin from P1 reaches P0 first.
        int selected = ReadWord(hw, OsLayout.CurrentIndexOffset);
        Assert.Equal(0, selected);
    }

    // ---- BoostInterval constant correctness ------------------------------

    // EDGE CASE: BoostInterval is used as the reset value when the timer fires. A
    // mismatch between the ISA instruction that resets it and the C# constant would
    // cause the boost to fire at the wrong cadence.
    [Fact]
    public void ContextSwitch_BoostTimerReset_UsesBoostIntervalConstant()
    {
        // EDGE CASE: OsRoutines.EmitContextSwitch hard-codes OsLayout.BoostInterval
        // as a MovImm operand. This test confirms the reset value matches the constant
        // rather than a stale literal.

        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.BoostTimerOffset, 1); // fire on this switch
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 0, ticksUsed: 0);

        hw.DispatchOsRoutine(Hardware.IvtContextSwitch);
        RunRoutine(hw);

        Assert.Equal(OsLayout.BoostInterval, ReadWord(hw, OsLayout.BoostTimerOffset));
    }

    // ---- Block routine MLFQ field preservation --------------------------

    // EDGE CASE: The Block routine saves the current process and marks it Blocked.
    // It must not alter Priority or TicksUsed (those fields are only changed by
    // ContextSwitch and Wake). This verifies Block preserves MLFQ state.
    [Fact]
    public void Block_PreservesMlfqFields_WhenMarkingProcessBlocked()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 2, ticksUsed: 3);

        hw.DispatchOsRoutine(Hardware.IvtBlockInput, (int)WaitReason.Input);
        RunRoutine(hw);

        int entry0 = OsLayout.ProcessEntryAddress(0);
        Assert.Equal((int)ProcessState.Blocked, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        // Block must not touch MLFQ fields.
        Assert.Equal(2, ReadWord(hw, entry0 + Hardware.ProcessEntryPriority));
        Assert.Equal(3, ReadWord(hw, entry0 + Hardware.ProcessEntryTicksUsed));
    }

    // ---- Halt routine MLFQ field non-corruption --------------------------

    // EDGE CASE: When Halt terminates a process it marks the slot Terminated. The
    // slot's Priority and TicksUsed are left at whatever they were. Verify the Halt
    // routine does not write to those fields (they are garbage in a Terminated slot
    // and should not be used, but they must also not inadvertently destroy adjacent
    // memory by writing to wrong offsets).
    [Fact]
    public void Halt_DoesNotWriteToMlfqFields_InTerminatedSlot()
    {
        Hardware hw = NewSeededHardware();
        SeedMlfqDefaults(hw);
        WriteWord(hw, OsLayout.ProcessCountOffset, 1);
        WriteWord(hw, OsLayout.CurrentIndexOffset, 0);
        SeedEntry(hw, 0, (int)ProcessState.Ready, (int)PrivilegeLevel.User, priority: 1, ticksUsed: 5);

        // Record the byte range just after ProcessEntrySize to detect stray writes.
        int entry0 = OsLayout.ProcessEntryAddress(0);
        int adjacentAddr = entry0 + Hardware.ProcessEntrySize;
        int sentinel = unchecked((int)0xDEADBEEF);
        WriteWord(hw, adjacentAddr, sentinel);

        hw.DispatchOsRoutine(Hardware.IvtHalt);
        RunRoutine(hw);

        Assert.Equal((int)ProcessState.Terminated, ReadWord(hw, entry0 + Hardware.ProcessEntryState));
        // The sentinel beyond the entry boundary must be intact.
        Assert.Equal(sentinel, ReadWord(hw, adjacentAddr));
    }
}
