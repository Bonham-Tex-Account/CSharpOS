namespace CSharpOS;

// Filesystem: IvtFsOp dispatch + fs_* block/chain/directory subroutines.
public static partial class OsRoutines
{
    // ---- filesystem block allocator + free-chaining (Increment 3) -----------

    // ===== EmitFsOp ==========================================================
    // IvtFsOp: the filesystem control interface. Op selector in EAX; block arg in EBX;
    // ChainSetNext's next arg in ECX. Sets up the privileged stack, routes to a block-layer
    // subroutine, and parks the result (an allocated block, a chain pointer, else 0) in the
    // FsResult header word. Grows with more selectors as later increments add directory ops.
    private static void EmitFsOp(Assembler asm)
    {
        asm.Label("fs_op");
        SetupPrivilegedStack(asm);
        asm.Mov(R(R13), R(EAX));                 // R13 = op selector
        asm.MovImm(R(R14), Hardware.FsOpFormat);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_format");
        asm.MovImm(R(R14), Hardware.FsOpAllocBlock);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_alloc");
        asm.MovImm(R(R14), Hardware.FsOpFreeBlock);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_free");
        asm.MovImm(R(R14), Hardware.FsOpChainNext);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_next");
        asm.MovImm(R(R14), Hardware.FsOpChainSetNext);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_setnext");
        asm.MovImm(R(R14), Hardware.FsOpRootDir);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_rootdir");
        asm.MovImm(R(R14), Hardware.FsOpHash);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_hash");
        asm.MovImm(R(R14), Hardware.FsOpLookup);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_lookup");
        asm.MovImm(R(R14), Hardware.FsOpInsert);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_insert");
        asm.MovImm(R(R14), Hardware.FsOpRemove);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_remove");
        asm.MovImm(R(R12), 0);                   // unknown op
        asm.Jmp("fo_result");

        asm.Label("fo_format");
        asm.Call("fs_format");
        asm.MovImm(R(R12), 0);
        asm.Jmp("fo_result");
        asm.Label("fo_alloc");
        asm.Call("fs_alloc_block");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_free");
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_free_block");
        asm.MovImm(R(R12), 0);
        asm.Jmp("fo_result");
        asm.Label("fo_next");
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_chain_next");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_setnext");
        asm.Mov(R(EAX), R(EBX));                 // block; ECX still carries the caller's next arg
        asm.Call("fs_chain_set_next");
        asm.MovImm(R(R12), 0);
        asm.Jmp("fo_result");
        asm.Label("fo_rootdir");
        asm.Call("fs_root_dir");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_hash");
        asm.Mov(R(EAX), R(EBX));                 // name addr
        asm.Call("fs_hash");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_lookup");
        asm.Mov(R(EAX), R(EBX));                 // dir block; ECX = name addr
        asm.Call("fs_dir_lookup");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_insert");
        asm.Call("fs_dir_insert");               // EBX=dir, ECX=name, EDX=type, ESI=first
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_remove");
        asm.Mov(R(EAX), R(EBX));                 // dir block; ECX = name addr
        asm.Call("fs_dir_remove");
        asm.Mov(R(R12), R(EAX));

        asm.Label("fo_result");
        Imm16(asm, EBP, OsLayout.FsResultOffset);
        asm.Store(R(EBP), R(R12));
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ===== EmitFsSubroutines =================================================
    // Block layer over the file-block region, all going through the cache. Convention: block
    // arrives in EAX; results come back in EAX. EDX/EDI carry a value across cache_* calls
    // (the cache subroutines never touch EDX/EDI); R8-R15/EAX/EBP are scratch. Bitmap bit = 1
    // means the block is allocated.
    private static void EmitFsSubroutines(Assembler asm)
    {
        // ---- fs_format: initial superblock + empty bitmap (blocks 0,1 reserved) ----
        asm.Label("fs_format");
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_get");                   // EAX = bitmap data addr
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(R9), 0);
        asm.Label("ff_zero");
        asm.MovImm(R(R10), FsLayout.BitmapWords);
        asm.Cmp(R(R9), R(R10));
        asm.Jns("ff_zero_done");
        asm.Mov(R(R11), R(R9));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));
        asm.MovImm(R(EAX), 0);
        asm.Store(R(R11), R(EAX));
        asm.Inc(R(R9));
        asm.Jmp("ff_zero");
        asm.Label("ff_zero_done");
        asm.MovImm(R(EAX), 3);                   // bits 0,1 set: blocks 0,1 in use
        asm.Store(R(R8), R(EAX));
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_dirty");
        // allocate the root directory block (its entries are zero = all free on a fresh disk)
        asm.Call("fs_alloc_block");
        asm.Mov(R(EDI), R(EAX));                 // EDI = root dir block (preserved across cache_get)
        // superblock: magic, block count, free count, root dir
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_get");                   // EAX = superblock data addr
        asm.Mov(R(R8), R(EAX));
        asm.MovImm16(R(EAX), FsLayout.SuperMagic);
        asm.Store(R(R8), R(EAX));                // magic at offset 0
        asm.MovImm16(R(EAX), FsLayout.BlockCount);
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(R10), FsLayout.SuperBlockCountOffset);
        asm.Add(R(R9), R(R10));
        asm.Store(R(R9), R(EAX));
        asm.MovImm16(R(EAX), FsLayout.BlockCount - FsLayout.FirstDataBlock - 1);
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(R10), FsLayout.SuperFreeCountOffset);
        asm.Add(R(R9), R(R10));
        asm.Store(R(R9), R(EAX));
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(R10), FsLayout.SuperRootDirOffset);
        asm.Add(R(R9), R(R10));
        asm.Store(R(R9), R(EDI));                // root dir block
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_dirty");
        asm.Ret();

        // ---- fs_alloc_block: → EAX = allocated block, or -1 if full ----
        asm.Label("fs_alloc_block");
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));                   // R8 = bitmap base
        asm.MovImm(R(R9), 0);                     // R9 = word index
        asm.Label("fa_word");
        asm.MovImm(R(R10), FsLayout.BitmapWords);
        asm.Cmp(R(R9), R(R10));
        asm.Jns("fa_full");
        asm.Mov(R(R11), R(R9));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));                   // R11 = &bitmap[word]
        asm.Load(R(R12), R(R11));                 // R12 = word bits
        asm.MovImm(R(R13), 0);                    // R13 = bit index
        asm.Label("fa_bit");
        asm.MovImm(R(EAX), 32);
        asm.Cmp(R(R13), R(EAX));
        asm.Jns("fa_next_word");
        asm.Mov(R(R14), R(R12));
        asm.Shr(R(R14), R(R13));
        asm.MovImm(R(EAX), 1);
        asm.And(R(R14), R(EAX));
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R14), R(EAX));
        asm.Jz("fa_found");                       // bit == 0 → free block
        asm.Inc(R(R13));
        asm.Jmp("fa_bit");
        asm.Label("fa_next_word");
        asm.Inc(R(R9));
        asm.Jmp("fa_word");

        asm.Label("fa_found");
        asm.Mov(R(EDX), R(R9));                   // block = word*32 + bit → EDX
        asm.MovImm(R(EAX), 32);
        asm.Mul(R(EDX), R(EAX));
        asm.Add(R(EDX), R(R13));
        asm.MovImm(R(EAX), 1);
        asm.Shl(R(EAX), R(R13));
        asm.Or(R(R12), R(EAX));                   // set the bit
        asm.Store(R(R11), R(R12));
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_dirty");
        // initialise the new block's next-pointer = -1 (end of chain)
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(EAX), FsLayout.NextPtrOffset);
        asm.Add(R(R8), R(EAX));
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Store(R(R8), R(EAX));
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_dirty");
        asm.Mov(R(EAX), R(EDX));                  // return the block number
        asm.Ret();
        asm.Label("fa_full");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_free_block: EAX = block (clear bitmap bit, discard from cache) ----
        asm.Label("fs_free_block");
        asm.Mov(R(EDX), R(EAX));                  // EDX = block
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));
        asm.Mov(R(R9), R(EDX));
        asm.MovImm(R(EAX), 32);
        asm.Div(R(R9), R(EAX));                   // R9 = word = block/32
        asm.Mov(R(R10), R(R9));
        asm.MovImm(R(EAX), 32);
        asm.Mul(R(R10), R(EAX));
        asm.Mov(R(R13), R(EDX));
        asm.Sub(R(R13), R(R10));                  // R13 = bit = block - word*32
        asm.Mov(R(R11), R(R9));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));                   // R11 = &bitmap[word]
        asm.Load(R(R12), R(R11));
        asm.MovImm(R(EAX), 1);
        asm.Shl(R(EAX), R(R13));
        asm.Not(R(EAX));
        asm.And(R(R12), R(EAX));                  // clear the bit
        asm.Store(R(R11), R(R12));
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_dirty");
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_discard");               // drop the freed block's cached copy
        asm.Ret();

        // ---- fs_chain_next: EAX = block → EAX = next-block link ----
        asm.Label("fs_chain_next");
        asm.Call("cache_get");
        asm.Mov(R(R9), R(EAX));
        asm.MovImm(R(EAX), FsLayout.NextPtrOffset);
        asm.Add(R(R9), R(EAX));
        asm.Load(R(EAX), R(R9));
        asm.Ret();

        // ---- fs_chain_set_next: EAX = block, ECX = next ----
        asm.Label("fs_chain_set_next");
        asm.Mov(R(EDI), R(ECX));                  // EDI = next (preserved across cache_*)
        asm.Mov(R(EDX), R(EAX));                  // EDX = block
        asm.Call("cache_get");
        asm.Mov(R(R9), R(EAX));
        asm.MovImm(R(EAX), FsLayout.NextPtrOffset);
        asm.Add(R(R9), R(EAX));
        asm.Store(R(R9), R(EDI));
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_dirty");
        asm.Ret();
    }

    // ===== EmitFsDirSubroutines ==============================================
    // Directory tree over the block layer (Increment 4a): name hashing, root-dir lookup, and
    // single-directory lookup/insert/remove with duplicate rejection. Names are word-per-char
    // (null-padded to NameMaxChars). Because the cache subroutines clobber nearly every
    // register, any state that must persist across a nested cache/chain call is spilled to the
    // OsLayout.FsScratch* words rather than kept in registers (EDX/EDI are the only registers
    // that survive a cache_get, and even those are lost across fs_alloc_block/chain_set_next).
    private static void EmitFsDirSubroutines(Assembler asm)
    {
        // ---- fs_hash: EAX = name addr → EAX = hash (h = h*31 + c over the chars) ----
        asm.Label("fs_hash");
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(R9), 0);                    // hash
        asm.MovImm(R(R10), 0);                   // i
        asm.Label("fh_loop");
        asm.MovImm(R(EAX), FsLayout.NameMaxChars);
        asm.Cmp(R(R10), R(EAX));
        asm.Jns("fh_done");
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));
        asm.Load(R(R12), R(R11));                // char word
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R12), R(EAX));
        asm.Jz("fh_done");                       // null terminator
        asm.MovImm(R(EAX), 31);
        asm.Mul(R(R9), R(EAX));
        asm.Add(R(R9), R(R12));
        asm.Inc(R(R10));
        asm.Jmp("fh_loop");
        asm.Label("fh_done");
        asm.Mov(R(EAX), R(R9));
        asm.Ret();

        // ---- fs_root_dir: → EAX = root directory block (from the superblock) ----
        asm.Label("fs_root_dir");
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_get");
        asm.MovImm(R(R9), FsLayout.SuperRootDirOffset);
        asm.Add(R(EAX), R(R9));
        asm.Load(R(EAX), R(EAX));
        asm.Ret();

        // ---- fs_dir_lookup: EAX=dir block, ECX=name addr → EAX = entry addr or -1 ----
        asm.Label("fs_dir_lookup");
        asm.Mov(R(EDI), R(ECX));                 // EDI = name addr (durable)
        asm.Mov(R(EDX), R(EAX));                 // EDX = current block (durable)
        asm.Mov(R(EAX), R(EDI));
        asm.Call("fs_hash");
        SpillStore(asm, OsLayout.FsScratchHash, EAX);
        asm.Label("dl_block");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDX), R(EAX));
        asm.Js("dl_notfound");                   // ran off the end of the chain
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));                  // R8 = block addr
        SpillLoad(asm, OsLayout.FsScratchHash, R9);   // R9 = target hash
        asm.MovImm(R(R10), 0);                   // entry index
        asm.Label("dl_entry");
        asm.MovImm(R(EAX), FsLayout.DirEntriesPerBlock);
        asm.Cmp(R(R10), R(EAX));
        asm.Jns("dl_next_block");
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), FsLayout.DirEntryBytes);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));                  // R11 = entry addr
        asm.Load(R(R12), R(R11));                // type
        asm.MovImm(R(EAX), FsLayout.DirTypeFree);
        asm.Cmp(R(R12), R(EAX));
        asm.Jz("dl_next_entry");
        asm.Mov(R(R13), R(R11));
        asm.MovImm(R(EAX), FsLayout.DirEntryHash);
        asm.Add(R(R13), R(EAX));
        asm.Load(R(R13), R(R13));                // entry hash
        asm.Cmp(R(R13), R(R9));
        asm.Jnz("dl_next_entry");                // hash mismatch → skip
        // verify the name word-by-word
        asm.Mov(R(R14), R(R11));
        asm.MovImm(R(EAX), FsLayout.DirEntryName);
        asm.Add(R(R14), R(EAX));                 // R14 = entry name addr
        asm.MovImm(R(R15), 0);
        asm.Label("dl_cmp");
        asm.MovImm(R(EAX), FsLayout.NameMaxChars);
        asm.Cmp(R(R15), R(EAX));
        asm.Jns("dl_match");
        asm.Mov(R(EBX), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        asm.Mov(R(ECX), R(R14));
        asm.Add(R(ECX), R(EBX));
        asm.Load(R(ECX), R(ECX));                // entry char
        asm.Mov(R(EBP), R(EDI));
        asm.Add(R(EBP), R(EBX));
        asm.Load(R(EBP), R(EBP));                // lookup char
        asm.Cmp(R(ECX), R(EBP));
        asm.Jnz("dl_next_entry");
        asm.Inc(R(R15));
        asm.Jmp("dl_cmp");
        asm.Label("dl_match");
        SpillStore(asm, OsLayout.FsScratchEntryBlock, EDX);
        asm.Mov(R(EAX), R(R11));
        asm.Ret();
        asm.Label("dl_next_entry");
        asm.Inc(R(R10));
        asm.Jmp("dl_entry");
        asm.Label("dl_next_block");
        asm.Mov(R(EAX), R(EDX));
        asm.Call("fs_chain_next");
        asm.Mov(R(EDX), R(EAX));
        asm.Jmp("dl_block");
        asm.Label("dl_notfound");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_dir_insert: EBX=dir, ECX=name, EDX=type, ESI=first → EAX=entry addr or -1 ----
        asm.Label("fs_dir_insert");
        SpillStore(asm, OsLayout.FsScratchDir, EBX);
        SpillStore(asm, OsLayout.FsScratchName, ECX);
        SpillStore(asm, OsLayout.FsScratchType, EDX);
        SpillStore(asm, OsLayout.FsScratchFirst, ESI);
        // reject duplicates
        asm.Mov(R(EAX), R(EBX));                 // dir; ECX already = name
        asm.Call("fs_dir_lookup");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jns("di_dup");                       // already present
        // scan the chain for a free entry slot, remembering the previous block
        SpillLoad(asm, OsLayout.FsScratchDir, EDX);   // current block
        asm.MovImm(R(EDI), 0);
        asm.Dec(R(EDI));                         // prev block = -1
        asm.Label("di_block");
        asm.Mov(R(EAX), R(EDX));
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(R10), 0);
        asm.Label("di_entry");
        asm.MovImm(R(EAX), FsLayout.DirEntriesPerBlock);
        asm.Cmp(R(R10), R(EAX));
        asm.Jns("di_next_block");
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), FsLayout.DirEntryBytes);
        asm.Mul(R(R11), R(EAX));
        asm.Add(R(R11), R(R8));
        asm.Load(R(R12), R(R11));
        asm.MovImm(R(EAX), FsLayout.DirTypeFree);
        asm.Cmp(R(R12), R(EAX));
        asm.Jz("di_found_slot");
        asm.Inc(R(R10));
        asm.Jmp("di_entry");
        asm.Label("di_found_slot");
        SpillStore(asm, OsLayout.FsScratchEntryBlock, EDX);
        asm.Jmp("di_write");
        asm.Label("di_next_block");
        asm.Mov(R(EDI), R(EDX));                 // prev = current
        asm.Mov(R(EAX), R(EDX));
        asm.Call("fs_chain_next");
        asm.Mov(R(EDX), R(EAX));
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(EDX), R(EAX));
        asm.Jns("di_block");                     // more blocks → keep scanning
        // end of chain, no free slot → extend with a new block
        asm.Call("fs_alloc_block");              // EAX = new block; EDI (prev) preserved
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("di_full");
        SpillStore(asm, OsLayout.FsScratchFreeBlock, EAX);
        asm.Mov(R(ECX), R(EAX));                 // next = new
        asm.Mov(R(EAX), R(EDI));                 // block = prev
        asm.Call("fs_chain_set_next");
        SpillLoad(asm, OsLayout.FsScratchFreeBlock, EAX);
        SpillStore(asm, OsLayout.FsScratchEntryBlock, EAX);
        asm.Call("cache_get");
        asm.Mov(R(R11), R(EAX));                 // entry addr = first entry of the new block
        asm.Jmp("di_write");
        asm.Label("di_dup");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
        asm.Label("di_full");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
        asm.Label("di_write");
        asm.Mov(R(R13), R(R11));                 // R13 = entry addr (survives fs_hash/cache_dirty)
        SpillLoad(asm, OsLayout.FsScratchType, EAX);
        asm.Store(R(R13), R(EAX));               // type at offset 0
        SpillLoad(asm, OsLayout.FsScratchName, EAX);
        asm.Call("fs_hash");
        asm.Mov(R(R14), R(R13));
        asm.MovImm(R(EBX), FsLayout.DirEntryHash);
        asm.Add(R(R14), R(EBX));
        asm.Store(R(R14), R(EAX));
        asm.Mov(R(R14), R(R13));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R14), R(EBX));
        SpillLoad(asm, OsLayout.FsScratchFirst, EAX);
        asm.Store(R(R14), R(EAX));
        asm.Mov(R(R14), R(R13));
        asm.MovImm(R(EBX), FsLayout.DirEntrySizeField);
        asm.Add(R(R14), R(EBX));
        asm.MovImm(R(EAX), 0);
        asm.Store(R(R14), R(EAX));               // size = 0
        // copy the name (NameMaxChars words) into the entry
        SpillLoad(asm, OsLayout.FsScratchName, R8);
        asm.Mov(R(R9), R(R13));
        asm.MovImm(R(EBX), FsLayout.DirEntryName);
        asm.Add(R(R9), R(EBX));
        asm.MovImm(R(R10), 0);
        asm.Label("di_name_copy");
        asm.MovImm(R(EAX), FsLayout.NameMaxChars);
        asm.Cmp(R(R10), R(EAX));
        asm.Jns("di_name_done");
        asm.Mov(R(R11), R(R10));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R11), R(EAX));
        asm.Mov(R(R12), R(R8));
        asm.Add(R(R12), R(R11));
        asm.Load(R(R12), R(R12));
        asm.Mov(R(EBX), R(R9));
        asm.Add(R(EBX), R(R11));
        asm.Store(R(EBX), R(R12));
        asm.Inc(R(R10));
        asm.Jmp("di_name_copy");
        asm.Label("di_name_done");
        SpillLoad(asm, OsLayout.FsScratchEntryBlock, EAX);
        asm.Call("cache_dirty");
        asm.Mov(R(EAX), R(R13));                 // return entry addr
        asm.Ret();

        // ---- fs_dir_remove: EAX=dir, ECX=name → EAX = 0, or -1 if absent ----
        asm.Label("fs_dir_remove");
        asm.Call("fs_dir_lookup");               // stashes the matched block in FsScratchEntryBlock
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("dr_notfound");
        asm.Mov(R(R13), R(EAX));                 // entry addr
        asm.MovImm(R(EAX), 0);
        asm.Store(R(R13), R(EAX));               // type = free
        SpillLoad(asm, OsLayout.FsScratchEntryBlock, EAX);
        asm.Call("cache_dirty");
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("dr_notfound");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }
}
