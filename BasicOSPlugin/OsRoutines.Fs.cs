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
        asm.MovImm(R(R14), Hardware.FsOpMkdir);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_mkdir");
        asm.MovImm(R(R14), Hardware.FsOpPathResolve);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_pathresolve");
        asm.MovImm(R(R14), Hardware.FsOpOpen);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_open");
        asm.MovImm(R(R14), Hardware.FsOpClose);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_close");
        asm.MovImm(R(R14), Hardware.FsOpRead);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_read");
        asm.MovImm(R(R14), Hardware.FsOpWrite);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_write");
        asm.MovImm(R(R14), Hardware.FsOpUnlink);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_unlink");
        asm.MovImm(R(R14), Hardware.FsOpMkdirPath);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_mkdirpath");
        asm.MovImm(R(R14), Hardware.FsOpReadDir);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("fo_readdir");
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
        asm.Jmp("fo_result");
        asm.Label("fo_mkdir");
        asm.Call("fs_mkdir");                    // EBX=parent dir, ECX=name
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_pathresolve");
        asm.Mov(R(EAX), R(EBX));                 // path addr
        asm.Call("fs_path_resolve");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_open");
        asm.Call("fs_open_core");                // EBX=abs path, ECX=flags, EDX=proc index
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_close");
        asm.Call("fs_close_core");               // EBX=fd, ECX=proc index
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_read");
        asm.Call("fs_read_core");                // EBX=fd, ECX=abs buf, EDX=count, ESI=proc
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_write");
        asm.Call("fs_write_core");               // EBX=fd, ECX=abs buf, EDX=count, ESI=proc
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_unlink");
        asm.Mov(R(EAX), R(EBX));                 // abs path
        asm.Call("fs_unlink");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_mkdirpath");
        asm.Mov(R(EAX), R(EBX));                 // abs path
        asm.Call("fs_mkdir_path");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("fo_result");
        asm.Label("fo_readdir");
        asm.Call("fs_readdir");                  // EBX=dir block, ECX=index, EDX=abs out buf
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
        // Pin the superblock and bitmap in the cache: nearly every FS op reads them, so they
        // must never be chosen as eviction victims. Also exercises the cache pin path in
        // production (not just tests). They are resident here (just written above).
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_pin");
        asm.MovImm(R(EAX), FsLayout.BitmapBlock);
        asm.Call("cache_pin");
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
        // maintain the superblock free count (one block just left the free pool). EDX (the
        // block) survives cache_get/cache_dirty, which never touch EDX/EDI.
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(EAX), FsLayout.SuperFreeCountOffset);
        asm.Add(R(R8), R(EAX));                   // R8 = &FreeCount
        asm.Load(R(R9), R(R8));
        asm.Dec(R(R9));
        asm.Store(R(R8), R(R9));
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
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
        // maintain the superblock free count (one block just returned to the free pool).
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_get");
        asm.Mov(R(R8), R(EAX));
        asm.MovImm(R(EAX), FsLayout.SuperFreeCountOffset);
        asm.Add(R(R8), R(EAX));
        asm.Load(R(R9), R(R8));
        asm.Inc(R(R9));
        asm.Store(R(R8), R(R9));
        asm.MovImm(R(EAX), FsLayout.SuperBlock);
        asm.Call("cache_dirty");
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

    // ===== EmitFsPathSubroutines =============================================
    // Nested directories (Increment 4b): extract one path component, resolve a whole
    // "/a/b/c" path by descending through DirTypeDir entries, and mkdir a subdirectory.
    // fs_path_resolve keeps all loop state in OsLayout.FsPath* memory because fs_dir_lookup
    // clobbers the registers between components.
    private static void EmitFsPathSubroutines(Assembler asm)
    {
        // ---- fs_extract_component: read the next path component out of FsPathPos into the
        // FsPathComponent buffer (null-padded), advance FsPathPos to the delimiter, set
        // FsPathLast=1 iff nothing but separators/null follows. EAX = component length. Pure
        // memory work (no cache calls), so all registers are free scratch. ----
        asm.Label("fs_extract_component");
        SpillLoad(asm, OsLayout.FsPathPos, R8);          // R8 = read position
        asm.Label("ec_skip");
        asm.Load(R(R9), R(R8));
        asm.MovImm(R(R10), OsLayout.FsPathSeparator);
        asm.Cmp(R(R9), R(R10));
        asm.Jnz("ec_copy_start");
        asm.MovImm(R(R11), 4);
        asm.Add(R(R8), R(R11));
        asm.Jmp("ec_skip");
        asm.Label("ec_copy_start");
        asm.MovImm(R(R12), 0);                           // R12 = component length i
        Imm16(asm, R13, OsLayout.FsPathComponentBase);   // R13 = component buffer base
        asm.Label("ec_copy");
        asm.Load(R(R9), R(R8));                          // char
        asm.MovImm(R(R10), 0);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("ec_pad");                                // null terminator
        asm.MovImm(R(R10), OsLayout.FsPathSeparator);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("ec_pad");                                // component separator
        asm.MovImm(R(R10), FsLayout.NameMaxChars);
        asm.Cmp(R(R12), R(R10));
        asm.Jns("ec_advance");                           // name full: truncate extra chars
        asm.Mov(R(R11), R(R12));
        asm.MovImm(R(R10), 4);
        asm.Mul(R(R11), R(R10));
        asm.Add(R(R11), R(R13));
        asm.Store(R(R11), R(R9));
        asm.Label("ec_advance");
        asm.Inc(R(R12));
        asm.MovImm(R(R10), 4);
        asm.Add(R(R8), R(R10));
        asm.Jmp("ec_copy");
        asm.Label("ec_pad");
        asm.Mov(R(R14), R(R12));                         // pad from j = i to NameMaxChars
        asm.Label("ec_pad_loop");
        asm.MovImm(R(R10), FsLayout.NameMaxChars);
        asm.Cmp(R(R14), R(R10));
        asm.Jns("ec_pad_done");
        asm.Mov(R(R11), R(R14));
        asm.MovImm(R(R10), 4);
        asm.Mul(R(R11), R(R10));
        asm.Add(R(R11), R(R13));
        asm.MovImm(R(R10), 0);
        asm.Store(R(R11), R(R10));
        asm.Inc(R(R14));
        asm.Jmp("ec_pad_loop");
        asm.Label("ec_pad_done");
        SpillStore(asm, OsLayout.FsPathPos, R8);         // leave the cursor at the delimiter
        // last component? skip trailing separators; if the path then ends, this was the last.
        asm.Mov(R(R15), R(R8));
        asm.Label("ec_last_skip");
        asm.Load(R(R9), R(R15));
        asm.MovImm(R(R10), OsLayout.FsPathSeparator);
        asm.Cmp(R(R9), R(R10));
        asm.Jnz("ec_last_check");
        asm.MovImm(R(R10), 4);
        asm.Add(R(R15), R(R10));
        asm.Jmp("ec_last_skip");
        asm.Label("ec_last_check");
        asm.Load(R(R9), R(R15));
        asm.MovImm(R(R10), 0);
        asm.Cmp(R(R9), R(R10));
        asm.Jz("ec_is_last");
        asm.MovImm(R(R11), 0);
        SpillStore(asm, OsLayout.FsPathLast, R11);
        asm.Jmp("ec_ret");
        asm.Label("ec_is_last");
        asm.MovImm(R(R11), 1);
        SpillStore(asm, OsLayout.FsPathLast, R11);
        asm.Label("ec_ret");
        asm.Mov(R(EAX), R(R12));                         // return component length
        asm.Ret();

        // ---- fs_path_resolve: EAX = path addr → EAX = final entry addr, or -1 ----
        asm.Label("fs_path_resolve");
        SpillStore(asm, OsLayout.FsPathPos, EAX);        // cursor = path
        asm.Call("fs_root_dir");
        SpillStore(asm, OsLayout.FsPathDir, EAX);        // current dir = root
        asm.Label("pr_loop");
        asm.Call("fs_extract_component");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jz("pr_fail");                               // empty (e.g. "/" or trailing) → no entry
        SpillLoad(asm, OsLayout.FsPathDir, EAX);         // dir
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);   // name = the extracted component
        asm.Call("fs_dir_lookup");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("pr_fail");                               // component not found
        SpillLoad(asm, OsLayout.FsPathLast, EBX);
        asm.MovImm(R(ECX), 1);
        asm.Cmp(R(EBX), R(ECX));
        asm.Jz("pr_found");                              // last component → this is the answer
        // not last: the component must be a directory to descend into
        asm.Mov(R(R8), R(EAX));                          // entry addr
        asm.Load(R(R9), R(R8));                          // type
        asm.MovImm(R(EBX), FsLayout.DirTypeDir);
        asm.Cmp(R(R9), R(EBX));
        asm.Jnz("pr_fail");                              // not a directory
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(R9), R(R9));                          // firstBlock
        SpillStore(asm, OsLayout.FsPathDir, R9);         // descend
        asm.Jmp("pr_loop");
        asm.Label("pr_found");
        asm.Ret();                                       // EAX = entry addr
        asm.Label("pr_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_mkdir: EBX = parent dir, ECX = name → EAX = new dir block, or -1 ----
        asm.Label("fs_mkdir");
        SpillStore(asm, OsLayout.FsScratchArgA, ECX);    // name (survives everything below)
        asm.Mov(R(EDI), R(EBX));                         // parent (survives fs_alloc_block)
        asm.Call("fs_alloc_block");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("mk_fail");                               // disk full
        SpillStore(asm, OsLayout.FsScratchArgB, EAX);    // new dir block
        asm.Mov(R(ESI), R(EAX));                         // first = new block
        asm.Mov(R(EBX), R(EDI));                         // parent
        SpillLoad(asm, OsLayout.FsScratchArgA, ECX);     // name
        asm.MovImm(R(EDX), FsLayout.DirTypeDir);
        asm.Call("fs_dir_insert");                       // add the directory entry
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("mk_insert_fail");                        // duplicate name or parent full
        SpillLoad(asm, OsLayout.FsScratchArgB, EAX);     // return the new dir block
        asm.Ret();
        asm.Label("mk_insert_fail");
        SpillLoad(asm, OsLayout.FsScratchArgB, EAX);
        asm.Call("fs_free_block");                       // undo the allocation
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
        asm.Label("mk_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }

    // ===== EmitFsSyscall =====================================================
    // IvtFsSyscall: the FSYS dispatcher. Entered atomically with the trapped user registers
    // still live (EAX=syscall#, EBX/ECX/EDX=args). Runs the op via the fs_*_core routines,
    // then resumes the caller with the result in EAX — the SAVEREGS/entry-EAX/LOADREGS/OSRET
    // idiom (as EmitWait's reap path), which persists the CAPTURED trap frame (clean user
    // regs) to the entry, overrides EAX with the result, and returns to the same process.
    private static void EmitFsSyscall(Assembler asm)
    {
        asm.Label("fs_syscall");
        SetupPrivilegedStack(asm);
        asm.MovImm(R(R8), Hardware.FsysOpen);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_open");
        asm.MovImm(R(R8), Hardware.FsysClose);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_close");
        asm.MovImm(R(R8), Hardware.FsysRead);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_read");
        asm.MovImm(R(R8), Hardware.FsysWrite);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_write");
        asm.MovImm(R(R8), Hardware.FsysExec);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_exec");
        asm.MovImm(R(R8), Hardware.FsysUnlink);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_unlink");
        asm.MovImm(R(R8), Hardware.FsysMkdir);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_mkdir");
        asm.MovImm(R(R8), Hardware.FsysReaddir);
        asm.Cmp(R(EAX), R(R8));
        asm.Jz("fsy_readdir");
        asm.MovImm(R(R15), 0);                   // unknown syscall → -1
        asm.Dec(R(R15));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_open");
        // absolute path = current process ProgramAddress + user path ptr (EBX); flags in ECX.
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(EDX), R(EBP));                // EDX = current process index (OPEN ignores user EDX)
        EntryAddress(asm, EDX, R9);              // R9 = current entry
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(EBX), R(R10));                 // EBX = absolute path addr
        asm.Call("fs_open_core");                // (EBX=abs path, ECX=flags, EDX=proc)
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_close");
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EBP));                // ECX = current process index
        asm.Call("fs_close_core");               // (EBX=fd, ECX=proc)
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_read");
        // translate user buffer ptr (ECX) to absolute; fd in EBX, count in EDX, proc in ESI.
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));                // ESI = proc index
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(ECX), R(R10));                 // ECX = absolute buffer addr
        asm.Call("fs_read_core");                // (EBX=fd, ECX=abs buf, EDX=count, ESI=proc)
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_write");
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(ECX), R(R10));
        asm.Call("fs_write_core");
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_exec");
        // translate the user path ptr (EBX) to absolute, then hand off to fs_exec_core, which
        // replaces the running image and resumes it (never returns) on success, or returns -1
        // if the path does not resolve to a file — in which case we deliver -1 to the caller.
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(EBX), R(R10));                 // EBX = absolute path addr
        asm.Call("fs_exec_core");                // resumes the process on success; returns -1 on failure
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_unlink");
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(EBX), R(R10));                 // EBX = absolute path addr
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_unlink");
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_mkdir");
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(EBX), R(R10));                 // EBX = absolute path addr
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_mkdir_path");               // EAX = new dir block, or -1
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");

        asm.Label("fsy_readdir");
        // translate the dir path ptr (EBX) and out ptr (EDX); index in ECX. Resolving the path
        // clobbers registers, so stash the index + translated out ptr in FsScratchArg* (which
        // fs_path_resolve/fs_readdir do not touch) and reload them after resolving the dir.
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ESI), R(EBP));
        EntryAddress(asm, ESI, R9);
        LoadField(asm, R9, Hardware.ProcessEntryProgramAddress, R10);
        asm.Add(R(EBX), R(R10));                 // abs dir path
        asm.Add(R(EDX), R(R10));                 // abs out buffer
        SpillStore(asm, OsLayout.FsScratchArgA, ECX);   // index
        SpillStore(asm, OsLayout.FsScratchArgB, EDX);   // out buffer
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_resolve_dir");              // EAX = dir block, or -1
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("fsy_readdir_fail");
        asm.Mov(R(EBX), R(EAX));                 // dir block
        SpillLoad(asm, OsLayout.FsScratchArgA, ECX);
        SpillLoad(asm, OsLayout.FsScratchArgB, EDX);
        asm.Call("fs_readdir");                  // EBX=dir, ECX=index, EDX=out → type or -1
        asm.Mov(R(R15), R(EAX));
        asm.Jmp("fsy_deliver");
        asm.Label("fsy_readdir_fail");
        asm.MovImm(R(R15), 0);
        asm.Dec(R(R15));

        asm.Label("fsy_deliver");
        Imm16(asm, EBP, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EBP));
        EntryAddress(asm, ECX, EBX);             // EBX = current entry
        asm.SaveRegs(R(EBX));                    // persist the captured user regs
        StoreFieldReg(asm, EBX, EaxSlot, R15);   // deliver the result in EAX
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));
    }

    // ===== EmitFsFileSubroutines =============================================
    // File-syscall cores (Increment 5): open/create + close over the open-file table, plus
    // the parent-path resolver and OFT slot allocator they need. Cores take an ABSOLUTE path
    // and an explicit process index (so they're callable straight from IvtFsOp in tests); the
    // FSYS wrapper translates the user pointer and passes the current process index.
    private static void EmitFsFileSubroutines(Assembler asm)
    {
        // ---- oft_alloc: → EAX = free open-file-table index, or -1 (pure memory) ----
        asm.Label("oft_alloc");
        asm.MovImm(R(R8), 0);
        asm.Label("oa_loop");
        asm.MovImm(R(R9), OsLayout.MaxOpenFiles);
        asm.Cmp(R(R8), R(R9));
        asm.Jns("oa_full");
        asm.Mov(R(R10), R(R8));
        asm.MovImm(R(R9), OsLayout.OftEntryBytes);
        asm.Mul(R(R10), R(R9));
        Imm16(asm, R9, OsLayout.OftBase);
        asm.Add(R(R10), R(R9));                  // R10 = OFT entry addr
        asm.Load(R(R11), R(R10));                // inUse
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(R11), R(R9));
        asm.Jz("oa_found");
        asm.Inc(R(R8));
        asm.Jmp("oa_loop");
        asm.Label("oa_found");
        asm.Mov(R(EAX), R(R8));
        asm.Ret();
        asm.Label("oa_full");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_resolve_parent: EAX=path → EAX=parent dir block or -1; leaves the last
        // component in FsPathComponentBase (like fs_path_resolve but stops before the tail) ----
        asm.Label("fs_resolve_parent");
        SpillStore(asm, OsLayout.FsPathPos, EAX);
        asm.Call("fs_root_dir");
        SpillStore(asm, OsLayout.FsPathDir, EAX);
        asm.Label("rp_loop");
        asm.Call("fs_extract_component");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jz("rp_fail");                       // empty path
        SpillLoad(asm, OsLayout.FsPathLast, EBX);
        asm.MovImm(R(ECX), 1);
        asm.Cmp(R(EBX), R(ECX));
        asm.Jz("rp_done");                       // last component → current dir is the parent
        SpillLoad(asm, OsLayout.FsPathDir, EAX);
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);
        asm.Call("fs_dir_lookup");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("rp_fail");
        asm.Mov(R(R8), R(EAX));
        asm.Load(R(R9), R(R8));                  // type
        asm.MovImm(R(EBX), FsLayout.DirTypeDir);
        asm.Cmp(R(R9), R(EBX));
        asm.Jnz("rp_fail");                      // intermediate is not a directory
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(R9), R(R9));
        SpillStore(asm, OsLayout.FsPathDir, R9);
        asm.Jmp("rp_loop");
        asm.Label("rp_done");
        SpillLoad(asm, OsLayout.FsPathDir, EAX);
        asm.Ret();
        asm.Label("rp_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_create_file: path in FsOpenAbsPath → EAX = new file entry addr or -1;
        // fs_dir_insert leaves the entry's block in FsScratchEntryBlock ----
        asm.Label("fs_create_file");
        SpillLoad(asm, OsLayout.FsOpenAbsPath, EAX);
        asm.Call("fs_resolve_parent");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("cf_fail");
        SpillStore(asm, OsLayout.FsOpenDirBlock, EAX);   // parent (temp)
        asm.Call("fs_alloc_block");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("cf_fail");
        SpillStore(asm, OsLayout.FsOpenFirst, EAX);      // file's first block (temp)
        SpillLoad(asm, OsLayout.FsOpenDirBlock, EBX);    // parent
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);   // name = last component
        asm.MovImm(R(EDX), FsLayout.DirTypeFile);
        SpillLoad(asm, OsLayout.FsOpenFirst, ESI);       // first block
        asm.Call("fs_dir_insert");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("cf_insert_fail");
        asm.Ret();                                       // EAX = new entry addr
        asm.Label("cf_insert_fail");
        SpillLoad(asm, OsLayout.FsOpenFirst, EAX);
        asm.Call("fs_free_block");                       // undo the block alloc
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
        asm.Label("cf_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_open_core: EBX=abs path, ECX=flags, EDX=proc → EAX = fd or -1 ----
        asm.Label("fs_open_core");
        SpillStore(asm, OsLayout.FsOpenAbsPath, EBX);
        SpillStore(asm, OsLayout.FsOpenFlags, ECX);
        SpillStore(asm, OsLayout.FsOpenProc, EDX);
        SpillLoad(asm, OsLayout.FsOpenAbsPath, EAX);
        asm.Call("fs_path_resolve");             // EAX = entry addr or -1
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jns("open_have_entry");              // found
        SpillLoad(asm, OsLayout.FsOpenFlags, EBX);
        asm.MovImm(R(ECX), Hardware.FsysCreateFlag);
        asm.And(R(EBX), R(ECX));
        asm.MovImm(R(ECX), 0);
        asm.Cmp(R(EBX), R(ECX));
        asm.Jz("open_fail");                     // not found and no create flag
        asm.Call("fs_create_file");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("open_fail");
        asm.Label("open_have_entry");
        asm.Mov(R(R8), R(EAX));                  // entry addr
        asm.Load(R(R9), R(R8));                  // type
        asm.MovImm(R(EBX), FsLayout.DirTypeDir);
        asm.Cmp(R(R9), R(EBX));
        asm.Jz("open_fail");                     // cannot open a directory as a file
        SpillStore(asm, OsLayout.FsOpenEntryAddr, R8);
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(R9), R(R9));
        SpillStore(asm, OsLayout.FsOpenFirst, R9);
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntrySizeField);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(R9), R(R9));
        SpillStore(asm, OsLayout.FsOpenSize, R9);
        SpillLoad(asm, OsLayout.FsScratchEntryBlock, R9);   // dir block holding the entry
        SpillStore(asm, OsLayout.FsOpenDirBlock, R9);
        // entry byte offset within its block = entryAddr - cache_get(dirBlock).dataAddr
        asm.Mov(R(EAX), R(R9));
        asm.Call("cache_get");                   // hit: same slot the entry lives in
        SpillLoad(asm, OsLayout.FsOpenEntryAddr, EBX);
        asm.Sub(R(EBX), R(EAX));
        SpillStore(asm, OsLayout.FsOpenEntryOffset, EBX);
        // Reject a second open of the same file (single-open policy): two OFT handles would
        // each cache their own size/offset, so a write through one desyncs the other. Scan the
        // open-file table for this file's first block.
        SpillLoad(asm, OsLayout.FsOpenFirst, EAX);
        asm.Call("oft_find_first");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jnz("open_fail");                    // already open
        // allocate an OFT slot and fill it
        asm.Call("oft_alloc");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("open_fail");
        asm.Mov(R(R8), R(EAX));                  // OFT index
        asm.Mov(R(R10), R(R8));
        asm.MovImm(R(EBX), OsLayout.OftEntryBytes);
        asm.Mul(R(R10), R(EBX));
        Imm16(asm, EBX, OsLayout.OftBase);
        asm.Add(R(R10), R(EBX));                 // R10 = OFT entry addr
        StoreFieldImm(asm, R10, OsLayout.OftInUse, 1);
        SpillLoad(asm, OsLayout.FsOpenFirst, EBX);
        StoreFieldReg(asm, R10, OsLayout.OftFirstBlock, EBX);
        StoreFieldImm(asm, R10, OsLayout.OftOffset, 0);
        SpillLoad(asm, OsLayout.FsOpenSize, EBX);
        StoreFieldReg(asm, R10, OsLayout.OftSize, EBX);
        SpillLoad(asm, OsLayout.FsOpenDirBlock, EBX);
        StoreFieldReg(asm, R10, OsLayout.OftDirBlock, EBX);
        SpillLoad(asm, OsLayout.FsOpenEntryOffset, EBX);
        StoreFieldReg(asm, R10, OsLayout.OftEntryOffset, EBX);
        // allocate an fd (2..FdCount-1) in the owning process; store OFT index + 1
        asm.Mov(R(R11), R(R8));
        asm.Inc(R(R11));                         // fd value = OFT index + 1
        SpillLoad(asm, OsLayout.FsOpenProc, ECX);
        EntryAddress(asm, ECX, R12);             // R12 = process entry
        asm.MovImm(R(R13), 2);                   // fd index k (skip stdin/stdout)
        asm.Label("open_fd_loop");
        asm.MovImm(R(EBX), Hardware.FdCount);
        asm.Cmp(R(R13), R(EBX));
        asm.Jns("open_fd_full");
        asm.Mov(R(R14), R(R13));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(R14), R(EBX));
        asm.MovImm(R(EBX), Hardware.ProcessEntryFdTable);
        asm.Add(R(R14), R(EBX));
        asm.Add(R(R14), R(R12));                 // R14 = fd slot addr
        asm.Load(R(R15), R(R14));
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R15), R(EBX));
        asm.Jz("open_fd_found");
        asm.Inc(R(R13));
        asm.Jmp("open_fd_loop");
        asm.Label("open_fd_found");
        asm.Store(R(R14), R(R11));               // fd[k] = OFT index + 1
        asm.Mov(R(EAX), R(R13));                 // return the fd number
        asm.Ret();
        asm.Label("open_fd_full");
        StoreFieldImm(asm, R10, OsLayout.OftInUse, 0);   // release the OFT slot
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
        asm.Label("open_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_close_core: EBX=fd, ECX=proc → EAX = 0, or -1 (pure memory) ----
        asm.Label("fs_close_core");
        asm.MovImm(R(R8), 2);
        asm.Cmp(R(EBX), R(R8));
        asm.Js("close_fail");                    // fd < 2
        asm.MovImm(R(R8), Hardware.FdCount);
        asm.Cmp(R(EBX), R(R8));
        asm.Jns("close_fail");                   // fd >= FdCount
        EntryAddress(asm, ECX, R9);              // R9 = process entry
        asm.Mov(R(R10), R(EBX));
        asm.MovImm(R(R8), 4);
        asm.Mul(R(R10), R(R8));
        asm.MovImm(R(R8), Hardware.ProcessEntryFdTable);
        asm.Add(R(R10), R(R8));
        asm.Add(R(R10), R(R9));                  // R10 = fd slot addr
        asm.Load(R(R11), R(R10));
        asm.MovImm(R(R8), 0);
        asm.Cmp(R(R11), R(R8));
        asm.Jz("close_fail");                    // fd not open
        asm.Dec(R(R11));                         // OFT index
        asm.Mov(R(R12), R(R11));
        asm.MovImm(R(R8), OsLayout.OftEntryBytes);
        asm.Mul(R(R12), R(R8));
        Imm16(asm, R8, OsLayout.OftBase);
        asm.Add(R(R12), R(R8));                  // R12 = OFT entry addr
        StoreFieldImm(asm, R12, OsLayout.OftInUse, 0);
        asm.MovImm(R(R8), 0);
        asm.Store(R(R10), R(R8));                // clear the fd slot
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("close_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }

    // ===== EmitFsRwSubroutines ===============================================
    // Byte-level read/write across a file's block chain (Increment 5b). File content is stored
    // word-per-char (CharsPerBlock per block), so copies are word LOAD/STORE loops. Every loop
    // variable lives in OsLayout.FsRw* memory because the cache/chain calls clobber registers.
    private static void EmitFsRwSubroutines(Assembler asm)
    {
        // ---- oft_from_fd: EBX=fd, ECX=proc → EAX = OFT entry addr, or -1 (pure memory) ----
        asm.Label("oft_from_fd");
        asm.MovImm(R(R8), 2);
        asm.Cmp(R(EBX), R(R8));
        asm.Js("off_fail");
        asm.MovImm(R(R8), Hardware.FdCount);
        asm.Cmp(R(EBX), R(R8));
        asm.Jns("off_fail");
        EntryAddress(asm, ECX, R9);              // R9 = process entry
        asm.Mov(R(R10), R(EBX));
        asm.MovImm(R(R8), 4);
        asm.Mul(R(R10), R(R8));
        asm.MovImm(R(R8), Hardware.ProcessEntryFdTable);
        asm.Add(R(R10), R(R8));
        asm.Add(R(R10), R(R9));                  // R10 = fd slot addr
        asm.Load(R(R11), R(R10));
        asm.MovImm(R(R8), 0);
        asm.Cmp(R(R11), R(R8));
        asm.Jz("off_fail");                      // fd not open
        asm.Dec(R(R11));                         // OFT index
        asm.Mov(R(R12), R(R11));
        asm.MovImm(R(R8), OsLayout.OftEntryBytes);
        asm.Mul(R(R12), R(R8));
        Imm16(asm, R8, OsLayout.OftBase);
        asm.Add(R(R12), R(R8));
        asm.Mov(R(EAX), R(R12));
        asm.Ret();
        asm.Label("off_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_grow_chain: EBX=firstBlock, ECX=neededBlocks → EAX = 0, or -1 (disk full).
        // Walks/extends the chain until it has at least neededBlocks blocks. ----
        asm.Label("fs_grow_chain");
        SpillStore(asm, OsLayout.FsRwCurBlock, EBX);
        SpillStore(asm, OsLayout.FsRwCounter, ECX);      // needed
        asm.MovImm(R(EBX), 1);
        SpillStore(asm, OsLayout.FsRwRemaining, EBX);    // count = 1 (blocks so far)
        asm.Label("gc_loop");
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        SpillLoad(asm, OsLayout.FsRwCounter, R9);
        asm.Cmp(R(R8), R(R9));
        asm.Jns("gc_done");                              // count >= needed
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");                       // EAX = next
        asm.MovImm(R(EBX), 0);
        asm.Dec(R(EBX));
        asm.Cmp(R(EAX), R(EBX));
        asm.Jz("gc_extend");                             // next == -1 → allocate
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);     // advance to existing next
        asm.Jmp("gc_next");
        asm.Label("gc_extend");
        asm.Call("fs_alloc_block");                      // EAX = new block or -1
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("gc_fail");
        SpillStore(asm, OsLayout.FsRwBufPtr, EAX);       // temp: new block
        asm.Mov(R(ECX), R(EAX));                         // next = new block
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);      // block = cur
        asm.Call("fs_chain_set_next");
        SpillLoad(asm, OsLayout.FsRwBufPtr, EAX);
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);     // advance to the new block
        asm.Label("gc_next");
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Inc(R(R8));
        SpillStore(asm, OsLayout.FsRwRemaining, R8);
        asm.Jmp("gc_loop");
        asm.Label("gc_done");
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("gc_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_read_core: EBX=fd, ECX=abs buf, EDX=count, ESI=proc → EAX = chars read or -1 ----
        asm.Label("fs_read_core");
        SpillStore(asm, OsLayout.FsRwFd, EBX);
        SpillStore(asm, OsLayout.FsRwBuf, ECX);
        SpillStore(asm, OsLayout.FsRwCount, EDX);
        SpillStore(asm, OsLayout.FsRwProc, ESI);
        SpillLoad(asm, OsLayout.FsRwFd, EBX);
        SpillLoad(asm, OsLayout.FsRwProc, ECX);
        asm.Call("oft_from_fd");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("read_fail");
        SpillStore(asm, OsLayout.FsRwOft, EAX);
        asm.Mov(R(R8), R(EAX));
        LoadField(asm, R8, OsLayout.OftSize, R9);        // size
        LoadField(asm, R8, OsLayout.OftOffset, R10);     // offset
        asm.Mov(R(R11), R(R9));
        asm.Sub(R(R11), R(R10));                         // avail = size - offset
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R11), R(EBX));
        asm.Jns("read_avail_ok");
        asm.MovImm(R(R11), 0);
        asm.Label("read_avail_ok");
        SpillLoad(asm, OsLayout.FsRwCount, R12);
        asm.Cmp(R(R12), R(R11));
        asm.Js("read_count_ok");
        asm.Mov(R(R12), R(R11));                         // count = min(count, avail)
        asm.Label("read_count_ok");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R12), R(EBX));
        asm.Jns("read_count_pos");
        asm.MovImm(R(R12), 0);
        asm.Label("read_count_pos");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R12), R(EBX));
        asm.Jz("read_zero");
        SpillStore(asm, OsLayout.FsRwCopied, R12);
        SpillStore(asm, OsLayout.FsRwRemaining, R12);
        LoadField(asm, R8, OsLayout.OftFirstBlock, R13);
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Mov(R(R14), R(R10));
        asm.Div(R(R14), R(EBX));                         // skip = offset / CharsPerBlock
        asm.Mov(R(R15), R(R14));
        asm.MovImm(R(EAX), FsLayout.CharsPerBlock);
        asm.Mul(R(R15), R(EAX));
        asm.Mov(R(EBX), R(R10));
        asm.Sub(R(EBX), R(R15));                         // charInBlock = offset - skip*CPB
        SpillStore(asm, OsLayout.FsRwCharInBlock, EBX);
        SpillStore(asm, OsLayout.FsRwCurBlock, R13);
        SpillLoad(asm, OsLayout.FsRwBuf, EBX);
        SpillStore(asm, OsLayout.FsRwBufPtr, EBX);
        SpillStore(asm, OsLayout.FsRwCounter, R14);      // skip count
        asm.Label("read_skip");
        SpillLoad(asm, OsLayout.FsRwCounter, R8);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("read_copy_loop");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        SpillLoad(asm, OsLayout.FsRwCounter, R8);
        asm.Dec(R(R8));
        SpillStore(asm, OsLayout.FsRwCounter, R8);
        asm.Jmp("read_skip");
        asm.Label("read_copy_loop");
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("read_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("cache_get");
        asm.Mov(R(R9), R(EAX));                          // block data addr
        SpillLoad(asm, OsLayout.FsRwCharInBlock, R10);
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Sub(R(EBX), R(R10));
        asm.Mov(R(R11), R(EBX));                         // availInBlock
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Mov(R(R12), R(R8));
        asm.Cmp(R(R12), R(R11));
        asm.Js("read_n_ok");
        asm.Mov(R(R12), R(R11));                         // n = min(remaining, availInBlock)
        asm.Label("read_n_ok");
        asm.Mov(R(R13), R(R10));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(R13), R(EBX));
        asm.Add(R(R13), R(R9));                          // src = block + charInBlock*4
        SpillLoad(asm, OsLayout.FsRwBufPtr, R14);        // dst
        asm.MovImm(R(R15), 0);
        asm.Label("read_word");
        asm.Cmp(R(R15), R(R12));
        asm.Jns("read_word_done");
        asm.Mov(R(EBX), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        asm.Mov(R(ECX), R(R13));
        asm.Add(R(ECX), R(EBX));
        asm.Load(R(ECX), R(ECX));                        // char from file
        asm.Mov(R(EBP), R(R14));
        asm.Add(R(EBP), R(EBX));
        asm.Store(R(EBP), R(ECX));                       // → user buffer
        asm.Inc(R(R15));
        asm.Jmp("read_word");
        asm.Label("read_word_done");
        asm.Mov(R(EBX), R(R12));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        asm.Add(R(R14), R(EBX));
        SpillStore(asm, OsLayout.FsRwBufPtr, R14);       // bufPtr += n*4
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Sub(R(R8), R(R12));
        SpillStore(asm, OsLayout.FsRwRemaining, R8);     // remaining -= n
        asm.MovImm(R(EBX), 0);
        SpillStore(asm, OsLayout.FsRwCharInBlock, EBX);  // charInBlock = 0 for the next block
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("read_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Jmp("read_copy_loop");
        asm.Label("read_done");
        SpillLoad(asm, OsLayout.FsRwOft, R8);
        LoadField(asm, R8, OsLayout.OftOffset, R9);
        SpillLoad(asm, OsLayout.FsRwCopied, R10);
        asm.Add(R(R9), R(R10));
        StoreFieldReg(asm, R8, OsLayout.OftOffset, R9);  // offset += copied
        asm.Mov(R(EAX), R(R10));
        asm.Ret();
        asm.Label("read_zero");
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("read_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_write_core: EBX=fd, ECX=abs buf, EDX=count, ESI=proc → EAX = chars written or -1 ----
        asm.Label("fs_write_core");
        SpillStore(asm, OsLayout.FsRwFd, EBX);
        SpillStore(asm, OsLayout.FsRwBuf, ECX);
        SpillStore(asm, OsLayout.FsRwCount, EDX);
        SpillStore(asm, OsLayout.FsRwProc, ESI);
        SpillLoad(asm, OsLayout.FsRwFd, EBX);
        SpillLoad(asm, OsLayout.FsRwProc, ECX);
        asm.Call("oft_from_fd");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("write_fail");
        SpillStore(asm, OsLayout.FsRwOft, EAX);
        asm.Mov(R(R8), R(EAX));
        LoadField(asm, R8, OsLayout.OftOffset, R10);     // offset
        SpillLoad(asm, OsLayout.FsRwCount, R12);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R12), R(EBX));
        asm.Jns("write_count_ok");
        asm.MovImm(R(R12), 0);
        asm.Label("write_count_ok");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R12), R(EBX));
        asm.Jz("write_zero");
        SpillStore(asm, OsLayout.FsRwCopied, R12);
        SpillStore(asm, OsLayout.FsRwRemaining, R12);
        // grow the chain to hold offset + count chars
        asm.Mov(R(R13), R(R10));
        asm.Add(R(R13), R(R12));                         // end = offset + count
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Dec(R(EBX));
        asm.Add(R(R13), R(EBX));                         // end + CPB - 1
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Div(R(R13), R(EBX));                         // needed = ceil(end / CPB)
        LoadField(asm, R8, OsLayout.OftFirstBlock, EBX);
        asm.Mov(R(ECX), R(R13));
        asm.Call("fs_grow_chain");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("write_fail");                            // disk full
        // re-derive walk state (grow clobbered the FsRw scratch, including FsRwRemaining
        // which grow reuses as its own block counter — restore it from FsRwCopied=count).
        SpillLoad(asm, OsLayout.FsRwCopied, R8);
        SpillStore(asm, OsLayout.FsRwRemaining, R8);
        SpillLoad(asm, OsLayout.FsRwOft, R8);
        LoadField(asm, R8, OsLayout.OftOffset, R10);
        LoadField(asm, R8, OsLayout.OftFirstBlock, R13);
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Mov(R(R14), R(R10));
        asm.Div(R(R14), R(EBX));                         // skip
        asm.Mov(R(R15), R(R14));
        asm.MovImm(R(EAX), FsLayout.CharsPerBlock);
        asm.Mul(R(R15), R(EAX));
        asm.Mov(R(EBX), R(R10));
        asm.Sub(R(EBX), R(R15));
        SpillStore(asm, OsLayout.FsRwCharInBlock, EBX);
        SpillStore(asm, OsLayout.FsRwCurBlock, R13);
        SpillLoad(asm, OsLayout.FsRwBuf, EBX);
        SpillStore(asm, OsLayout.FsRwBufPtr, EBX);
        SpillStore(asm, OsLayout.FsRwCounter, R14);      // skip
        asm.Label("write_skip");
        SpillLoad(asm, OsLayout.FsRwCounter, R8);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("write_copy_loop");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        SpillLoad(asm, OsLayout.FsRwCounter, R8);
        asm.Dec(R(R8));
        SpillStore(asm, OsLayout.FsRwCounter, R8);
        asm.Jmp("write_skip");
        asm.Label("write_copy_loop");
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("write_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("cache_get");
        asm.Mov(R(R9), R(EAX));
        SpillLoad(asm, OsLayout.FsRwCharInBlock, R10);
        asm.MovImm(R(EBX), FsLayout.CharsPerBlock);
        asm.Sub(R(EBX), R(R10));
        asm.Mov(R(R11), R(EBX));
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Mov(R(R12), R(R8));
        asm.Cmp(R(R12), R(R11));
        asm.Js("write_n_ok");
        asm.Mov(R(R12), R(R11));
        asm.Label("write_n_ok");
        asm.Mov(R(R13), R(R10));
        asm.MovImm(R(EBX), 4);
        asm.Mul(R(R13), R(EBX));
        asm.Add(R(R13), R(R9));                          // dst = block + charInBlock*4
        SpillLoad(asm, OsLayout.FsRwBufPtr, R14);        // src
        asm.MovImm(R(R15), 0);
        asm.Label("write_word");
        asm.Cmp(R(R15), R(R12));
        asm.Jns("write_word_done");
        asm.Mov(R(EBX), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        asm.Mov(R(ECX), R(R14));
        asm.Add(R(ECX), R(EBX));
        asm.Load(R(ECX), R(ECX));                        // char from user buffer
        asm.Mov(R(EBP), R(R13));
        asm.Add(R(EBP), R(EBX));
        asm.Store(R(EBP), R(ECX));                       // → file block
        asm.Inc(R(R15));
        asm.Jmp("write_word");
        asm.Label("write_word_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("cache_dirty");                         // the block was modified
        asm.Mov(R(EBX), R(R12));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        SpillLoad(asm, OsLayout.FsRwBufPtr, R14);
        asm.Add(R(R14), R(EBX));
        SpillStore(asm, OsLayout.FsRwBufPtr, R14);
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Sub(R(R8), R(R12));
        SpillStore(asm, OsLayout.FsRwRemaining, R8);
        asm.MovImm(R(EBX), 0);
        SpillStore(asm, OsLayout.FsRwCharInBlock, EBX);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("write_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Jmp("write_copy_loop");
        asm.Label("write_done");
        SpillLoad(asm, OsLayout.FsRwOft, R8);
        LoadField(asm, R8, OsLayout.OftOffset, R9);
        SpillLoad(asm, OsLayout.FsRwCopied, R10);
        asm.Add(R(R9), R(R10));                          // new offset
        StoreFieldReg(asm, R8, OsLayout.OftOffset, R9);
        LoadField(asm, R8, OsLayout.OftSize, R11);
        asm.Cmp(R(R11), R(R9));
        asm.Jns("write_ret");                            // old size already covers it
        StoreFieldReg(asm, R8, OsLayout.OftSize, R9);    // grow the size to the new offset
        SpillStore(asm, OsLayout.FsRwCounter, R9);       // stash new size (Counter is free now)
        // write the new size into the on-disk directory entry. Load the entry offset into
        // EDX first — EDX survives cache_get, whereas LoadField would clobber the block
        // address cache_get returns in EAX.
        LoadField(asm, R8, OsLayout.OftEntryOffset, EDX);
        LoadField(asm, R8, OsLayout.OftDirBlock, EAX);
        asm.Call("cache_get");
        asm.Add(R(EAX), R(EDX));                          // entry addr in the cached block
        SpillLoad(asm, OsLayout.FsRwCounter, R9);         // new size
        StoreFieldReg(asm, EAX, FsLayout.DirEntrySizeField, R9);
        SpillLoad(asm, OsLayout.FsRwOft, R8);
        LoadField(asm, R8, OsLayout.OftDirBlock, EAX);
        asm.Call("cache_dirty");
        asm.Label("write_ret");
        SpillLoad(asm, OsLayout.FsRwCopied, EAX);
        asm.Ret();
        asm.Label("write_zero");
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("write_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }

    // ===== EmitFsExecSubroutine ==============================================
    // fs_exec_core (Increment 6): replace the running process's image with a program stored as
    // an FS file. Mirrors EmitExec's teardown/realloc/resume, but sources the new image from a
    // file's block chain instead of a disk image slot. Entered with EBX = ABSOLUTE path address
    // (the FSYS wrapper has already translated the user pointer).
    //
    // The path is resolved to a file entry FIRST — while the old image (holding the path string)
    // is still mapped — and its firstBlock + size are captured into FsRw* scratch before any
    // teardown or cache eviction can invalidate them. Only after a successful resolve does the
    // routine cross the point of no return and rebuild the process. Code pages are RAM-home, so
    // the rebuilt process needs no disk slot; DiskSlot is set to -1 to mark it FS-backed.
    //   Returns EAX = -1 if the path is missing or names a directory (caller delivers it);
    //   on success it OSRETs into the new image and never returns.
    private static void EmitFsExecSubroutine(Assembler asm)
    {
        asm.Label("fs_exec_core");
        asm.Mov(R(EAX), R(EBX));
        asm.Call("fs_path_resolve");                 // EAX = entry addr or -1
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("fxc_fail");                          // path not found
        asm.Mov(R(R12), R(EAX));                     // R12 = entry addr (LoadField clobbers EAX)
        LoadField(asm, R12, FsLayout.DirEntryType, R8);
        asm.MovImm(R(EBX), FsLayout.DirTypeFile);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("fxc_isfile");
        asm.Jmp("fxc_fail");                         // a directory is not executable

        asm.Label("fxc_isfile");
        // Capture firstBlock + size (in words) NOW, before teardown: the entry addr points into
        // a cache slot a later cache_get could evict, and the buddy/paging teardown clobbers the
        // FsRw* scratch — so the durable values live in FsScratch* (which only the dir/path
        // routines touch) and are re-seeded into FsRw* after the realloc.
        LoadField(asm, R12, FsLayout.DirEntryFirstBlock, R8);
        SpillStore(asm, OsLayout.FsScratchArgA, R8);         // chain walk start (survives teardown)
        LoadField(asm, R12, FsLayout.DirEntrySizeField, R8); // size in words
        SpillStore(asm, OsLayout.FsScratchFirst, R8);        // words to load (survives teardown)
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(R9), R(EAX));
        SpillStore(asm, OsLayout.FsScratchArgB, R9);         // newLen in bytes (survives teardown)

        // ---------------- point of no return: tear down the old image ----------------
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        EntryAddress(asm, ECX, EBX);                         // EBX = current entry
        StoreFieldMinusOne(asm, EBX, Hardware.ProcessEntryDiskSlot); // FS-backed: no disk slot

        SetupPrivilegedStack(asm);
        asm.Call("free_sub");
        asm.Call("resolve_cow");
        asm.Call("release_frames");
        asm.Call("zero_swap_slots");
        // resolve_cow clobbered EBX; reload this process's entry.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(EBX), R(EAX));
        EntryAddress(asm, EBX, EBX);

        // Sizing: newTotal = oldTotal - oldProgramSize + newLen.
        SpillLoad(asm, OsLayout.FsScratchArgB, R9);          // newLen bytes (stashed pre-teardown)
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);
        LoadField(asm, EBX, Hardware.ProcessEntryProgramSize, R11);
        asm.Mov(R(R12), R(R10));
        asm.Sub(R(R12), R(R11));
        asm.Add(R(R12), R(R9));
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryProgramSize, R9);
        StoreFieldReg(asm, EBX, Hardware.ProcessEntryTotalSize, R12);

        SetupPrivilegedStack(asm);
        asm.Call("alloc_sub");                               // sets entry.ProgramAddress or -1
        LoadField(asm, EBX, Hardware.ProcessEntryProgramAddress, R9);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R9), R(EAX));
        asm.Jns("fxc_alloc_ok");
        // Out of memory: the old image is already gone, so terminate the process.
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Terminated);
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(ECX), R(EAX));
        asm.Jmp("resume_mlfq");

        asm.Label("fxc_alloc_ok");
        // Copy the file's block chain into ProgramAddress (R9), word by word, through the cache.
        // Re-seed the FsRw* walk state from the FsScratch* values stashed before teardown.
        SpillStore(asm, OsLayout.FsRwBufPtr, R9);           // dest = ProgramAddress
        SpillLoad(asm, OsLayout.FsScratchArgA, R8);
        SpillStore(asm, OsLayout.FsRwCurBlock, R8);         // chain walk start
        SpillLoad(asm, OsLayout.FsScratchFirst, R8);
        SpillStore(asm, OsLayout.FsRwRemaining, R8);        // words left to load
        asm.Label("fxc_load_loop");
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("fxc_load_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("cache_get");                              // EAX = block data addr
        asm.Mov(R(R9), R(EAX));                             // src block
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Mov(R(R12), R(R8));
        asm.MovImm(R(R11), FsLayout.CharsPerBlock);
        asm.Cmp(R(R12), R(R11));
        asm.Js("fxc_n_ok");
        asm.Mov(R(R12), R(R11));                            // n = min(remaining, CharsPerBlock)
        asm.Label("fxc_n_ok");
        SpillLoad(asm, OsLayout.FsRwBufPtr, R14);           // dst
        asm.MovImm(R(R15), 0);
        asm.Label("fxc_word");
        asm.Cmp(R(R15), R(R12));
        asm.Jns("fxc_word_done");
        asm.Mov(R(EBX), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));                            // EBX = R15*4
        asm.Mov(R(ECX), R(R9));
        asm.Add(R(ECX), R(EBX));
        asm.Load(R(ECX), R(ECX));                           // word from file block
        asm.Mov(R(EBP), R(R14));
        asm.Add(R(EBP), R(EBX));
        asm.Store(R(EBP), R(ECX));                          // → RAM image
        asm.Inc(R(R15));
        asm.Jmp("fxc_word");
        asm.Label("fxc_word_done");
        asm.Mov(R(EBX), R(R12));
        asm.MovImm(R(EAX), 4);
        asm.Mul(R(EBX), R(EAX));
        SpillLoad(asm, OsLayout.FsRwBufPtr, R14);
        asm.Add(R(R14), R(EBX));
        SpillStore(asm, OsLayout.FsRwBufPtr, R14);          // dst += n*4
        SpillLoad(asm, OsLayout.FsRwRemaining, R8);
        asm.Sub(R(R8), R(R12));
        SpillStore(asm, OsLayout.FsRwRemaining, R8);        // remaining -= n
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R8), R(EBX));
        asm.Jz("fxc_load_done");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Jmp("fxc_load_loop");
        asm.Label("fxc_load_done");

        // The chain walk clobbered EBX; reload the current entry.
        Imm16(asm, EAX, OsLayout.CurrentIndexOffset);
        asm.Load(R(EBX), R(EAX));
        EntryAddress(asm, EBX, EBX);

        // Reset the register file to zero (24 words) so the new program starts fresh (EIP=0).
        asm.MovImm(R(R13), 0);
        asm.Label("fxc_clear");
        asm.MovImm(R(EAX), 96);
        asm.Cmp(R(R13), R(EAX));
        asm.Jns("fxc_clear_done");
        asm.Mov(R(R14), R(EBX));
        asm.Add(R(R14), R(R13));
        asm.MovImm(R(R15), 0);
        asm.Store(R(R14), R(R15));
        asm.MovImm(R(EAX), 4);
        asm.Add(R(R13), R(EAX));
        asm.Jmp("fxc_clear");
        asm.Label("fxc_clear_done");

        // ESP = top of the user stack = TotalSize - KernelStackSize (EIP stays 0).
        LoadField(asm, EBX, Hardware.ProcessEntryTotalSize, R10);
        asm.MovImm(R(EAX), Hardware.KernelStackSize);
        asm.Sub(R(R10), R(EAX));
        StoreFieldReg(asm, EBX, EspSlot, R10);

        // Scheduling state (keep Pid/ParentPid — exec preserves identity).
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryLevel, User);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryState, Ready);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryWaitReason, WaitNone);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryPriority, 0);
        StoreFieldImm(asm, EBX, Hardware.ProcessEntryTicksUsed, 0);

        // Resume the process running its new image (it is still the current process).
        asm.LoadRegs(R(EBX));
        asm.SetLayout(R(EBX));
        LoadField(asm, EBX, Hardware.ProcessEntryLevel, EAX);
        asm.OsRet(R(EAX));

        asm.Label("fxc_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }

    // ===== EmitFsMaintSubroutines ============================================
    // Directory maintenance (Phase 1 rectification): unlink (delete a file and free its whole
    // block chain — the old fs_dir_remove leaked the chain), mkdir-by-path, and readdir. Plus
    // oft_find_first, the open-file-table scan shared by unlink (refuse to delete an open file)
    // and fs_open_core (reject a second open of the same file). Path cores take EAX = abs path.
    private static void EmitFsMaintSubroutines(Assembler asm)
    {
        // ---- oft_find_first: EAX = firstBlock → EAX = 1 if any in-use OFT handle references
        // that file, else 0 (pure memory scan). ----
        asm.Label("oft_find_first");
        asm.Mov(R(R8), R(EAX));                  // target firstBlock
        asm.MovImm(R(R9), 0);                    // i
        asm.Label("ofd_loop");
        asm.MovImm(R(R10), OsLayout.MaxOpenFiles);
        asm.Cmp(R(R9), R(R10));
        asm.Jns("ofd_none");
        asm.Mov(R(R11), R(R9));
        asm.MovImm(R(R10), OsLayout.OftEntryBytes);
        asm.Mul(R(R11), R(R10));
        Imm16(asm, R10, OsLayout.OftBase);
        asm.Add(R(R11), R(R10));                 // R11 = OFT entry addr
        asm.Load(R(R12), R(R11));                // inUse
        asm.MovImm(R(R10), 0);
        asm.Cmp(R(R12), R(R10));
        asm.Jz("ofd_next");
        asm.Mov(R(R12), R(R11));
        asm.MovImm(R(R10), OsLayout.OftFirstBlock);
        asm.Add(R(R12), R(R10));
        asm.Load(R(R12), R(R12));                // this handle's firstBlock
        asm.Cmp(R(R12), R(R8));
        asm.Jz("ofd_found");
        asm.Label("ofd_next");
        asm.Inc(R(R9));
        asm.Jmp("ofd_loop");
        asm.Label("ofd_found");
        asm.MovImm(R(EAX), 1);
        asm.Ret();
        asm.Label("ofd_none");
        asm.MovImm(R(EAX), 0);
        asm.Ret();

        // ---- fs_unlink: EAX = abs path → EAX = 0, or -1. Resolve the parent + entry, refuse a
        // directory or an open file, free every block in the chain, then drop the dir entry. ----
        asm.Label("fs_unlink");
        asm.Call("fs_resolve_parent");           // EAX = parent dir block or -1; name in FsPathComponentBase
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("ul_fail");
        SpillStore(asm, OsLayout.FsOpenDirBlock, EAX);   // parent dir block
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);
        asm.Call("fs_dir_lookup");               // EAX = entry addr or -1 (EAX already = parent)
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("ul_fail");
        asm.Mov(R(R8), R(EAX));                  // entry addr
        asm.Load(R(R9), R(R8));                  // type
        asm.MovImm(R(EBX), FsLayout.DirTypeFile);
        asm.Cmp(R(R9), R(EBX));
        asm.Jnz("ul_fail");                      // only regular files (directories are refused)
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(R9), R(R9));                  // firstBlock
        SpillStore(asm, OsLayout.FsOpenFirst, R9);
        asm.Mov(R(EAX), R(R9));
        asm.Call("oft_find_first");
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jnz("ul_fail");                      // the file is open → refuse
        // free the whole block chain (read each block's next link before freeing it)
        SpillLoad(asm, OsLayout.FsOpenFirst, R8);
        SpillStore(asm, OsLayout.FsRwCurBlock, R8);
        asm.Label("ul_free_loop");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("ul_free_done");                  // end of chain (-1)
        asm.Call("fs_chain_next");               // EAX(block) → EAX = next
        SpillStore(asm, OsLayout.FsRwCounter, EAX);
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_free_block");
        SpillLoad(asm, OsLayout.FsRwCounter, EAX);
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Jmp("ul_free_loop");
        asm.Label("ul_free_done");
        SpillLoad(asm, OsLayout.FsOpenDirBlock, EAX);   // parent
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);
        asm.Call("fs_dir_remove");               // clear the entry (name still in FsPathComponentBase)
        asm.MovImm(R(EAX), 0);
        asm.Ret();
        asm.Label("ul_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_mkdir_path: EAX = abs path → EAX = new dir block, or -1. ----
        asm.Label("fs_mkdir_path");
        asm.Call("fs_resolve_parent");           // EAX = parent or -1; name in FsPathComponentBase
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("mp_fail");
        asm.Mov(R(EBX), R(EAX));                 // fs_mkdir wants EBX = parent dir
        Imm16(asm, ECX, OsLayout.FsPathComponentBase);
        asm.Call("fs_mkdir");                    // EAX = new dir block, or -1 (dup/full)
        asm.Ret();
        asm.Label("mp_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_readdir: EBX = dir block, ECX = index, EDX = out addr → EAX = entry type, or
        // -1 past the last in-use entry. Copies the whole 64-byte dir entry to the out buffer
        // (caller reads type/size/firstBlock/name at the DirEntry* offsets). ----
        asm.Label("fs_readdir");
        SpillStore(asm, OsLayout.FsRwCurBlock, EBX);    // current dir block
        SpillStore(asm, OsLayout.FsRwCounter, ECX);     // index countdown
        SpillStore(asm, OsLayout.FsRwBufPtr, EDX);      // out buffer
        asm.Label("rd_block");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Js("rd_end");                        // ran off the chain
        asm.Call("cache_get");                   // EAX = block data addr
        asm.Mov(R(R8), R(EAX));                  // R8 = block base (no cache calls in the entry loop)
        asm.MovImm(R(R9), 0);                    // entry index i
        asm.Label("rd_entry");
        asm.MovImm(R(EBX), FsLayout.DirEntriesPerBlock);
        asm.Cmp(R(R9), R(EBX));
        asm.Jns("rd_next_block");
        asm.Mov(R(R10), R(R9));
        asm.MovImm(R(EBX), FsLayout.DirEntryBytes);
        asm.Mul(R(R10), R(EBX));
        asm.Add(R(R10), R(R8));                  // R10 = entry addr
        asm.Load(R(R11), R(R10));                // type
        asm.MovImm(R(EBX), FsLayout.DirTypeFree);
        asm.Cmp(R(R11), R(EBX));
        asm.Jz("rd_skip");                       // free slot: not counted
        SpillLoad(asm, OsLayout.FsRwCounter, R12);
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R12), R(EBX));
        asm.Jz("rd_found");                      // countdown hit 0 → this entry
        asm.Dec(R(R12));
        SpillStore(asm, OsLayout.FsRwCounter, R12);
        asm.Label("rd_skip");
        asm.Inc(R(R9));
        asm.Jmp("rd_entry");
        asm.Label("rd_next_block");
        SpillLoad(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Call("fs_chain_next");
        SpillStore(asm, OsLayout.FsRwCurBlock, EAX);
        asm.Jmp("rd_block");
        asm.Label("rd_found");
        SpillLoad(asm, OsLayout.FsRwBufPtr, R12);       // out
        asm.MovImm(R(R13), 0);                   // byte offset j
        asm.Label("rd_copy");
        asm.MovImm(R(EBX), FsLayout.DirEntryBytes);
        asm.Cmp(R(R13), R(EBX));
        asm.Jns("rd_copy_done");
        asm.Mov(R(R14), R(R10));
        asm.Add(R(R14), R(R13));
        asm.Load(R(R14), R(R14));                // word from the entry
        asm.Mov(R(EBX), R(R12));
        asm.Add(R(EBX), R(R13));
        asm.Store(R(EBX), R(R14));               // → out buffer
        asm.MovImm(R(EBX), 4);
        asm.Add(R(R13), R(EBX));
        asm.Jmp("rd_copy");
        asm.Label("rd_copy_done");
        asm.Load(R(EAX), R(R10));                // return the entry type
        asm.Ret();
        asm.Label("rd_end");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- fs_resolve_dir: EAX = abs path → EAX = directory block, or -1. Like
        // fs_path_resolve but returns the *directory's* first block and handles the root path
        // ("/" or all-separators), which fs_path_resolve reports as -1. ----
        asm.Label("fs_resolve_dir");
        SpillStore(asm, OsLayout.FsOpenAbsPath, EAX);
        asm.Call("fs_path_resolve");             // EAX = entry addr or -1
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(EAX), R(EBX));
        asm.Jns("rsd_entry");                    // resolved to a directory entry
        // Not resolved: treat an all-separator path as the root directory.
        SpillLoad(asm, OsLayout.FsOpenAbsPath, R8);
        asm.Label("rsd_scan");
        asm.Load(R(R9), R(R8));
        asm.MovImm(R(EBX), 0);
        asm.Cmp(R(R9), R(EBX));
        asm.Jz("rsd_root");                      // null terminator, only separators seen → root
        asm.MovImm(R(EBX), OsLayout.FsPathSeparator);
        asm.Cmp(R(R9), R(EBX));
        asm.Jnz("rsd_fail");                     // a real component that did not resolve → fail
        asm.MovImm(R(EBX), 4);
        asm.Add(R(R8), R(EBX));
        asm.Jmp("rsd_scan");
        asm.Label("rsd_root");
        asm.Call("fs_root_dir");                 // EAX = root dir block
        asm.Ret();
        asm.Label("rsd_entry");
        asm.Mov(R(R8), R(EAX));                  // entry addr
        asm.Load(R(R9), R(R8));                  // type
        asm.MovImm(R(EBX), FsLayout.DirTypeDir);
        asm.Cmp(R(R9), R(EBX));
        asm.Jnz("rsd_fail");                     // not a directory
        asm.Mov(R(R9), R(R8));
        asm.MovImm(R(EBX), FsLayout.DirEntryFirstBlock);
        asm.Add(R(R9), R(EBX));
        asm.Load(R(EAX), R(R9));                 // dir block = entry.firstBlock
        asm.Ret();
        asm.Label("rsd_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();
    }
}
