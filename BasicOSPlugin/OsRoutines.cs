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
/// R8-R15 are used by ContextSwitch, resume_mlfq, and the buddy allocator.
///
/// Buddy allocator bitmap: 1 bit per tree node (bit=1 FREE, bit=0 used/split),
/// stored as 8 × 32-bit words at BuddyBitmapOffset. Node i (1-indexed) → bit i-1
/// → word (i-1)/32, bit-in-word (i-1)%32. Bit ops (AND/OR/XOR/NOT/SHL/SHR) are
/// used to pack/unpack bits. For heaps with ≤32 nodes (4-level, common case) every
/// tree operation touches only word 0.
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
        int loadProcess   = OsLayout.CodeBase + asm.CodeLength; EmitBuddyAlloc(asm);
        EmitBuddyFree(asm);     // label "buddy_free_entry"; ends with Jmp("resume_mlfq")
        EmitResumeMlfq(asm);    // label "resume_mlfq"

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

    // ---- scheduling routines (unchanged) ------------------------------------

    private static void EmitContextSwitch(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("cs_skip");

        EntryAddress(asm, ECX, EBX);
        asm.SaveRegs(R(EBX));

        LoadField(asm, EBX, Hardware.ProcessEntryTicksUsed, R8);
        asm.Inc(R(R8));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryTicksUsed, R8);

        LoadField(asm, EBX, Hardware.ProcessEntryPriority, R9);
        asm.MovImm(R(R10), OsLayout.QueueCount - 1);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("cs_no_demote");

        asm.Mov(R(R10), R(R9));
        asm.MovImm(R(R11), 4);
        asm.Mul(R(R10), R(R11));
        asm.MovImm16(R(R11), OsLayout.QuantumTableOffset);
        asm.Add(R(R10), R(R11));
        asm.Load(R(R10), R(R10));

        asm.Cmp(R(R8), R(R10));
        asm.Js("cs_no_demote");

        asm.Inc(R(R9));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryPriority, R9);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_no_demote");

        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.Load(R(R10), R(R8));
        asm.Dec(R(R10));
        asm.Store(R(R8), R(R10));

        asm.MovImm(R(R11), 0);
        asm.Cmp(R(R10), R(R11));
        asm.Jnz("cs_boost_skip");

        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));
        asm.MovImm(R(ESI), 0);

        asm.Label("cs_boost_loop");
        asm.Mov(R(R11), R(EDI));
        asm.Cmp(R(R11), R(ESI));
        asm.Jz("cs_boost_done");
        asm.Js("cs_boost_done");

        asm.Mov(R(R12), R(ESI));
        asm.MovImm(R(R13), EntrySize);
        asm.Mul(R(R12), R(R13));
        asm.MovImm16(R(R13), OsLayout.ProcessTableOffset);
        asm.Add(R(R12), R(R13));

        LoadField(asm, R12, Hardware.ProcessEntryState, R13);
        asm.MovImm(R(R14), Terminated);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("cs_boost_skip_entry");

        StoreFieldImm(asm, R12, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, R12, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("cs_boost_skip_entry");
        asm.Inc(R(ESI));
        asm.Jmp("cs_boost_loop");

        asm.Label("cs_boost_done");
        Imm16(asm, R8, OsLayout.BoostTimerOffset);
        asm.MovImm(R(R10), OsLayout.BoostInterval);
        asm.Store(R(R8), R(R10));

        asm.Label("cs_boost_skip");
        asm.Label("cs_skip");
        asm.Jmp("resume_mlfq");
    }

    private static void EmitSchedule(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");
    }

    private static void EmitBlock(Assembler asm)
    {
        asm.Mov(R(EDX), R(EAX));
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Blocked);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryWaitReason, EDX);
        asm.SaveRegs(R(EBX));
        asm.Jmp("resume_mlfq");
    }

    // Halt and InvalidInstruction mark the process Terminated then jump to the
    // shared buddy-free routine which returns its memory and falls through to
    // resume_mlfq.
    private static void EmitHalt(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        asm.Jmp("buddy_free_entry");
    }

    private static void EmitInvalidInstruction(Assembler asm)
    {
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        asm.Jmp("buddy_free_entry");
    }

    // ---- buddy allocator ---------------------------------------------------

    // BuddyAlloc: allocate memory for the staged process-table entry (address in EAX).
    // Reads entry.TotalSize, walks the buddy tree to find the smallest free block that
    // fits, splits ancestors as needed, records the base address in entry.ProgramAddress.
    // Sets ProgramAddress = -1 when no block fits. Returns via OSRET (no process switch).
    //
    // Registers during execution:
    //   EBX = entry address, ECX = needed (TotalSize)
    //   ESI = targetLevel, EDI = blockSize at targetLevel
    //   EDX = BuddyLevels (max depth), R9 = HeapSize (for level computation)
    //   R8  = searchLevel (outer scan; decrements toward root)
    //   R10 = current scan node (inner loop) or leftChild (split loop)
    //   R11, R12, R13 = scratch within split/merge steps
    //   EAX, EBP, R14, R15 = dedicated scratch for bit operations
    private static void EmitBuddyAlloc(Assembler asm)
    {
        asm.Mov(R(EBX), R(EAX));                          // EBX = entry

        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, ECX); // ECX = needed

        // Load heap parameters from OS data.
        Imm16(asm, EAX, OsLayout.BuddyHeapSizeOffset);
        asm.Load(R(R9), R(EAX));                           // R9 = HeapSize
        Imm16(asm, EAX, OsLayout.BuddyLevelsOffset);
        asm.Load(R(EDX), R(EAX));                          // EDX = BuddyLevels

        // Compute target level: smallest level where blockSize >= needed.
        // Start at level 0 (blockSize = HeapSize), halve until blockSize/2 < needed.
        asm.MovImm(R(ESI), 0);                             // ESI = targetLevel = 0
        asm.Mov(R(R10), R(R9));                            // R10 = currentBlockSize = HeapSize

        asm.Label("ba_find_level");
        asm.MovImm(R(R11), 2);
        asm.Mov(R(EBP), R(R10));
        asm.Div(R(EBP), R(R11));                           // EBP = currentBlockSize / 2
        asm.Cmp(R(EBP), R(ECX));
        asm.Js("ba_level_done");                           // blockSize/2 < needed: stop here
        asm.Mov(R(R11), R(EDX));
        asm.Cmp(R(R11), R(ESI));
        asm.Jz("ba_level_done");                           // targetLevel == BuddyLevels: stop
        asm.Js("ba_level_done");                           // targetLevel > BuddyLevels: stop
        asm.Inc(R(ESI));                                   // targetLevel++
        asm.Mov(R(R10), R(EBP));                           // blockSize = blockSize/2
        asm.Jmp("ba_find_level");

        asm.Label("ba_level_done");
        asm.Mov(R(EDI), R(R10));                           // EDI = blockSize at targetLevel (save)

        // Guard: if the smallest available block (blockSize) is still less than needed
        // (happens when needed > heapSize), fail immediately.
        asm.Cmp(R(EDI), R(ECX));
        asm.Js("ba_fail");

        // Scan from targetLevel up toward root for any free node.
        // R8 = searchLevel (starts at targetLevel, decrements toward 0).
        asm.Mov(R(R8), R(ESI));                            // R8 = searchLevel = targetLevel

        asm.Label("ba_scan_outer");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Js("ba_fail");                                 // searchLevel < 0: no memory

        // firstNode = 1 << searchLevel; endNode = 2 * firstNode.
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(R8));                           // EBP = 2^searchLevel = firstNode
        asm.Mov(R(R9), R(EBP));
        asm.Add(R(R9), R(EBP));                            // R9 = endNode = 2 * firstNode

        asm.Mov(R(R10), R(EBP));                           // R10 = currentScanNode = firstNode

        asm.Label("ba_scan_inner");
        asm.Cmp(R(R9), R(R10));
        asm.Jz("ba_scan_next_level");                      // currentNode == endNode: exhausted
        asm.Js("ba_scan_next_level");

        // Check if bit(R10) is set (node is free).
        EmitReadBit(asm, R10);                             // sets ZF if bit=0; clobbers EAX,EBP,R14,R15
        asm.Jz("ba_scan_bit_zero");
        asm.Jmp("ba_found");                               // bit=1: this node is free

        asm.Label("ba_scan_bit_zero");
        asm.Inc(R(R10));
        asm.Jmp("ba_scan_inner");

        asm.Label("ba_scan_next_level");
        asm.Dec(R(R8));                                    // searchLevel--
        asm.Jmp("ba_scan_outer");

        // Found a free node at R10, searchLevel in R8.
        asm.Label("ba_found");
        asm.Cmp(R(R8), R(ESI));
        asm.Jz("ba_exact");                                // foundLevel == targetLevel: just allocate

        // Split from R8 (foundLevel) down to ESI (targetLevel).
        // At each step: clear bit(currentNode), set bit(rightChild), descend left.
        asm.Mov(R(R11), R(R10));                           // R11 = currentSplitNode

        asm.Label("ba_split");
        asm.Cmp(R(R8), R(ESI));
        asm.Jz("ba_split_done");                           // reached targetLevel: R11 is allocated

        EmitClearBit(asm, R11);                            // clear ancestor (now split)

        asm.MovImm(R(EAX), 2);
        asm.Mov(R(R12), R(R11));
        asm.Mul(R(R12), R(EAX));                           // R12 = leftChild = 2*R11
        asm.Mov(R(R13), R(R12));
        asm.Inc(R(R13));                                   // R13 = rightChild = 2*R11+1

        EmitSetBit(asm, R13);                              // right child = free (buddy)

        asm.Mov(R(R11), R(R12));                           // descend into left child
        asm.Inc(R(R8));                                    // level++
        asm.Jmp("ba_split");

        asm.Label("ba_split_done");
        // R11 is at targetLevel; its bit was never set → it is the allocated block.
        asm.Jmp("ba_addr");

        asm.Label("ba_exact");
        // R10 is at targetLevel and is free; clear its bit to allocate it.
        EmitClearBit(asm, R10);
        asm.Mov(R(R11), R(R10));                           // R11 = allocated node

        // Compute physical address: HeapStart + (node - 2^targetLevel) * blockSize.
        asm.Label("ba_addr");
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(ESI));                          // EBP = 2^targetLevel
        asm.Sub(R(R11), R(EBP));                          // R11 = block_j = node - firstNode
        asm.Mul(R(R11), R(EDI));                          // R11 = block_j * blockSize

        Imm16(asm, EAX, OsLayout.BuddyHeapStartOffset);
        asm.Load(R(EBP), R(EAX));                         // EBP = HeapStart
        asm.Add(R(R11), R(EBP));                          // R11 = PhysAddr

        asm.Mov(R(EBP), R(EBX));
        asm.MovImm(R(EAX), Hardware.ProcessEntryProgramAddress);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(R11));
        asm.Jmp("ba_done");

        asm.Label("ba_fail");
        asm.Mov(R(EBP), R(EBX));
        asm.MovImm(R(EAX), Hardware.ProcessEntryProgramAddress);
        asm.Add(R(EBP), R(EAX));
        asm.MovImm(R(R11), 0);
        asm.Dec(R(R11));                                   // R11 = -1
        asm.Store(R(EBP), R(R11));

        asm.Label("ba_done");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // BuddyFree: mark the terminated process's memory block as free in the buddy tree
    // and merge with its buddy recursively while the buddy is also free. Expects
    // EBX = process-table entry (already marked Terminated). Ends with Jmp("resume_mlfq").
    //
    // Registers:
    //   EBX = entry, R9 = programAddress, R10 = totalSize
    //   ESI = level, EDI = blockSize at level
    //   EDX = BuddyLevels, R11 = heapSize (level computation)
    //   R8 = current level (merge loop), R10 = current node (merge loop)
    //   R11 = buddy node (merge loop), R12 = parent node (merge loop)
    //   EAX, EBP, R14, R15 = bit-op scratch
    private static void EmitBuddyFree(Assembler asm)
    {
        asm.Label("buddy_free_entry");

        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9);  // R9 = programAddress
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);       // R10 = totalSize

        // Load heap parameters.
        Imm16(asm, EAX, OsLayout.BuddyHeapSizeOffset);
        asm.Load(R(R11), R(EAX));                          // R11 = heapSize

        // Skip bitmap update if heap is not configured (heapSize == 0) or the entry
        // was never allocated via the buddy allocator (TotalSize == 0).
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R11), R(EAX));
        asm.Jz("bf_done");                                 // heapSize == 0: no heap
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("bf_done");                                 // totalSize == 0: not a buddy alloc

        Imm16(asm, EAX, OsLayout.BuddyLevelsOffset);
        asm.Load(R(EDX), R(EAX));                          // EDX = BuddyLevels

        // Compute level (same rule as alloc): halve blockSize while blockSize/2 >= totalSize.
        asm.MovImm(R(ESI), 0);                             // ESI = level = 0
        asm.Mov(R(EDI), R(R11));                           // EDI = currentBlockSize = heapSize

        asm.Label("bf_find_level");
        asm.MovImm(R(EAX), 2);
        asm.Mov(R(EBP), R(EDI));
        asm.Div(R(EBP), R(EAX));                          // EBP = blockSize / 2
        asm.Cmp(R(EBP), R(R10));
        asm.Js("bf_level_done");                           // blockSize/2 < totalSize: stop
        asm.Mov(R(EAX), R(EDX));
        asm.Cmp(R(EAX), R(ESI));
        asm.Jz("bf_level_done");
        asm.Js("bf_level_done");
        asm.Inc(R(ESI));
        asm.Mov(R(EDI), R(EBP));
        asm.Jmp("bf_find_level");

        asm.Label("bf_level_done");
        // ESI = level, EDI = blockSize at level.

        // block_j = (programAddress - HeapStart) / blockSize.
        Imm16(asm, EAX, OsLayout.BuddyHeapStartOffset);
        asm.Load(R(EBP), R(EAX));                         // EBP = HeapStart
        asm.Sub(R(R9), R(EBP));                           // R9 = offset from heap start
        asm.Div(R(R9), R(EDI));                            // R9 = block_j

        // node = 2^level + block_j.
        asm.MovImm(R(EBP), 1);
        asm.Shl(R(EBP), R(ESI));                          // EBP = 2^level
        asm.Add(R(R9), R(EBP));                            // R9 = node (1-indexed)

        // Mark the freed block as free.
        EmitSetBit(asm, R9);                               // bit(R9) = 1

        // Merge loop: while level > 0 and buddy is free, merge with buddy.
        asm.Mov(R(R8), R(ESI));                            // R8 = current level
        asm.Mov(R(R10), R(R9));                            // R10 = current node

        asm.Label("bf_merge");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R8), R(EAX));
        asm.Jz("bf_done");                                 // level == 0: at root, done

        // buddy = currentNode XOR 1.
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), 1);
        asm.Xor(R(R11), R(EAX));                          // R11 = buddy index

        // Check if buddy is free.
        EmitReadBit(asm, R11);                             // ZF set if buddy bit=0 (not free)
        asm.Jz("bf_done");                                 // buddy not free: stop merging

        // Both current and buddy are free: merge into parent.
        EmitClearBit(asm, R10);                            // clear current node
        EmitClearBit(asm, R11);                            // clear buddy

        // parent = currentNode / 2.
        asm.MovImm(R(EAX), 2);
        asm.Mov(R(R12), R(R10));
        asm.Div(R(R12), R(EAX));                          // R12 = parent

        EmitSetBit(asm, R12);                              // parent = free

        asm.Mov(R(R10), R(R12));                           // ascend to parent
        asm.Dec(R(R8));                                    // level--
        asm.Jmp("bf_merge");

        asm.Label("bf_done");
        asm.Jmp("resume_mlfq");
    }

    // ---- bit operation helpers ---------------------------------------------
    // Each helper operates on the buddy bitmap stored in OS data memory.
    // Node index (1-indexed) is passed in nodeReg.
    // Scratch registers clobbered: EAX, EBP, R14, R15.
    // After EmitReadBit: ZF is set if the bit is 0 (node NOT free).

    // Computes word_addr → EBP, mask (1 << bit_in_word) → R15, bit_in_word → EAX.
    // Clobbers EAX, EBP, R14, R15.
    private static void EmitComputeBitAddress(Assembler asm, byte nodeReg)
    {
        // EAX = nodeReg - 1  (bit_pos, 0-indexed)
        asm.Mov(R(EAX), R(nodeReg));
        asm.Dec(R(EAX));

        // R14 = word_idx = bit_pos / 32
        asm.Mov(R(R14), R(EAX));
        asm.MovImm(R(EBP), 32);
        asm.Div(R(R14), R(EBP));

        // EAX = bit_in_word = bit_pos - word_idx*32
        asm.Mov(R(R15), R(R14));
        asm.Mul(R(R15), R(EBP));                          // R15 = word_idx * 32
        asm.Sub(R(EAX), R(R15));                          // EAX = bit_in_word

        // EBP = word_addr = BuddyBitmapOffset + word_idx * 4
        asm.MovImm(R(EBP), 4);
        asm.Mov(R(R15), R(R14));
        asm.Mul(R(R15), R(EBP));                          // R15 = word_idx * 4
        asm.MovImm16(R(EBP), OsLayout.BuddyBitmapOffset);
        asm.Add(R(EBP), R(R15));                          // EBP = word_addr

        // R15 = mask = 1 << bit_in_word
        asm.MovImm(R(R15), 1);
        asm.Shl(R(R15), R(EAX));                          // R15 = mask
    }

    // ReadBit: ZF set if bit(nodeReg) == 0. Clobbers EAX, EBP, R14, R15.
    private static void EmitReadBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.And(R(R14), R(R15));                           // R14 &= mask; ZF set if bit=0
    }

    // SetBit: set bit(nodeReg) = 1 (mark free). Clobbers EAX, EBP, R14, R15.
    private static void EmitSetBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.Or(R(R14), R(R15));                            // R14 |= mask (set bit)
        asm.Store(R(EBP), R(R14));
    }

    // ClearBit: set bit(nodeReg) = 0 (mark used). Clobbers EAX, EBP, R14, R15.
    private static void EmitClearBit(Assembler asm, byte nodeReg)
    {
        EmitComputeBitAddress(asm, nodeReg);               // EBP=word_addr, R15=mask
        asm.Load(R(R14), R(EBP));                         // R14 = word value
        asm.Not(R(R15));                                   // R15 = ~mask
        asm.And(R(R14), R(R15));                           // R14 &= ~mask (clear bit)
        asm.Store(R(EBP), R(R14));
    }

    // ---- wake routines (unchanged) ----------------------------------------

    private static void EmitWakeEntry(Assembler asm, int reason)
    {
        asm.MovImm(R(EBP), reason);
        asm.Jmp("wk_body");
    }

    private static void EmitWakeBody(Assembler asm)
    {
        asm.Label("wk_body");
        asm.Mov(R(EDX), R(EBP));
        EntryAddress(asm, EAX, EBX);
        LoadField(asm, EBX, Hardware.ProcessEntryState, EAX);
        asm.MovImm(R(EBP), Blocked);
        asm.Cmp(R(EAX), R(EBP));
        asm.Jnz("wk_resume");
        LoadField(asm, EBX, Hardware.ProcessEntryWaitReason, EAX);
        asm.Cmp(R(EAX), R(EDX));
        asm.Jnz("wk_resume");

        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        asm.Label("wk_resume");
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.MovImm(R(EDX), 0);
        asm.Cmp(R(ECX), R(EDX));
        asm.Js("wk_idle");
        EntryAddress(asm, ECX, EBX);
        asm.SaveRegs(R(EBX));
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
        asm.Label("wk_idle");
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ---- MLFQ scheduler tail (unchanged) -----------------------------------

    private static void EmitResumeMlfq(Assembler asm)
    {
        asm.Label("resume_mlfq");
        Imm16(asm, EAX, OsLayout.ProcessCountOffset);
        asm.Load(R(EDI), R(EAX));

        asm.MovImm(R(R8), 0);

        asm.Label("rn_level");
        asm.MovImm(R(R9), OsLayout.QueueCount);
        asm.Cmp(R(R8), R(R9));
        asm.Jns("rn_idle");

        asm.MovImm(R(ESI), 0);

        asm.Label("rn_scan");
        asm.Inc(R(ESI));
        asm.Mov(R(R9), R(EDI));
        asm.Cmp(R(R9), R(ESI));
        asm.Js("rn_next_level");

        asm.Mov(R(R10), R(ECX));
        asm.Add(R(R10), R(ESI));
        asm.Cmp(R(R10), R(EDI));
        asm.Js("rn_in_range");
        asm.Sub(R(R10), R(EDI));
        asm.Label("rn_in_range");

        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(R12), EntrySize);
        asm.Mul(R(R11), R(R12));
        asm.MovImm16(R(R12), OsLayout.ProcessTableOffset);
        asm.Add(R(R11), R(R12));

        LoadField(asm, R11, Hardware.ProcessEntryState, R13);
        asm.MovImm(R(R14), Ready);
        asm.Cmp(R(R13), R(R14));
        asm.Jnz("rn_scan");

        LoadField(asm, R11, Hardware.ProcessEntryPriority, R13);
        asm.Cmp(R(R13), R(R8));
        asm.Jnz("rn_scan");

        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Store(R(EAX), R(R10));
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
        asm.Dec(R(EBX));
        asm.Store(R(EAX), R(EBX));
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ---- emit helpers -------------------------------------------------------

    private static RegisterName R(byte index) { return (RegisterName)index; }

    private static void Imm16(Assembler asm, byte dest, int value)
    {
        asm.MovImm16(R(dest), value);
    }

    private static void EntryAddress(Assembler asm, byte indexReg, byte dest)
    {
        asm.Mov(R(dest), R(indexReg));
        asm.MovImm(R(EAX), EntrySize);
        asm.Mul(R(dest), R(EAX));
        asm.MovImm16(R(EAX), OsLayout.ProcessTableOffset);
        asm.Add(R(dest), R(EAX));
    }

    private static void LoadField(Assembler asm, byte entry, int fieldOffset, byte dest)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Load(R(dest), R(EBP));
    }

    private static void StoreFieldReg(Assembler asm, byte entry, int fieldOffset, byte valueReg)
    {
        asm.Mov(R(EBP), R(entry));
        asm.MovImm(R(EAX), fieldOffset);
        asm.Add(R(EBP), R(EAX));
        asm.Store(R(EBP), R(valueReg));
    }

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
