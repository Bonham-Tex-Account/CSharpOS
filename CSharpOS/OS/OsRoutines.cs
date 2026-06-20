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
        int wake          = OsLayout.CodeBase + asm.CodeLength; EmitWake(asm);
        int halt          = OsLayout.CodeBase + asm.CodeLength; EmitHalt(asm);
        int invalid       = OsLayout.CodeBase + asm.CodeLength; EmitInvalidInstruction(asm);
        int loadProcess   = OsLayout.CodeBase + asm.CodeLength; EmitLoadProcess(asm);
        EmitResumeTail(asm);

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
        WriteWord(image, Hardware.IvtWakeInput * 4,          wake);
        WriteWord(image, Hardware.IvtWakeOutput * 4,         wake);
        WriteWord(image, Hardware.IvtHalt * 4,               halt);
        WriteWord(image, Hardware.IvtInvalidInstruction * 4, invalid);
        WriteWord(image, Hardware.IvtLoadProcess * 4,        loadProcess);
        return image;
    }

    // ContextSwitch: persist the interrupted process (if any), then resume the next
    // Ready process after it.
    private static void EmitContextSwitch(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("cs_skip");                         // no current -> nothing to save
        EntryAddress(asm, ECX, EBX);              // EBX = current entry
        asm.SaveRegs(R(EBX));
        asm.Label("cs_skip");
        asm.Jmp("resume_next");                    // ECX is the scan basis
    }

    // Schedule: called when the CPU is idle; resume any Ready process.
    private static void EmitSchedule(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));                 // ECX = currentIndex (-1 when idle)
        asm.Jmp("resume_next");
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
        asm.Jmp("resume_next");
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
        asm.Jmp("resume_next");
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
        asm.Jmp("resume_next");
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

    // Wake: a device interrupt for the reason in EAX fired. Make one process waiting
    // on that reason Ready, then resume the interrupted process unchanged (a device
    // interrupt does not preempt the running process).
    private static void EmitWake(Assembler asm)
    {
        asm.Mov(R(EDX), R(EAX));                   // EDX = target wait reason
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                 // EDI = count
        asm.MovImm(R(ESI), 0);                     // ESI = index

        asm.Label("wk_scan");
        asm.Mov(R(EAX), R(EDI));
        asm.Cmp(R(EAX), R(ESI));                   // count - i
        asm.Js("wk_resume");                       // i > count: scanned all
        asm.Jz("wk_resume");                       // i == count: scanned all
        EntryAddress(asm, ESI, EBX);              // EBX = entry[i]
        LoadField(asm, EBX, Hardware.ProcessEntryState, EAX);
        asm.MovImm(R(EBP), Blocked);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jnz("wk_next");                        // not blocked
        LoadField(asm, EBX, Hardware.ProcessEntryWaitReason, EAX);
        asm.Cmp(R(EAX), R(EDX));
        asm.Jnz("wk_next");                        // not waiting on this reason
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        asm.Jmp("wk_resume");                      // wake exactly one

        asm.Label("wk_next");
        asm.Inc(R(ESI));
        asm.Jmp("wk_scan");

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

    // Shared tail: scan from ECX+1 (wrapping) for the next Ready process, switch to
    // it, and resume it; if none are Ready, record idle (currentIndex = -1).
    private static void EmitResumeTail(Assembler asm)
    {
        asm.Label("resume_next");
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));                 // EDI = count
        asm.MovImm(R(ESI), 0);                     // ESI = i

        asm.Label("rn_scan");
        asm.Inc(R(ESI));
        asm.Mov(R(EAX), R(EDI));
        asm.Cmp(R(EAX), R(ESI));                   // count - i
        asm.Js("rn_idle");                         // i > count: none Ready
        asm.Mov(R(EBX), R(ECX));
        asm.Add(R(EBX), R(ESI));                   // candidate = current + i
        asm.Mov(R(EAX), R(EDI));
        asm.Cmp(R(EBX), R(EAX));                   // candidate - count
        asm.Js("rn_have");
        asm.Sub(R(EBX), R(EDI));                    // wrap once into [0, count)
        asm.Label("rn_have");
        asm.Mov(R(EDX), R(EBX));                    // EDX = candidate index
        asm.MovImm(R(EAX), EntrySize);
        asm.Mul(R(EBX), R(EAX));
        Imm16(asm, EAX, OsLayout.ProcessTableOffset);
        asm.Add(R(EBX), R(EAX));                    // EBX = candidate entry
        LoadField(asm, EBX, Hardware.ProcessEntryState, EAX);
        asm.MovImm(R(EBP), Ready);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jnz("rn_scan");                        // not Ready: keep scanning

        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(EDX));                  // currentIndex = candidate
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));

        asm.Label("rn_idle");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));                            // EBX = -1
        asm.Store(R(EAX), R(EBX));                  // currentIndex = -1
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
