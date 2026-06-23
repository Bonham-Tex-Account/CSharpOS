namespace CSharpOS;

/// <summary>
/// Emits the OS routines as ISA code that runs in the OS memory region. Each
/// routine is entered through the IVT in Privileged mode (program base 0, so all
/// addresses are absolute) and returns to a process with OSRET. Built and tested
/// in isolation; BasicOS adopts them once the full set is proven.
///
/// Register convention across a routine: ECX = current index, EDI = process count,
/// ESI = scan/loop counter, EDX = a carried argument (wait reason) or the chosen
/// candidate index; EAX, EBX, EBP are scratch (EBX usually holds an entry address).
/// R8-R15 are used by ContextSwitch and resume_mlfq for MLFQ state that does not
/// fit in the classic set without spilling.
/// </summary>
public static class OsRoutines
{
    private const byte EAX = (byte)RegisterName.EAX;
    private const byte EBX = (byte)RegisterName.EBX;
    private const byte ECX = (byte)RegisterName.ECX;
    private const byte EDX = (byte)RegisterName.EDX;
    private const byte ESI = (byte)RegisterName.ESI;
    private const byte EDI = (byte)RegisterName.EDI;
    private const byte EBP = (byte)RegisterName.EBP;
    private const byte R8  = (byte)RegisterName.R8;
    private const byte R9  = (byte)RegisterName.R9;
    private const byte R10 = (byte)RegisterName.R10;
    private const byte R11 = (byte)RegisterName.R11;
    private const byte R12 = (byte)RegisterName.R12;
    private const byte R13 = (byte)RegisterName.R13;
    private const byte R14 = (byte)RegisterName.R14;
    private const byte R15 = (byte)RegisterName.R15;

    private const int Ready      = (int)ProcessState.Ready;
    private const int Blocked    = (int)ProcessState.Blocked;
    private const int Terminated = (int)ProcessState.Terminated;
    private const int WaitNone   = (int)WaitReason.None;
    private const int User       = (int)PrivilegeLevel.User;
    private const int EntrySize  = Hardware.ProcessEntrySize;

    // Builds the full OS image: every implemented routine packed after the IVT, with
    // each routine's IVT slot pointing at its absolute address. The data section is
    // left zeroed for the C# side (or a test) to seed.
    public static byte[] BuildOsImage()
    {
        Assembler asm = new Assembler();

        int contextSwitch = OsLayout.CodeBase + asm.CodeLength; EmitContextSwitch(asm);
        int schedule      = OsLayout.CodeBase + asm.CodeLength; EmitSchedule(asm);
        int block         = OsLayout.CodeBase + asm.CodeLength; EmitBlock(asm);
        int wakeInput     = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, (int)WaitReason.Input);
        int wakeOutput    = OsLayout.CodeBase + asm.CodeLength; EmitWakeEntry(asm, (int)WaitReason.Output);
        EmitWakeBody(asm);
        int halt          = OsLayout.CodeBase + asm.CodeLength; EmitHalt(asm);
        int invalid       = OsLayout.CodeBase + asm.CodeLength; EmitInvalidInstruction(asm);
        int loadProcess   = OsLayout.CodeBase + asm.CodeLength; EmitLoadProcess(asm);
        EmitResumeMlfq(asm);

        byte[] code = asm.Build(OsLayout.CodeBase);

        if (OsLayout.CodeBase + code.Length > OsLayout.DataBase)
        {
            throw new InvalidOperationException(
                $"OS code ({code.Length} bytes) overruns the data section at offset {OsLayout.DataBase}; raise OsLayout.DataBase.");
        }

        byte[] image = new byte[OsLayout.TotalSize];
        Array.Copy(code, 0, image, OsLayout.CodeBase, code.Length);
        WriteWord(image, Hardware.IvtContextSwitch * 4,      contextSwitch);
        WriteWord(image, Hardware.IvtSchedule * 4,           schedule);
        WriteWord(image, Hardware.IvtBlockInput * 4,         block);
        WriteWord(image, Hardware.IvtBlockOutput * 4,        block);
        WriteWord(image, Hardware.IvtWakeInput * 4,          wakeInput);
        WriteWord(image, Hardware.IvtWakeOutput * 4,         wakeOutput);
        WriteWord(image, Hardware.IvtHalt * 4,               halt);
        WriteWord(image, Hardware.IvtInvalidInstruction * 4, invalid);
        WriteWord(image, Hardware.IvtLoadProcess * 4,        loadProcess);
        return image;
    }

    // ContextSwitch: save the interrupted process (if any), apply MLFQ demotion if
    // the process used its full quantum, tick the global boost timer (resetting all
    // process priorities if it expires), then resume the highest-priority Ready process.
    private static void EmitContextSwitch(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("cs_skip");                         // no current process: skip save and MLFQ update

        EntryAddress(asm, ECX, EBX);              // EBX = current entry
        asm.SaveRegs(R(EBX));

        // ---- increment TicksUsed ----
        LoadField(asm, EBX, Hardware.ProcessEntryTicksUsed, R8); // R8 = ticksUsed
        asm.Inc(R(R8));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryTicksUsed, R8);

        // ---- quantum check: demote if ticks >= threshold for this level ----
        LoadField(asm, EBX, Hardware.ProcessEntryPriority, R9); // R9 = priority
        asm.MovImm(R(R10), OsLayout.QueueCount - 1);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("cs_no_demote");                    // level 3: never demote

        // threshold = QuantumTable[priority]  (R10 = &table[priority])
        asm.Mov(R(R10), R(R9));
        asm.MovImm(R(R11), 4);
        asm.Mul(R(R10), R(R11));                   // R10 = priority * 4
        asm.MovImm16(R(R11), OsLayout.QuantumTableOffset);
        asm.Add(R(R10), R(R11));                   // R10 = address of threshold entry
        asm.Load(R(R10), R(R10));                  // R10 = threshold

        asm.Cmp(R(R8), R(R10));                    // ticksUsed - threshold
        asm.Js("cs_no_demote");                    // ticksUsed < threshold: no demote

        // Demote: priority++, reset TicksUsed.
        // Priority was 0/1/2 (never 3 due to early exit above), so increment is safe.
        asm.Inc(R(R9));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryPriority, R9);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_no_demote");

        // ---- boost timer: periodically reset all process priorities to 0 ----
        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.Load(R(R10), R(R8));                   // R10 = boostTimer
        asm.Dec(R(R10));
        asm.Store(R(R8), R(R10));                  // write back decremented timer

        asm.MovImm(R(R11), 0);
        asm.Cmp(R(R10), R(R11));
        asm.Jnz("cs_boost_skip");                  // timer != 0: skip boost

        // Boost loop: iterate all entries, reset Priority and TicksUsed on non-Terminated ones.
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                  // EDI = processCount
        asm.MovImm(R(ESI), 0);                     // ESI = i

        asm.Label("cs_boost_loop");
        asm.Mov(R(R11), R(EDI));
        asm.Cmp(R(R11), R(ESI));                   // count - i
        asm.Jz("cs_boost_done");                   // i == count: done
        asm.Js("cs_boost_done");                   // i > count: done

        // R12 = entry address for process i
        asm.Mov(R(R12), R(ESI));
        asm.MovImm(R(R13), EntrySize);
        asm.Mul(R(R12), R(R13));
        asm.MovImm16(R(R13), OsLayout.ProcessTableOffset);
        asm.Add(R(R12), R(R13));

        LoadField(asm, R12, Hardware.ProcessEntryState, R13); // R13 = state
        asm.MovImm(R(R14), Terminated);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("cs_boost_skip_entry");             // terminated slot: skip

        StoreFieldImm(asm, R12, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R12, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_boost_skip_entry");
        asm.Inc(R(ESI));
        asm.Jmp("cs_boost_loop");

        asm.Label("cs_boost_done");
        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.MovImm(R(R10), OsLayout.BoostInterval);
        asm.Store(R(R8), R(R10));                  // reset boost timer

        asm.Label("cs_boost_skip");
        asm.Label("cs_skip");
        asm.Jmp("resume_mlfq");
    }

    // Schedule: called when the CPU is idle; resume any Ready process.
    private static void EmitSchedule(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex (-1 when idle)
        asm.Jmp("resume_mlfq");
    }

    // Block: mark the running process Blocked on the wait reason passed in EAX, save
    // it, and resume the next Ready process.
    private static void EmitBlock(Assembler asm)
    {
        asm.Mov(R(EDX), R(EAX));                   // EDX = wait reason
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex
        EntryAddress(asm, ECX, EBX);              // EBX = current entry
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Blocked);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryWaitReason, EDX);
        asm.SaveRegs(R(EBX));
        asm.Jmp("resume_mlfq");
    }

    // Halt: free the running process's slot (mark it Terminated, so the scheduler
    // ignores it), return its memory to the free list, then resume the next Ready
    // process.
    private static void EmitHalt(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex
        EntryAddress(asm, ECX, EBX);              // EBX = current entry (kept below)
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        EmitFreeListAppend(asm);                  // append {ProgramAddress, TotalSize}
        asm.Jmp("resume_mlfq");
    }

    // Appends the entry's memory range (ProgramAddress, TotalSize) to the free list.
    // Expects EBX = entry; uses EAX/EDX/ESI/EDI/EBP as scratch (ECX preserved).
    private static void EmitFreeListAppend(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.FreeRangeCountOffset);
        asm.Load(R(EDI), R(EAX));                  // EDI = free range count
        asm.Mov(R(EBP), R(EDI));                   // EBP = count * FreeRangeSize ...
        asm.MovImm(R(EAX), OsLayout.FreeRangeSize);
        asm.Mul(R(EBP), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.FreeRangeTableOffset);
        asm.Add(R(EBP), R(EAX));                   // EBP = new range slot address
        // slot.Start = entry.ProgramAddress
        asm.Mov(R(ESI), R(EBX)); asm.MovImm(R(EDX), Hardware.ProcessEntryProgramAddress); asm.Add(R(ESI), R(EDX));
        asm.Load(R(EAX), R(ESI));
        asm.Store(R(EBP), R(EAX));
        // slot.Size = entry.TotalSize
        asm.Mov(R(ESI), R(EBX)); asm.MovImm(R(EDX), Hardware.ProcessEntryTotalSize); asm.Add(R(ESI), R(EDX));
        asm.Load(R(EAX), R(ESI));
        asm.Mov(R(ESI), R(EBP)); asm.MovImm(R(EDX), 4); asm.Add(R(ESI), R(EDX));
        asm.Store(R(ESI), R(EAX));
        // count++
        asm.Inc(R(EDI));
        Imm16(asm, EAX, OsLayout.FreeRangeCountOffset);
        asm.Store(R(EAX), R(EDI));
    }

    // InvalidInstruction: the faulting opcode is in EAX (unused here beyond logging,
    // which Hardware handles). Terminate the faulting process like Halt, freeing its
    // slot and memory, then resume the next Ready process.
    private static void EmitInvalidInstruction(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        EmitFreeListAppend(asm);
        asm.Jmp("resume_mlfq");
    }

    // LoadProcess: first-fit allocation for the staged entry (address in EAX). Reads
    // the entry's TotalSize, finds the first free range large enough, records the
    // allocated base in the entry's ProgramAddress, and splits the range. On failure
    // (no range fits) it sets ProgramAddress = -1 for the C# loader to detect. Does
    // not switch processes; the C# loader finishes seeding the entry.
    private static void EmitLoadProcess(Assembler asm)
    {
        asm.Mov(R(EBX), R(EAX));                   // EBX = entry
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, ECX); // ECX = needed size
        Imm16(asm, EAX, OsLayout.FreeRangeCountOffset);
        asm.Load(R(EDI), R(EAX));                  // EDI = free range count
        asm.MovImm(R(ESI), 0);                     // ESI = i

        asm.Label("lp_scan");
        asm.Mov(R(EAX), R(EDI));
        asm.Cmp(R(EAX), R(ESI));
        asm.Js("lp_fail");                         // i > count
        asm.Jz("lp_fail");                         // i == count: scanned all, no fit
        // EBP = range slot address = FreeRangeTableOffset + i * FreeRangeSize
        asm.Mov(R(EBP), R(ESI));
        asm.MovImm(R(EAX), OsLayout.FreeRangeSize);
        asm.Mul(R(EBP), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.FreeRangeTableOffset);
        asm.Add(R(EBP), R(EAX));                   // EBP = range slot
        // EDX = range size = [slot + 4]
        asm.Mov(R(EAX), R(EBP)); asm.MovImm(R(EDX), 4); asm.Add(R(EAX), R(EDX)); asm.Load(R(EDX), R(EAX));
        asm.Cmp(R(EDX), R(ECX));                   // size - needed
        asm.Js("lp_next");                         // size < needed
        asm.Jmp("lp_found");
        asm.Label("lp_next");
        asm.Inc(R(ESI));
        asm.Jmp("lp_scan");

        asm.Label("lp_found");
        asm.Load(R(EAX), R(EBP));                  // EAX = range start
        // entry.ProgramAddress = range start
        asm.Mov(R(ESI), R(EBX)); asm.MovImm(R(EDI), Hardware.ProcessEntryProgramAddress); asm.Add(R(ESI), R(EDI));
        asm.Store(R(ESI), R(EAX));
        // split: slot.Start = start + needed
        asm.Mov(R(ESI), R(EAX)); asm.Add(R(ESI), R(ECX)); asm.Store(R(EBP), R(ESI));
        // slot.Size = size - needed
        asm.Mov(R(ESI), R(EBP)); asm.MovImm(R(EDI), 4); asm.Add(R(ESI), R(EDI));
        asm.Mov(R(EDI), R(EDX)); asm.Sub(R(EDI), R(ECX)); asm.Store(R(ESI), R(EDI));
        asm.Jmp("lp_done");

        asm.Label("lp_fail");
        asm.Mov(R(ESI), R(EBX)); asm.MovImm(R(EDI), Hardware.ProcessEntryProgramAddress); asm.Add(R(ESI), R(EDI));
        asm.MovImm(R(EDI), 0); asm.Dec(R(EDI)); asm.Store(R(ESI), R(EDI)); // ProgramAddress = -1

        asm.Label("lp_done");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));                          // return without switching
    }

    // Wake entry stub: a device interrupt fired for the device whose id (== process
    // table index) is in EAX. Like real hardware, the interrupt identifies the
    // device; this stub records which reason the device signals (input vs output)
    // and falls through to the shared body, which wakes that specific process if it
    // is blocked on that reason. Two stubs (input/output) point at distinct IVT slots.
    private static void EmitWakeEntry(Assembler asm, int reason)
    {
        asm.MovImm(R(EBP), reason);                 // EBP = this device's signal reason
        asm.Jmp("wk_body");
    }

    // Shared wake body: EAX = target device/process index, EBP = the reason the
    // device signals. Wake that process if (and only if) it is Blocked on that
    // reason, boost it to priority 0 (I/O-bound processes stay responsive), then
    // resume the interrupted process unchanged (a device interrupt does not preempt).
    private static void EmitWakeBody(Assembler asm)
    {
        asm.Label("wk_body");
        asm.Mov(R(EDX), R(EBP));                    // EDX = reason (EBP is scratch below)
        EntryAddress(asm, EAX, EBX);              // EBX = entry[index]
        LoadField(asm, EBX, Hardware.ProcessEntryState, EAX);
        asm.MovImm(R(EBP), Blocked);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jnz("wk_resume");                       // not blocked: spurious, just resume
        LoadField(asm, EBX, Hardware.ProcessEntryWaitReason, EAX);
        asm.Cmp(R(EAX), R(EDX));
        asm.Jnz("wk_resume");                       // blocked on a different reason

        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        // Boost woken process to the top queue so I/O-bound processes stay responsive.
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("wk_resume");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("wk_idle");                         // was idle: nothing to resume
        EntryAddress(asm, ECX, EBX);
        asm.SaveRegs(R(EBX));                      // persist interrupted frame + level
        asm.LoadRegs(R(EBX));                      // stage it straight back
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));                          // resume interrupted process
        asm.Label("wk_idle");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));                          // stay idle (currentIndex == -1)
    }

    // Shared MLFQ scheduling tail: scan all priority levels 0..QueueCount-1 in order.
    // Within each level, round-robin from ECX+1 (wrapping). Switch to the first Ready
    // process found at the highest level; if none are Ready at any level, go idle.
    private static void EmitResumeMlfq(Assembler asm)
    {
        asm.Label("resume_mlfq");
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                 // EDI = processCount

        asm.MovImm(R(R8), 0);                      // R8 = current priority level (outer loop)

        asm.Label("rn_level");
        asm.MovImm(R(R9), OsLayout.QueueCount);
        asm.Cmp(R(R8), R(R9));                     // priority - QueueCount
        asm.Jns("rn_idle");                        // priority >= QueueCount: no one Ready

        asm.MovImm(R(ESI), 0);                     // ESI = i (inner round-robin counter)

        asm.Label("rn_scan");
        asm.Inc(R(ESI));
        asm.Mov(R(R9), R(EDI));
        asm.Cmp(R(R9), R(ESI));                    // count - i
        asm.Js("rn_next_level");                   // i > count: exhausted this level

        // candidate = (currentIndex + i) % processCount
        asm.Mov(R(R10), R(ECX));
        asm.Add(R(R10), R(ESI));
        asm.Cmp(R(R10), R(EDI));
        asm.Js("rn_in_range");
        asm.Sub(R(R10), R(EDI));                   // wrap once into [0, count)
        asm.Label("rn_in_range");

        // R11 = entry address for candidate R10
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(R12), EntrySize);
        asm.Mul(R(R11), R(R12));
        asm.MovImm16(R(R12), OsLayout.ProcessTableOffset);
        asm.Add(R(R11), R(R12));

        // Skip if not Ready
        LoadField(asm, R11, Hardware.ProcessEntryState, R13);
        asm.MovImm(R(R14), Ready);
        asm.Cmp(R(R13), R(R14));
        asm.Jnz("rn_scan");

        // Skip if not at the target priority level
        LoadField(asm, R11, Hardware.ProcessEntryPriority, R13);
        asm.Cmp(R(R13), R(R8));
        asm.Jnz("rn_scan");

        // Found — switch to this process.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(R10));                 // currentIndex = candidate index
        asm.LoadRegs(R(R11));
        asm.SetLayout(R(R11));
        LoadField(asm, R11, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));

        asm.Label("rn_next_level");
        asm.Inc(R(R8));
        asm.Jmp("rn_level");

        asm.Label("rn_idle");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));                            // EBX = -1
        asm.Store(R(EAX), R(EBX));                 // currentIndex = -1
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ---- emit helpers ----------------------------------------------------

    private static RegisterName R(byte index) { return (RegisterName)index; }

    private static void Imm16(Assembler asm, byte dest, int value)
    {
        asm.MovImm16(R(dest), value);
    }

    // dest = ProcessTableOffset + index * EntrySize. Clobbers EAX.
    private static void EntryAddress(Assembler asm, byte indexReg, byte dest)
    {
        asm.Mov(R(dest), R(indexReg));
        asm.MovImm(R(EAX), EntrySize);
        asm.Mul(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.ProcessTableOffset);
        asm.Add(R(dest), R(EAX));
    }

    // dest = [entry + fieldOffset]. Clobbers EAX and EBP.
    private static void LoadField(Assembler asm, byte entry, int fieldOffset, byte dest)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Load(R(dest), R(EBP));
    }

    // [entry + fieldOffset] = value register. Clobbers EAX and EBP.
    private static void StoreFieldReg(Assembler asm, byte entry, int fieldOffset, byte valueReg)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(valueReg));
    }

    // [entry + fieldOffset] = immediate. Clobbers EAX and EBP.
    private static void StoreFieldImm(Assembler asm, byte entry, int fieldOffset, int value)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.MovImm(R(EAX), value);
        asm.Store(R(EBP), R(EAX));
    }

    private static void WriteWord(byte[] buffer, int offset, int value)
    {
        buffer[offset]     = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8)  & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
