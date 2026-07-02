namespace CSharpOS;

// Filesystem RAM write-back cache: IvtCacheOp dispatch + cache_* subroutines.
public static partial class OsRoutines
{
    // ---- filesystem RAM write-back cache (Increment 2) ----------------------

    // ===== EmitCacheOp =======================================================
    // IvtCacheOp: the kernel-internal buffer-cache control interface, dispatched with an op
    // selector in EAX and a block number in EBX. Sets up the privileged stack, routes to the
    // matching cache subroutine, and parks the result (cache_get's data address, else 0) in
    // the CacheResult header word so a synchronous test dispatch can read it from memory.
    private static void EmitCacheOp(Assembler asm)
    {
        asm.Label("cache_op");
        SetupPrivilegedStack(asm);
        asm.Mov(R(R13), R(EAX));                 // R13 = op selector
        asm.Mov(R(EAX), R(EBX));                 // EAX = block (subroutine calling convention)

        asm.MovImm(R(R14), Hardware.CacheOpGet);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_get");
        asm.MovImm(R(R14), Hardware.CacheOpDirty);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_dirty");
        asm.MovImm(R(R14), Hardware.CacheOpWriteThrough);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_wt");
        asm.MovImm(R(R14), Hardware.CacheOpPin);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_pin");
        asm.MovImm(R(R14), Hardware.CacheOpUnpin);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_unpin");
        asm.MovImm(R(R14), Hardware.CacheOpDiscard);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_discard");
        asm.MovImm(R(R14), Hardware.CacheOpFlush);
        asm.Cmp(R(R13), R(R14));
        asm.Jz("co_flush");
        asm.MovImm(R(R12), 0);                   // unknown op → result 0
        asm.Jmp("co_result");

        asm.Label("co_get");
        asm.Call("cache_get");
        asm.Mov(R(R12), R(EAX));
        asm.Jmp("co_result");
        asm.Label("co_dirty");
        asm.Call("cache_dirty");
        asm.MovImm(R(R12), 0);
        asm.Jmp("co_result");
        asm.Label("co_wt");
        asm.Call("cache_write_through");
        asm.MovImm(R(R12), 0);
        asm.Jmp("co_result");
        asm.Label("co_pin");
        asm.Call("cache_pin");
        asm.MovImm(R(R12), 0);
        asm.Jmp("co_result");
        asm.Label("co_unpin");
        asm.Call("cache_unpin");
        asm.MovImm(R(R12), 0);
        asm.Jmp("co_result");
        asm.Label("co_discard");
        asm.Call("cache_discard");
        asm.MovImm(R(R12), 0);
        asm.Jmp("co_result");
        asm.Label("co_flush");
        asm.Call("cache_flush");
        asm.MovImm(R(R12), 0);

        asm.Label("co_result");
        Imm16(asm, EBP, OsLayout.CacheResultOffset);
        asm.Store(R(EBP), R(R12));
        asm.MovImm(R(EAX), User);
        asm.OsRet(R(EAX));
    }

    // ===== EmitCacheSubroutines ==============================================
    // The cache manager as CALL/RET subroutines over the OsLayout cache slot table. Common
    // convention: the block number arrives in EAX; EAX returns a data address (cache_get) or
    // slot base (cache_find), or -1 when not resident / no evictable slot. EAX/EBP are helper
    // scratch (LoadField/StoreField clobber them); ESI is the scan index; R9 the slot base;
    // R10/R11/R14 scratch. cache_get additionally keeps the target block in R8 and the victim
    // choice in R12/R13 across its miss path.
    private static void EmitCacheSubroutines(Assembler asm)
    {
        // ---- cache_find: block in EAX → slot base in EAX, or -1 if not resident ----
        asm.Label("cache_find");
        asm.Mov(R(R11), R(EAX));                  // R11 = target block
        asm.MovImm(R(ESI), 0);
        asm.Label("cf_loop");
        asm.MovImm(R(R9), OsLayout.CacheSlotCount);
        asm.Cmp(R(ESI), R(R9));
        asm.Jns("cf_notfound");
        EmitCacheSlotBase(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.CacheValidField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cf_next");
        LoadField(asm, R9, OsLayout.CacheBlockField, R10);
        asm.Cmp(R(R10), R(R11));
        asm.Jz("cf_found");
        asm.Label("cf_next");
        asm.Inc(R(ESI));
        asm.Jmp("cf_loop");
        asm.Label("cf_found");
        asm.Mov(R(EAX), R(R9));
        asm.Ret();
        asm.Label("cf_notfound");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- cache_get: block in EAX → cached data address in EAX (or -1) ----
        asm.Label("cache_get");
        asm.Mov(R(R8), R(EAX));                   // R8 = target block (preserved across cache_find)
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cg_miss");
        // hit: stamp and return the data address
        asm.Mov(R(R9), R(EAX));
        EmitStampSlot(asm, R9);
        asm.Mov(R(EAX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(EAX), R(EBP));
        asm.Ret();

        // miss: pick a victim (an invalid slot first, else the lowest-stamp unpinned slot)
        asm.Label("cg_miss");
        asm.MovImm(R(R12), 0);
        asm.Dec(R(R12));                          // R12 = victim index = -1
        asm.MovImm(R(R13), 0);                    // R13 = best stamp (meaningful once R12 >= 0)
        asm.MovImm(R(ESI), 0);
        asm.Label("cg_vloop");
        asm.MovImm(R(R9), OsLayout.CacheSlotCount);
        asm.Cmp(R(ESI), R(R9));
        asm.Jns("cg_vchosen");
        EmitCacheSlotBase(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.CacheValidField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cg_found_invalid");               // empty slot: best possible victim
        LoadField(asm, R9, OsLayout.CachePinField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jnz("cg_vnext");                      // pinned: never evict
        LoadField(asm, R9, OsLayout.CacheStampField, R14);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R12), R(EAX));
        asm.Js("cg_take");                        // no victim yet
        asm.Cmp(R(R14), R(R13));
        asm.Js("cg_take");                        // stamp < best
        asm.Jmp("cg_vnext");
        asm.Label("cg_take");
        asm.Mov(R(R12), R(ESI));
        asm.Mov(R(R13), R(R14));
        asm.Label("cg_vnext");
        asm.Inc(R(ESI));
        asm.Jmp("cg_vloop");

        asm.Label("cg_found_invalid");
        asm.Mov(R(R12), R(ESI));
        asm.Jmp("cg_load");

        asm.Label("cg_vchosen");
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R12), R(EAX));
        asm.Js("cg_fail");                        // no evictable slot (all valid + pinned)
        // flush the victim first if it is dirty
        EmitCacheSlotBase(asm, R12, R9);
        LoadField(asm, R9, OsLayout.CacheDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cg_load");
        LoadField(asm, R9, OsLayout.CacheBlockField, EBX);
        asm.Mov(R(ECX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(ECX), R(EBP));
        asm.FbWrite(R(EBX), R(ECX));              // write back the evicted block

        asm.Label("cg_load");
        EmitCacheSlotBase(asm, R12, R9);          // recompute base (invalid + dirty paths merge here)
        asm.Mov(R(ECX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(ECX), R(EBP));
        asm.FbRead(R(ECX), R(R8));                // load the requested block into the slot
        StoreFieldImm(asm, R9, OsLayout.CacheValidField, 1);
        StoreFieldReg(asm, R9, OsLayout.CacheBlockField, R8);
        StoreFieldImm(asm, R9, OsLayout.CacheDirtyField, 0);
        StoreFieldImm(asm, R9, OsLayout.CachePinField, 0);
        EmitStampSlot(asm, R9);
        asm.Mov(R(EAX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(EAX), R(EBP));
        asm.Ret();

        asm.Label("cg_fail");
        asm.MovImm(R(EAX), 0);
        asm.Dec(R(EAX));
        asm.Ret();

        // ---- cache_dirty: mark the resident block dirty ----
        asm.Label("cache_dirty");
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cd_done");
        asm.Mov(R(R9), R(EAX));
        StoreFieldImm(asm, R9, OsLayout.CacheDirtyField, 1);
        asm.Label("cd_done");
        asm.Ret();

        // ---- cache_write_through: flush the resident block now, leave it clean ----
        asm.Label("cache_write_through");
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cwt_done");
        asm.Mov(R(R9), R(EAX));
        LoadField(asm, R9, OsLayout.CacheBlockField, EBX);
        asm.Mov(R(ECX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(ECX), R(EBP));
        asm.FbWrite(R(EBX), R(ECX));
        StoreFieldImm(asm, R9, OsLayout.CacheDirtyField, 0);
        asm.Label("cwt_done");
        asm.Ret();

        // ---- cache_pin: increment the resident block's pin count ----
        asm.Label("cache_pin");
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cp_done");
        asm.Mov(R(R9), R(EAX));
        LoadField(asm, R9, OsLayout.CachePinField, R10);
        asm.Inc(R(R10));
        StoreFieldReg(asm, R9, OsLayout.CachePinField, R10);
        asm.Label("cp_done");
        asm.Ret();

        // ---- cache_unpin: decrement the pin count (floored at 0) ----
        asm.Label("cache_unpin");
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cu_done");
        asm.Mov(R(R9), R(EAX));
        LoadField(asm, R9, OsLayout.CachePinField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cu_done");                        // already 0
        asm.Dec(R(R10));
        StoreFieldReg(asm, R9, OsLayout.CachePinField, R10);
        asm.Label("cu_done");
        asm.Ret();

        // ---- cache_discard: drop the block without write-back (clear valid+dirty+pin) ----
        asm.Label("cache_discard");
        asm.Call("cache_find");
        asm.MovImm(R(R9), 0);
        asm.Cmp(R(EAX), R(R9));
        asm.Js("cds_done");
        asm.Mov(R(R9), R(EAX));
        StoreFieldImm(asm, R9, OsLayout.CacheValidField, 0);
        StoreFieldImm(asm, R9, OsLayout.CacheDirtyField, 0);
        StoreFieldImm(asm, R9, OsLayout.CachePinField, 0);
        asm.Label("cds_done");
        asm.Ret();

        // ---- cache_flush: write back every dirty, unpinned, valid slot ----
        asm.Label("cache_flush");
        asm.MovImm(R(ESI), 0);
        asm.Label("cfl_loop");
        asm.MovImm(R(R9), OsLayout.CacheSlotCount);
        asm.Cmp(R(ESI), R(R9));
        asm.Jns("cfl_done");
        EmitCacheSlotBase(asm, ESI, R9);
        LoadField(asm, R9, OsLayout.CacheValidField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cfl_next");
        LoadField(asm, R9, OsLayout.CacheDirtyField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jz("cfl_next");
        LoadField(asm, R9, OsLayout.CachePinField, R10);
        asm.MovImm(R(EAX), 0);
        asm.Cmp(R(R10), R(EAX));
        asm.Jnz("cfl_next");
        LoadField(asm, R9, OsLayout.CacheBlockField, EBX);
        asm.Mov(R(ECX), R(R9));
        asm.MovImm(R(EBP), OsLayout.CacheDataField);
        asm.Add(R(ECX), R(EBP));
        asm.FbWrite(R(EBX), R(ECX));
        StoreFieldImm(asm, R9, OsLayout.CacheDirtyField, 0);
        asm.Label("cfl_next");
        asm.Inc(R(ESI));
        asm.Jmp("cfl_loop");
        asm.Label("cfl_done");
        asm.Ret();
    }

}
