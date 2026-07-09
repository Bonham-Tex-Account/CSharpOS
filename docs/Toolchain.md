# In-OS Toolchain: write → compile → run

CSharpOS can author, assemble, and run a program **entirely inside the running OS**, on the emulated
CPU — no host trapdoor. The three steps are ordinary shell commands:

```
$ /bin/edit /prog.s          # 1. author assembly source into a file
$ /bin/as /prog.s /bin/prog  # 2. assemble it into a runnable image
$ /bin/prog                  # 3. run it
```

`/bin/edit` and `/bin/as` are themselves programs written in the machine's own instruction set (see
`CSharpOSConsole/Programs.cs`), so the whole toolchain is visible in the instruction stream and the
visualizer. Step 3 needs no new machinery — `FSYS` exec-by-path already runs any image file.

A ready-made sample is bundled so you can try step 2→3 without editing first:

```
$ /bin/as /hello.s /bin/hi
$ /bin/hi
72
```

---

## The assembly text format

Deliberately trivial to parse (the assembler is a small ISA program):

- **One instruction per line.** `MNEMONIC ARG ARG` with single-space separators.
- **Registers by name:** `EAX`, `EBX`, …, `R8` … `R15` (case-insensitive — the tokenizer upcases).
- **Immediates in decimal.** `MOV EAX 42`. Values 0–255 assemble to `MOV r, imm8`; larger values
  (up to 65535) to `MOV r, imm16` — the assembler picks the encoding automatically.
- **Labels:** `name:` on its own line (or at the start of a line). A branch/`CALL` target is a label.
- **Comments:** `;` to end of line. Blank lines are ignored.
- Source is stored **word-per-char** (one character per 32-bit word), the same convention `/bin/edit`
  writes and `OUTS`/`INS` use.

### Worked example — count 1..3 then halt

```asm
; count EAX up to EBX, printing each value
MOV EAX 0
MOV EBX 3
loop:
INC EAX
OUT EAX          ; prints 1, 2, 3
CMP EAX EBX
JNZ loop         ; branch back while EAX != EBX
HLT
```

Running the assembled image prints `1 2 3`.

### Instruction coverage

Every user-writable opcode is supported, across all operand shapes:

| Shape | Examples | Notes |
|-------|----------|-------|
| No operands | `HLT` `RET` `IRET` `FORK` `FSYS` `SIGRETURN` | |
| One register | `OUT EAX` `INC EBX` `EXIT EAX` `INK EAX` | |
| Two registers | `ADD EAX EBX` `LOAD EAX EBX` `CMP EAX EBX` `OUTS EAX EBX` | |
| Three registers | `DREAD EAX EBX ECX` `DWRITE EAX EBX ECX` | |
| `MOV` (overloaded) | `MOV EAX EBX` · `MOV EAX 42` · `MOV R8 300` | reg→reg / imm8 / imm16, chosen by operand 2 |
| Branch to a label | `JMP loop` `JZ done` `JNZ loop` `JS neg` `JNS pos` `CALL sub` | resolved to a program byte offset |

The assembled bytes are **identical** to what the host C# `Assembler` produces for the same program
(the test suite golden-compares them).

### What it rejects

A malformed source aborts the whole assembly: the assembler closes and **unlinks** the output file
(so no half-written `/bin/prog` is left behind) and exits with status 1. Rejected cases:

- an unknown mnemonic,
- a missing or non-register operand where a register is required,
- a branch to an **undefined** label.

---

## `/bin/edit` — author a source file

```
$ /bin/edit /prog.s
```

Reads lines from the keyboard (`INS`) and appends each (plus a newline) to the file. A line containing
only `.` ends input and closes the file. The file is created if absent. Because content is word-per-char,
the file is exactly what `/bin/as` expects to read.

## `/bin/as` — assemble

```
$ /bin/as <source-path> <output-path>
```

Reads the source at `argv[1]`, assembles it, and writes the runnable image to `argv[2]`. Exit status 0 on
success, 1 on a malformed source (with the output file removed). Point the output at `/bin/<name>` to make
it a command you can then run by path.

Internally it is a **two-pass** assembler: pass 1 scans the source and records every label's byte offset
into a table; pass 2 emits one packed 4-byte instruction word per line, resolving branch targets from that
table. It embeds the shared mnemonic and register tables (`CSharpOS/Assembler/AsmTable.cs`) and scans them
with an in-ISA string-compare.

---

## Scope and follow-ons (v1)

Intentionally minimal so the ISA parser stays small. Explicit follow-ons, not yet implemented:

- numeric (non-label) branch targets,
- hexadecimal or negative immediates (decimal only today),
- assembler directives (`.org`, data/string directives),
- multiple-space / tab tolerance between tokens,
- duplicate-label detection (v1 uses the first definition scanned).

The text grammar maps 1:1 onto the 4-byte opcode encoding, so it can grow later without disturbing the
run step. See `docs/ISA.md` for the instruction encodings and `docs/Visualizer-Guide.md` for a
walkthrough of running the toolchain in the console.
