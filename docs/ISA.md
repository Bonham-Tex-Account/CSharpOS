# CSharpOS Instruction Set Architecture

This document is the authoritative reference for the CSharpOS ISA. Source of truth: the files listed in the "Keeping this updated" section at the bottom.

---

## Table of Contents

1. [Instruction encoding](#instruction-encoding)
2. [Register set](#register-set)
3. [Flags](#flags)
4. [Complete instruction list](#complete-instruction-list)
   - [Data movement](#data-movement)
   - [Arithmetic and logic](#arithmetic-and-logic)
   - [Control flow](#control-flow)
   - [I/O](#io)
   - [String and key I/O](#string-and-key-io)
   - [Process control](#process-control)
   - [OS / privileged support](#os--privileged-support)
   - [Disk](#disk)
   - [Filesystem](#filesystem)
5. [Addressing and program-relative offsets](#addressing-and-program-relative-offsets)
6. [Privilege levels and mode transitions](#privilege-levels-and-mode-transitions)
7. [Worked examples](#worked-examples)
8. [Unsupported and planned instructions](#unsupported-and-planned-instructions)
9. [Keeping this updated](#keeping-this-updated)

---

## Instruction encoding

Every instruction is a fixed 4-byte word:

```
byte 0  byte 1  byte 2  byte 3
opcode    b1      b2      b3
```

- **opcode** — one of the constants in `Instruction.cs`.
- **b1, b2, b3** — operands whose meaning depends on the instruction (register index, immediate, address bytes, or unused/zero).

Register operands are encoded as the numeric value of the `RegisterName` enum (EAX=0, EBX=1, ECX=2, …; see the register table below). The `Assembler` class casts `(byte)RegisterName.X` when it emits each instruction.

---

## Register set

The register file holds 24 × 32-bit integers. Source: `CSharpOS/Enums/RegisterName.cs`.

| Index | Name    | Conventional use |
|-------|---------|------------------|
| 0     | EAX     | General / return value / argument |
| 1     | EBX     | General / entry address scratch |
| 2     | ECX     | General / loop counter / current index |
| 3     | EDX     | General / argument / scratch |
| 4     | ESI     | General / source / scan counter |
| 5     | EDI     | General / destination / process count |
| 6     | ESP     | Stack pointer (offset from program base, not absolute) |
| 7     | EBP     | General / base pointer / bit-address scratch |
| 8     | EIP     | Instruction pointer (saved as program-relative offset in process entries) |
| 9     | EFLAGS  | Flags register — only bits 0 (ZeroFlag) and 1 (SignFlag) are used |
| 10    | CS      | Segment register (unused by user code; reserved) |
| 11    | DS      | Segment register (unused by user code; reserved) |
| 12    | ES      | Segment register (unused by user code; reserved) |
| 13    | FS      | Segment register (unused by user code; reserved) |
| 14    | GS      | Segment register (unused by user code; reserved) |
| 15    | SS      | Segment register (unused by user code; reserved) |
| 16    | R8      | Extended — used heavily by OS ISA routines (scheduler, buddy allocator) |
| 17    | R9      | Extended |
| 18    | R10     | Extended |
| 19    | R11     | Extended |
| 20    | R12     | Extended |
| 21    | R13     | Extended |
| 22    | R14     | Extended |
| 23    | R15     | Extended |

The segment registers (CS–SS) are present in the register file for symmetry but are not wired up to any segmentation logic. All addressing is done through program-base-relative arithmetic.

---

## Flags

`EFLAGS` is updated by every arithmetic, logic, shift, INC, and DEC instruction (and by CMP). Only two flag bits are defined:

| Bit | Mask | Name      | Set when                     | Cleared when             |
|-----|------|-----------|------------------------------|--------------------------|
| 0   | 0x01 | ZeroFlag  | result == 0                  | result != 0              |
| 1   | 0x02 | SignFlag  | result < 0 (signed 32-bit)  | result >= 0              |

There is no carry, overflow, parity, or auxiliary-carry flag.

---

## Complete instruction list

48 opcodes are currently defined. The "Assembler method" column shows the C# `Assembler` method that emits the instruction.

### Data movement

| Mnemonic | Opcode | Bytes (b1 b2 b3) | Assembler method | Description |
|----------|--------|-------------------|------------------|-------------|
| `MOV dst, src` | 0x01 | dst, src, 0 | `Mov(dst, src)` | `reg[dst] = reg[src]`. Flags unchanged. |
| `MOV dst, imm8` | 0x02 | dst, imm8, 0 | `MovImm(dst, imm)` | `reg[dst] = (int)(byte)imm`. Loads 0–255 (zero-extended). Flags unchanged. |
| `MOV dst, imm16` | 0x03 | dst, hi, lo | `MovImm16(dst, imm)` | `reg[dst] = (hi << 8) | lo`. Loads 0–65535 (zero-extended). Flags unchanged. Used for OS memory offsets that exceed 255. |
| `LOAD dst, [ptr]` | 0x05 | dst, ptr, 0 | `Load(dst, ptr)` | `reg[dst] = *(programBase + reg[ptr])`. Reads a little-endian 32-bit word from memory. See addressing rules below. |
| `STORE [ptr], src` | 0x06 | ptr, src, 0 | `Store(ptr, src)` | `*(programBase + reg[ptr]) = reg[src]`. Writes a little-endian 32-bit word to memory. |

**Note on MOV imm8 vs imm16:** `MovImm` truncates the C# `int` argument to a byte, so passing a value outside 0–255 silently loses the high bits. Use `MovImm16` for any offset or address above 255.

There is no opcode 0x04 (gap reserved for future use).

### Arithmetic and logic

All arithmetic and logic instructions (except bitwise NOT) take two register operands `(dest, src)` and write the result back into `dest`. All update EFLAGS from the result.

| Mnemonic | Opcode | b1, b2 | Assembler method | Description |
|----------|--------|--------|------------------|-------------|
| `ADD dst, src` | 0x10 | dst, src | `Add(dst, src)` | `dst += src` |
| `SUB dst, src` | 0x11 | dst, src | `Sub(dst, src)` | `dst -= src` |
| `MUL dst, src` | 0x12 | dst, src | `Mul(dst, src)` | `dst *= src` |
| `DIV dst, src` | 0x13 | dst, src | `Div(dst, src)` | `dst /= src` (integer, C# signed division) |
| `CMP a, b` | 0x14 | a, b | `Cmp(a, b)` | Sets flags from `a - b`; neither register is modified. |
| `INC dst` | 0x15 | dst, 0 | `Inc(dst)` | `dst += 1` |
| `DEC dst` | 0x16 | dst, 0 | `Dec(dst)` | `dst -= 1` |
| `AND dst, src` | 0x17 | dst, src | `And(dst, src)` | `dst &= src` |
| `OR dst, src` | 0x18 | dst, src | `Or(dst, src)` | `dst \|= src` |
| `XOR dst, src` | 0x19 | dst, src | `Xor(dst, src)` | `dst ^= src` |
| `NOT dst` | 0x1A | dst, 0 | `Not(dst)` | `dst = ~dst` (bitwise complement) |
| `SHL dst, cnt` | 0x1B | dst, cnt | `Shl(dst, cnt)` | `dst <<= reg[cnt] & 0x1F`. Logical left shift; count masked to 5 bits (0–31). |
| `SHR dst, cnt` | 0x1C | dst, cnt | `Shr(dst, cnt)` | `dst = (uint)dst >> (reg[cnt] & 0x1F)`. **Logical** (unsigned) right shift; high bits filled with zero. |

**DIV:** integer division truncates toward zero (C# default for `int / int`). Division by zero throws a `DivideByZeroException` from the C# runtime; there is no fault-to-OS path for this case.

**SHL/SHR:** the shift count is read from a register at runtime — there is no immediate shift count form.

### Control flow

Jump targets are program-relative 16-bit offsets encoded in b1 (high byte) and b2 (low byte). The CPU computes the absolute target as `programBase + ((b1 << 8) | b2)`.

When the assembler resolves a label, it adds the `origin` argument of `Build()` to the label's code position. Code assembled with `origin = 0` (default) uses offsets relative to the start of the byte array; code assembled with a non-zero origin (e.g. OS routines, which start at `OsLayout.CodeBase`) encodes absolute addresses directly.

| Mnemonic | Opcode | Assembler method | Condition | Description |
|----------|--------|------------------|-----------|-------------|
| `JMP label` | 0x20 | `Jmp(label)` | Always | Unconditional jump to `programBase + target`. |
| `JZ label` | 0x21 | `Jz(label)` | ZeroFlag == 1 | Jump if last result was zero (e.g. after CMP a,b: jump if a == b). |
| `JNZ label` | 0x22 | `Jnz(label)` | ZeroFlag == 0 | Jump if last result was not zero. |
| `JS label` | 0x25 | `Js(label)` | SignFlag == 1 | Jump if last result was negative. |
| `JNS label` | 0x26 | `Jns(label)` | SignFlag == 0 | Jump if last result was non-negative (zero or positive). |
| `CALL label` | 0x23 | `Call(label)` | — | Push return offset (IP - programBase) onto the stack at `programBase + ESP - 4`; decrement ESP by 4; jump to target. |
| `RET` | 0x24 | `Ret()` | — | Pop return offset from `programBase + ESP`; increment ESP by 4; jump to `programBase + returnOffset`. |

**Position-independent stack:** `ESP` holds a program-base-relative offset, not an absolute address. Both `CALL` and `RET` store and restore the return offset as a base-relative value. This means a forked child (same code at a different physical base) correctly returns into its own image.

Opcode gaps: opcodes 0x27–0x2F are undefined (jump if carry, jump if overflow, etc. — not implemented).

### I/O

`OUT` and `IN` are the user-mode I/O syscall mechanism. When executed in user mode, they do **not** perform the I/O directly; instead they call `hw.EnterKernel()`, which:
1. Pushes a trap frame (the saved user register file + trap-info) onto the base of this process's kernel stack and sets `EBP` to it.
2. Records trap-info (faulting opcode, operand byte-offset, return IP) within that frame.
3. Sets the CPU to Kernel mode (interrupts stay enabled — the handler is preemptible).
4. Jumps to the **shared** syscall handler in the OS region (address in IVT slot `IvtSyscall`).

The shared handler (`EmitSyscall` in `OsRoutines.cs`) reads the faulting opcode via `EBP`, dispatches to the real `OUT` or `IN` call (which now executes at Kernel level), and calls `IRET` to return to user mode. Because the trap frame lives on the per-process kernel stack, a syscall that blocks mid-handler survives a context switch without clobbering another process's in-flight syscall.

| Mnemonic | Opcode | Assembler method | Description |
|----------|--------|------------------|-------------|
| `OUT reg` | 0x30 | `Out(reg)` | Write `reg[reg]` to the process's stdout device (fd 1). In user mode: trap to kernel. In kernel mode: deliver to device, blocking if the device is output-busy. |
| `IN reg` | 0x31 | `In(reg)` | Read from the process's stdin device (fd 0) into `reg[reg]`. In user mode: trap to kernel. In kernel mode: dequeue from device buffer or block the process until input arrives. |
| `HLT` | 0x32 | `Hlt()` | Terminate the running process with exit status 0. Dispatches `IvtHalt`; the OS frees memory, resolves parent/zombie/orphan logic, and schedules the next process. |
| `IRET` | 0x33 | `Iret()` | Return from a kernel-mode syscall handler to user mode. Restores the user register file from the kernel-stack trap frame and resumes user code at the saved return IP. **Forbidden in user mode** (trapped as an invalid instruction by `IretTrapProvider`). |

### String and key I/O

Like `OUT`/`IN`, these are user-mode instructions that trap to the shared kernel syscall handler (`EnterKernel` → `IvtSyscall`) when executed in User mode; they run directly when already in Kernel mode.

| Mnemonic | Opcode | Bytes (b1 b2 b3) | Assembler method | Description |
|----------|--------|-------------------|------------------|-------------|
| `OUTS [ptr], len` | 0x47 | ptr, len, 0 | `Outs(ptr, len)` | Reads `reg[len]` words starting at virtual address `reg[ptr]`, outputs the low byte of each as a character, and stops early at the first null word. User mode traps via `EnterKernel(OUTS, reg[ptr]*4, reg[len]*4)`. |
| `INS [ptr], maxLen` | 0x48 | ptr, maxLen, 0 | `Ins(ptr, maxLen)` | Blocks (`WaitReason.StringInput`) until a line is available on stdin's string queue, then writes each character as a zero-extended word into `ptr[0..n)` and null-terminates. User mode traps via `EnterKernel(INS, reg[ptr]*4, reg[maxLen]*4)`. |
| `INK dst` | 0x49 | dst, 0, 0 | `Ink(dst)` | Blocks (`WaitReason.KeyInput`) until a raw keypress arrives, then delivers the keycode into `reg[dst]`. User mode traps via `EnterKernel(INK, reg[dst]*4, 0)`. |
| `INPOLL dst` | 0x4A | dst, 0, 0 | `InkPoll(dst)` | Non-blocking: delivers the next queued keycode into `reg[dst]`, or −1 if no key is queued. Never blocks. User mode traps via `EnterKernel(INPOLL, reg[dst]*4, 0)`. |

**Raw key codes:** ASCII 32–126 are delivered as-is. Special keys use values above the ASCII range, defined as `Hardware` constants:

| Constant | Value |
|----------|-------|
| `KeyUp` | 256 |
| `KeyDown` | 257 |
| `KeyLeft` | 258 |
| `KeyRight` | 259 |

**Wait reasons introduced by these instructions:** `WaitReason.StringInput = 4` (blocked in `INS`), `WaitReason.KeyInput = 5` (blocked in `INK`). A dedicated IVT slot, `IvtWakeKey` (slot 16), wakes the first process blocked on a raw keypress; string and int input share the ordinary `IvtWakeInput` path.

### Process control

These instructions are available to user-mode processes. They trap into the privileged OS routines listed (the pattern mirrors `HLT`).

| Mnemonic | Opcode | Assembler method | OS routine | Description |
|----------|--------|------------------|------------|-------------|
| `FORK` | 0x34 | `Fork()` | `IvtFork` | Duplicate the running process. The parent receives the child's PID in EAX; the child receives 0. On failure (table full or out of memory) the parent receives −1. |
| `EXEC reg` | 0x35 | `Exec(reg)` | `IvtExec` | Replace the running process's image with the program stored in disk slot `reg[reg]`. The PID and parent-child relationship are preserved. On out-of-memory, the process is terminated. |
| `WAIT reg` | 0x36 | `Wait(reg)` | `IvtWait` | Block until the child with PID `reg[reg]` terminates. Its exit status is delivered in EAX. Returns immediately if the child is already a zombie. |
| `EXIT reg` | 0x37 | `Exit(reg)` | `IvtHalt` | Terminate the running process with exit status `reg[reg]`. Same tear-down as HLT. |
| `SETFOCUS reg` | 0x38 | `SetFocus(reg)` | (C# path) | Make the process with PID `reg[reg]` the foreground (focused) process. The live keyboard and screen follow it. A no-op if the PID is not a live process. |

### OS / privileged support

These instructions are used exclusively by the OS ISA routines running in Kernel mode with interrupts masked (atomic). They have no user-mode guards enforced by traps, but calling them from user mode is incorrect: SAVEREGS/LOADREGS read and write absolute memory addresses, SETLAYOUT reconfigures the hardware layout, and OSRET manipulates the staged context and privilege level in ways that corrupt process state. Only DREAD/DWRITE/DLEN and FBREAD/FBWRITE have explicit user-mode faults (see [Disk](#disk)).

| Mnemonic | Opcode | Assembler method | Description |
|----------|--------|------------------|-------------|
| `SAVEREGS [ptr]` | 0x40 | `SaveRegs(ptr)` | Write the captured interrupt frame (the interrupted process's register file, with EIP folded in as a base-relative offset) to the absolute address `reg[ptr]`, followed by the interrupted privilege level. Used by the context-switch routine to persist a process's state to its process-table entry. |
| `LOADREGS [ptr]` | 0x41 | `LoadRegs(ptr)` | Stage the register file stored at absolute address `reg[ptr]` for `OSRET` to commit. Does not touch the live registers immediately (the routine may still use them as scratch until OSRET). |
| `SETLAYOUT [ptr]` | 0x42 | `SetLayout(ptr)` | Reload the hardware memory layout (program base, memory region, stack extents) from the process-table entry at absolute address `reg[ptr]`. Must be called before OSRET so program-relative addressing targets the correct process. |
| `OSRET reg` | 0x43 | `OsRet(reg)` | Atomically commit the context staged by LOADREGS, restore the IP from the EIP slot of that context (resolved against the program base for `reg[reg]`'s privilege level), and drop the CPU to privilege level `reg[reg]`. If no context was staged, sets `processRunning = false` (idle). |

**OSRET is the only path back from an atomic OS routine** (it re-enables interrupts). An OS routine always ends with a `LOADREGS`/`SETLAYOUT`/`OSRET` sequence (or a `JMP resume_mlfq` that eventually reaches one).

### Disk

Block-device transfers between RAM and the `Bin` disk. The disk is only accessible from Kernel mode; executing these in User mode calls `hw.TrapInvalidInstruction` (faults the process).

`Bin` has two independent address spaces: a **slot region** (variable-length, occupied/length directory — holds process program images and demand-paging swap) and a **file-block region** (fixed-size, block-addressed — holds the filesystem). `DREAD`/`DWRITE`/`DLEN` operate on the slot region; `FBREAD`/`FBWRITE` operate on the file-block region.

| Mnemonic | Opcode | Assembler method | Description |
|----------|--------|------------------|-------------|
| `DREAD [dst], slot, lenOut` | 0x44 | `DRead(dst, slot, lenOut)` | Copy disk slot `reg[slot]` into RAM at **absolute** address `reg[dst]`. Write the number of bytes copied into `reg[lenOut]`. |
| `DWRITE slot, [src], len` | 0x45 | `DWrite(slot, src, len)` | Copy `reg[len]` bytes from **absolute** address `reg[src]` into disk slot `reg[slot]`. |
| `DLEN slot, lenOut` | 0x46 | `DLen(slot, lenOut)` | Write the stored byte length of disk slot `reg[slot]` into `reg[lenOut]`. Used by `IvtExec` to size the new image's allocation before loading it. |
| `FBREAD [dst], block` | 0x4B | `FbRead(dst, block)` | Copy `Hardware.DefaultFileBlockSize` (256) bytes from file-block `reg[block]` into RAM at **absolute** address `reg[dst]`. Reading a never-written block yields zeros (raw block-device semantics; never throws). |
| `FBWRITE block, [src]` | 0x4C | `FbWrite(block, src)` | Copy 256 bytes from **absolute** address `reg[src]` into file-block `reg[block]`. |

**Disk addresses are absolute** (program base = 0 in Kernel mode), not program-relative. `FBREAD`/`FBWRITE` are used exclusively by the filesystem's `cache_get`/`cache_write_through`/`cache_flush` ISA subroutines (see [Filesystem](#filesystem)) — user code never issues them directly, and they trap the same way `DREAD`/`DWRITE`/`DLEN` do if a user process attempts them.

---

### Filesystem

`FSYS` is the single user-mode entry point into the filesystem. It does **not** use the `OUT`/`IN`-style `EnterKernel` trap path (preemptible, shared handler in Kernel mode with interrupts enabled). Instead, like `FORK`/`EXEC`/`WAIT`/`EXIT`, it dispatches `IvtFsSyscall` (slot 18) through the ordinary atomic `DispatchOsRoutine` path (interrupts masked) — the whole syscall runs to completion without preemption, then resumes the caller (SAVEREGS the captured trap frame → compute → write the result into the saved EAX slot → LOADREGS/SETLAYOUT/OSRET), the same idiom `IvtWait`'s zombie-reap path uses.

The `Read`/`Write` data buffers are translated one page-chunk at a time through the calling process's page table (via a new privileged routine, `user_word_addr`, and IVT slot `IvtEnsureUserPage = 19` / `Hardware.UserToPhysical`) before the transfer runs, so a buffer in demand-paged or swapped-out process memory round-trips correctly — this constraint that used to apply to FSYS read/write buffers is gone. The `Open`/`Exec`/`Unlink`/`Mkdir`/`Readdir` **path** pointers are still translated with flat `ProgramAddress + ptr` arithmetic, so a path argument must still live in a RAM-home page (the process's program image).

| Mnemonic | Opcode | Bytes (b1 b2 b3) | Assembler method | Description |
|----------|--------|-------------------|------------------|-------------|
| `FSYS` | 0x4D | 0, 0, 0 | *(none — assembled inline; see below)* | `EAX` = syscall number, `EBX`/`ECX`/`EDX` = arguments (meaning depends on the syscall). Dispatches `IvtFsSyscall` atomically; the result is delivered back into the caller's `EAX` when the syscall completes. |

**Syscall numbers** (value of `EAX` on entry):

| # | Name | Arguments | Result |
|---|------|-----------|--------|
| 0 | Open | `EBX` = pointer to a null-terminated word-per-char path (user-relative), `ECX` = flags (`FsysCreateFlag = 1` creates the file if missing) | fd (2–7), or −1 |
| 1 | Read | `EBX` = fd, `ECX` = buffer pointer (user-relative), `EDX` = count | characters read, or −1 |
| 2 | Write | `EBX` = fd, `ECX` = buffer pointer (user-relative), `EDX` = count | characters written, or −1 |
| 3 | Close | `EBX` = fd | 0, or −1 |
| 4 | Exec | `EBX` = pointer to a null-terminated word-per-char path (user-relative) | does not return on success (the process image is replaced and resumes at its new entry point); −1 if the path is missing or is a directory |
| 5 | Unlink | `EBX` = pointer to a null-terminated word-per-char path (user-relative) | 0, or −1 (missing path, a directory, or the file is currently open) |
| 6 | Mkdir | `EBX` = pointer to a null-terminated word-per-char path (user-relative) | new directory's block number, or −1 |
| 7 | Readdir | `EBX` = pointer to a null-terminated word-per-char directory path (user-relative; `"/"` = root), `ECX` = entry index, `EDX` = pointer to a 64-byte output buffer (user-relative) | the entry's type (1=file, 2=dir), or −1 past the last entry |

File descriptors 2–7 are per-process (`ProcessEntryFdTable`, `FdCount = 8`; fd 0/1 are reserved for stdin/stdout). `FSYS` always operates on the calling process — this is the only difference from the `IvtFsOp` selectors used internally and by tests, which take an absolute path/buffer and an explicit process index.

**Directories cannot be opened for read/write** — `Open` on a path that resolves to a directory entry fails with −1. `Unlink` frees the file's entire block chain (not just its directory entry) and refuses to remove a directory or a file that is currently open. `fs_unlink`/`fs_mkdir_path`/`fs_readdir` are the ISA subroutines behind syscalls 5–7 (see `docs/OS-Architecture.md`, "The ISA filesystem").

See `docs/OS-Architecture.md`, section "The ISA filesystem", for the on-disk layout, the buffer cache, the block allocator, and the full syscall implementation.

---

## Addressing and program-relative offsets

The CPU computes effective memory addresses as `programBase + reg[ptr]`. The program base depends on the current privilege level:

| Privilege level | Program base |
|-----------------|-------------|
| User | `currentProcessInstructionStart` (the process's code section start in physical RAM) |
| Kernel | 0 (absolute — the shared syscall handler and the OS routines can reach any address) |

Because all address arithmetic adds the program base, programs that only reference self-relative offsets work correctly when loaded at any physical address. This is the position-independent model used by `FORK` (the child is a byte-for-byte copy at a different base).

**Label fixups and origin:** the `Assembler.Build(int origin)` method shifts all resolved label offsets by `origin`. User programs are typically built with `Build()` (origin 0). OS routines are built with `Build(OsLayout.CodeBase)` so their jump targets encode absolute OS-region addresses (which Kernel mode resolves correctly against base 0).

**Memory-mapped address limits:** LOAD and STORE data addresses in User mode are translated through the running process's page table (the MMU — see `docs/OS-Architecture.md`, "Virtual memory and demand paging"). The MMU is the sole memory-protection mechanism: an access to a page outside the process's mapped extent (an unmapped PTE, or a page index ≥ `MaxPagesPerProcess`) is a **protection fault** that terminates the process (exit status −1), the same teardown path as an invalid instruction. There is no linear address fallback and no separate LOAD/STORE bounds trap.

---

## Privilege levels and mode transitions

Source: `CSharpOS/Enums/PrivilegeLevel.cs`, `CSharpOS/CPU/Hardware.cs`, `BasicOSPlugin/Traps/`.

### The two levels

Like real hardware, there are **two** privilege levels. Atomicity of OS routines is **not** a third level — it is the hardware **interrupt-enable flag** (`Hardware.InterruptsEnabled()`, the CPU's IF). OS routines run with interrupts masked (atomic, never preempted); the syscall handler runs with them enabled (preemptible).

| Level | Value | Who runs here | Interrupts | Notes |
|-------|-------|---------------|------------|-------|
| User | 0 | Process user code | Enabled | Traps evaluated before every instruction. I/O (`OUT`/`IN`) traps to kernel. Addresses relative to the program base. |
| Kernel | 1 | The shared syscall handler **and** the OS ISA routines | Syscall handler: enabled (preemptible). OS routines: masked (atomic). | Unrestricted; addresses the OS region absolutely (base 0). |

The run loop treats code as atomic exactly when interrupts are masked: while `!InterruptsEnabled()` it neither preempts at the quantum nor dispatches a pending interrupt.

### Restrictions enforced in user mode

The following operations are forbidden (or redirected) when the CPU is at User level:

| Operation | How enforced | Effect |
|-----------|-------------|--------|
| `IRET` | `IretTrapProvider` (OS trap table) | Faults the process (invalid instruction). |
| `LOAD`/`STORE` outside the process's mapped page extent | MMU (`Hardware.TryTranslateData` → `RaiseProtectionFault`) | Terminates the process (protection fault, exit status −1). Not a trap-table entry — enforced by the MMU on every data access. |
| `OUT reg` | Hardware in `InstructionFunctions.Out` | Redirected: calls `hw.EnterKernel(OUT, ...)` instead of executing directly. |
| `IN reg` | Hardware in `InstructionFunctions.In` | Redirected: calls `hw.EnterKernel(IN, ...)`. |
| `OUTS`, `INS`, `INK`, `INPOLL` | Hardware in the respective `InstructionFunctions` handlers | Redirected: each calls `hw.EnterKernel(...)` with its own opcode/operand encoding, same pattern as `OUT`/`IN`. |
| `DREAD`, `DWRITE`, `DLEN`, `FBREAD`, `FBWRITE` | Hardware in `InstructionFunctions.DRead/DWrite/DLen/FbRead/FbWrite` | Faults the process via `hw.TrapInvalidInstruction`. |
| `FORK`, `EXEC`, `WAIT`, `EXIT`, `FSYS` | Hardware in the respective `InstructionFunctions` handlers | Not user-mode-restricted — these dispatch an atomic OS routine directly (`DispatchOsRoutine`) from either privilege level; the routine itself enforces any needed checks. |

`SAVEREGS`, `LOADREGS`, `SETLAYOUT`, and `OSRET` do not have explicit user-mode guards, but using them from user mode produces undefined behavior (clobbering the OS's process-table data and hardware layout state).

### Mode transitions

**User → Kernel (I/O syscall):**
Triggered by `OUT`/`IN` in user mode. `Hardware.EnterKernel` pushes a trap frame — the saved user register file (offset 0) plus trap-info (opcode, operand byte-offset, return IP) at offset `KernelTrapInfoOffset` — onto the base of this process's kernel stack, sets `EBP` to that frame, points `ESP` at the kernel-stack top, sets the CPU to Kernel mode (interrupts stay **enabled**, so the handler is preemptible), and jumps to the shared syscall handler whose address is in IVT slot `IvtSyscall`.

**Kernel → User (IRET):**
`Hardware.Iret` restores the user register file from the kernel-stack trap frame, sets the CPU to User mode, re-enables interrupts, and resumes at the saved return IP (user-relative).

**→ atomic OS routine (IVT dispatch):**
`Hardware.DispatchOsRoutine(slot)` (called by the hardware run loop, by I/O fault paths, and by process-control instructions — `FORK`, `EXEC`, `WAIT`, `EXIT`, and `FSYS`): snapshots the interrupted process's registers plus its current IP (base-relative) into `trapFrame`, **masks interrupts** (making the routine atomic), sets the CPU to Kernel mode, and jumps to the address stored in the IVT at `0 + slot * 4`.

**OS routine → resumed process (OSRET):**
The OS routine calls `LOADREGS [entry]` (stages the target process's register file) then `SETLAYOUT [entry]` (rebuilds the hardware layout) then `OSRET levelReg`. `Hardware.OsReturn` commits the staged context, resolves the EIP slot against the target level's program base, drops to that level, **re-enables interrupts**, and sets `processRunning = true`.

---

## Worked examples

These examples use `CSharpOS.Assembler`. Coding standards apply: explicit types, curly braces, no ternary.

### Example 1 — counting loop

Count 1 to 5 and output each value.

```csharp
Assembler asm = new Assembler();

asm.MovImm(RegisterName.EAX, 0);   // counter = 0
asm.MovImm(RegisterName.EBX, 5);   // limit = 5

asm.Label("loop");
asm.Inc(RegisterName.EAX);         // counter++
asm.Out(RegisterName.EAX);         // output counter (traps to kernel; kernel calls real OUT)
asm.Cmp(RegisterName.EAX, RegisterName.EBX);
asm.Jnz("loop");                   // repeat while counter != limit

asm.Hlt();                         // exit with status 0

byte[] image = asm.Build();
```

Disassembled (offsets in bytes, instructions at 4-byte steps):

```
 0: MOV EAX, 0
 4: MOV EBX, 5
 8: INC EAX
12: OUT EAX
16: CMP EAX, EBX
20: JNZ 8           ; jump to offset 8 (the INC)
24: HLT
```

### Example 2 — CALL/RET subroutine

Call a subroutine that doubles a value.

```csharp
Assembler asm = new Assembler();

asm.MovImm(RegisterName.EAX, 7);   // argument: 7
asm.Call("double");                // call subroutine; return address pushed onto stack
asm.Out(RegisterName.EAX);        // output result (14)
asm.Hlt();

asm.Label("double");
asm.Add(RegisterName.EAX, RegisterName.EAX); // EAX = EAX + EAX = EAX * 2
asm.Ret();                         // return; pops base-relative offset, jumps back

byte[] image = asm.Build();
```

`CALL` pushes `(IP - programBase)` which is the offset of the `OUT` instruction (12 bytes in). `RET` pops that offset and jumps to `programBase + 12`, resuming after the call.

### Example 3 — process spawning (fork/wait)

A parent forks a child, waits for it to finish, then reads the exit status.

```csharp
Assembler asm = new Assembler();

asm.Fork();                               // child PID in EAX (parent) or 0 (child)
asm.MovImm(RegisterName.EBX, 0);
asm.Cmp(RegisterName.EAX, RegisterName.EBX);
asm.Jz("child_path");                    // if EAX == 0, we are the child

// --- parent path ---
asm.Mov(RegisterName.ECX, RegisterName.EAX);  // ECX = child PID
asm.Wait(RegisterName.ECX);                    // block until child exits; status in EAX
asm.Out(RegisterName.EAX);                    // output child's exit status
asm.Hlt();

// --- child path ---
asm.Label("child_path");
asm.MovImm(RegisterName.EAX, 42);
asm.Exit(RegisterName.EAX);              // exit with status 42

byte[] image = asm.Build();
```

### Example 4 — OUT/IN sequence (interactive I/O)

Prompt for input and echo the value back.

```csharp
Assembler asm = new Assembler();

asm.MovImm(RegisterName.EAX, 63);  // '?' prompt character
asm.Out(RegisterName.EAX);         // write to stdout (traps to kernel)
asm.In(RegisterName.EBX);          // read from stdin (traps to kernel; blocks if empty)
asm.Out(RegisterName.EBX);         // echo back
asm.Hlt();

byte[] image = asm.Build();
```

When `OUT` executes in user mode, the shared syscall handler runs (in Kernel mode, on the process's kernel stack), delivers the value to the process's stdout device, and returns with `IRET`. Similarly for `IN` — if no input is buffered, the process is blocked until a `RaiseInputInterrupt` wakes it.

---

## Unsupported and planned instructions

### Not implemented (no opcode defined)

The following are absent from the ISA and would require new opcodes and handlers:

- Immediate-form shifts (`SHL dst, imm`) — count is always a register.
- Arithmetic right shift (signed SHR) — `SHR` is always logical (unsigned fill).
- 8/16-bit memory access — `LOAD`/`STORE` always transfer 32-bit words.
- Signed multiplication/division overflow detection — no carry or overflow flag.
- Conditional moves, string instructions, floating-point.

### Implemented: branch prediction (no new opcodes)

A 2-bit saturating-counter branch history table (BHT, 64 entries) wraps the existing `JZ`/`JNZ`/`JS`/`JNS` handlers via `Hardware.RecordBranch`. **No new opcodes.** The predictor scores only user-mode branches (CPU at User level, interrupts enabled); OS-routine branches are skipped. A misprediction adds `MispredictPenalty = 3` observational cycles to the hardware cycle counter. The cycle counter is independent of the MLFQ quantum, which remains instruction-count based. Prediction results are exposed through the `BranchPredicted` event; the predictor is accessible via `Hardware.GetBranchPredictor()`.

Source files: `CSharpOS/CPU/BranchPredictor.cs`, `OSTests/BranchPredictorTests.cs`.

### Implemented: demand paging (MMU page-fault trap, no new user opcode)

The C# `Hardware` class implements a software MMU that translates user-mode data addresses through per-process page tables stored in the OS region. Code addresses (instruction fetches) are never translated. The ISA-visible addition is a **restartable page-fault trap**: when `TryTranslateData` encounters a non-resident PTE, it rewinds the instruction pointer to the faulting instruction and dispatches `IvtPageFault = 15` (the 16th IVT slot) with the faulting page number in EAX. After the ISA handler makes the page resident, the faulting instruction re-runs transparently.

Key constants:
- `PageSize = 256` bytes (= `BuddyDefaultMinBlock`)
- `MaxPagesPerProcess = 128` (32 KiB of mappable virtual space per process)
- `IvtPageFault = 15`; `IvtSlotCount = 20`; `OsLayout.CodeBase = 80` (= `IvtSlotCount * 4`)
- PTE encodings: `>= 0` resident (physical frame base); `-1` unmapped (a user access is a protection fault, not a linear fallback); `-2` non-resident RAM-home; `<= -3` non-resident swap-backed (`SwapPte`); `<= -4096` copy-on-write share (`CowPte`)
- Disk: `DefaultDiskSlots = 64` image slots followed by `MaxProcesses * MaxPagesPerProcess = 1024` swap slots (total: 1088 slots) — the filesystem's file-block region (below) is a separate address space and does not consume slots.
- Kernel-mediated user-memory access: `IvtEnsureUserPage = 19` / `Hardware.UserToPhysical(va, isWrite)` synchronously faults in or COW-resolves one user page on demand, outside the normal instruction-level MMU path — used by the `FSYS` read/write wrapper (see [Filesystem](#filesystem)) to translate a user buffer one page-chunk at a time.

See `OS-Architecture.md`, section "Virtual memory and demand paging", for the complete design.

Source files: `CSharpOS/CPU/Hardware.cs` (MMU methods `TryTranslateData`, `SeedPageTableIfNew`, `RaisePageFault`), `BasicOSPlugin/OsRoutines.Paging.cs` (`EmitPageFault`, `EmitReleaseFrames`, `EmitFlushFrames`, `EmitZeroSwapSlots`, `EmitPairResolve`, `EmitResolveCow`, `EmitCowShare`), `OSTests/PagingTests.cs`.

### Implemented: ISA filesystem (new opcodes `FBREAD`/`FBWRITE`/`FSYS`; see [Disk](#disk) and [Filesystem](#filesystem))

A complete filesystem — write-back buffer cache, on-disk block allocator with free-chaining, a nested directory tree with path traversal, an open-file table, and byte-level read/write — built as ISA code, the same way the scheduler and buddy allocator are. Three new opcodes support it: `FBREAD`/`FBWRITE` (0x4B/0x4C, privileged raw block transfer to/from the disk's file-block region) and `FSYS` (0x4D, the user-mode syscall entry point: open/read/write/close/exec-by-path).

Three IVT slots dispatch the filesystem's ISA routines: `IvtCacheOp = 16` (buffer-cache control), `IvtFsOp = 17` (block/directory/path/file-core operations, used internally and by tests), `IvtFsSyscall = 18` (the `FSYS` wrapper). A fourth slot, `IvtEnsureUserPage = 19`, backs the FSYS read/write wrapper's page-chunked user-buffer translation (see [Key constants](#implemented-demand-paging-mmu-page-fault-trap-no-new-user-opcode) above). The filesystem is formatted automatically on first boot (guarded by the on-disk superblock magic, so a persisted, already-formatted disk is left alone), and `OperatingSystem.LoadProcess` now installs every program into the filesystem and runs it filesystem-backed (see `docs/OS-Architecture.md`, "Boot creation (Spawn)").

Source files: `CSharpOS/Disk/FsLayout.cs` (on-disk structure), `CSharpOS/Disk/FsImage.cs` (host-side helper for staging a file before running it), `BasicOSPlugin/OsRoutines.Cache.cs` (buffer cache), `BasicOSPlugin/OsRoutines.Fs.cs` (block allocator, directories, path resolution, `FSYS`, open/close/read/write/exec cores), `OSTests/CacheManagerTests.cs`, `OSTests/FsBlockAllocatorTests.cs`, `OSTests/FsDirectoryTests.cs`, `OSTests/FsPathTests.cs`, `OSTests/FsOpenCloseTests.cs`, `OSTests/FsReadWriteTests.cs`, `OSTests/FsSyscallTests.cs`, `OSTests/FsExecTests.cs`.

### Implemented: string and raw-key I/O (new opcodes `OUTS`/`INS`/`INK`/`INPOLL`; see [String and key I/O](#string-and-key-io))

Word-per-char string output/input and raw (non-line-buffered) keypress input, layered on the same `EnterKernel`/`IvtSyscall` trap path as `OUT`/`IN`. `INK` blocks on `WaitReason.KeyInput`, woken by a dedicated `IvtWakeKey` slot; `INPOLL` never blocks.

Source files: `CSharpOS/CPU/InstructionFunctions.cs` (`Outs`, `Ins`, `Ink`, `InkPoll`), `BasicOSPlugin/OsRoutines.cs` (`EmitSyscall` dispatch, `EmitWakeEntry(KeyInput)`), `OSTests/SyscallTests.cs`.

---

## Keeping this updated

When adding, removing, or modifying an instruction, edit **all five** of these:

| File | What to change |
|------|---------------|
| `CSharpOS/CPU/Instruction.cs` | Add/change the opcode constant; register the handler in the static constructor's `opcodeTable`. |
| `CSharpOS/CPU/InstructionFunctions.cs` | Implement the `static void` handler method. |
| `CSharpOS/Assembler/Assembler.cs` | Add the emit method(s). |
| `CSharpOS/CPU/Disassembler.cs` | Add the `case` in `Decode()`. |
| `docs/ISA.md` (this file) | Update the instruction tables and any affected sections. |

If the instruction has user-mode restrictions, also add or update a trap provider in `BasicOSPlugin/Traps/` and verify `BasicOS.CollectTraps()` discovers it.
