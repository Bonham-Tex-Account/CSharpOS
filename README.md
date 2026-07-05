# CSharpOS

A learning project that emulates a complete operating system in C#. The emulator includes a custom CPU with its own instruction set, a multi-level feedback queue (MLFQ) scheduler, a buddy memory allocator, demand-paged virtual memory with copy-on-write fork, an ISA filesystem (block allocator, buffer cache, directories, and file syscalls, all compiled to the CPU's own instruction set), a trap system, and a live Spectre.Console TUI dashboard with time-travel debugging.

## Projects

| Project | Purpose |
|---|---|
| `CSharpOS` | Core library — CPU, assembler, OS base classes, OS layout, plugin loader |
| `BasicOSPlugin` | Concrete OS personality — MLFQ scheduler, buddy allocator, demand paging, filesystem, I/O routines (all compiled to ISA at boot) |
| `CSharpOSConsole` | Console host — interactive menu, shared focused-process screen, Spectre.Console dashboard |
| `OSTests` | xUnit test suite |

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build

```
dotnet build
```

## Run

```
dotnet run --project CSharpOSConsole
```

The menu lets you pick one of 13 demo scenarios. After selecting a scenario you are also prompted for visualizer detail level (1 minimal / 2 normal / 3 high) and rendering performance level.

### Demo scenarios

| # | Description |
|---|---|
| 1 | Counter to ten — counts 1..10, prints each value, halts |
| 2 | Average of a list — builds `[10,20,30,40]` in memory, prints the average (25) |
| 3 | Guessing game — interactive; secret is 42, type guesses in the process window |
| 4 | Counter + average together (round-robin scheduling) |
| 5 | All three together |
| 6 | Memory churn — short jobs continuously load and exit; watch the buddy tree |
| 7 | Fill & drain — mixed-size jobs fill heap then drain; watch coalescing/reclaim |
| 8 | Scheduler + memory — counter + average run while short jobs churn the heap |
| 9 | Shell — interactive: type an absolute command (`/bin/ls /`), run it in the background with `&`, and use the built-in job control (`jobs`, `fg`, `bg`, `stop`, `kill <n>`); `Ctrl-C`/`Ctrl-Z` signal the foreground job. Includes `/bin/snake` (an arrow-key game) |
| 10 | Two guessing games — Tab switches focus between them (process switching) |
| 11 | Spawn tree — a parent forks two children; watch the Process tree panel |
| 12 | String I/O demo — type a name in the Screen panel, press Enter (`OUTS`/`INS` in action) |
| 13 | Filesystem demo — a process creates a file, writes/reads it via `FSYS`, prints it back |

There is one shared Screen panel showing the focused process's I/O; `Tab` switches focus between running processes. Typed input is sent as an int or a string depending on what the focused process is waiting on.

### Dashboard controls (during a run)

| Key | Action |
|---|---|
| `a` | Toggle auto-run (runs continuously) |
| `s` | Single-step one instruction |
| `←` / `→` | Scrub backward / forward through recorded history |
| `o` | Toggle program I/O panel |
| `Tab` | Switch the focused (foreground) process |
| `q` | Quit the current run |

### Custom OS plugin

The console host loads the OS personality from a `.dll` at runtime. The default is `BasicOSPlugin.dll` next to the executable. Pass a different path with:

```
dotnet run --project CSharpOSConsole -- --os-plugin /path/to/MyOsPlugin.dll
```

The plugin DLL must contain a non-abstract subclass of `OperatingSystem` with a `(TextWriter)` constructor. See `BasicOSPlugin/BasicOS.cs` for the reference implementation.

## Run tests

```
dotnet test
```

## Writing programs

Programs are assembled using `CSharpOS.Assembler` and passed to the emulator as byte arrays. Example — count to ten:

```csharp
Assembler asm = new Assembler();
asm.MovImm(RegisterName.EAX, 0);   // counter = 0
asm.MovImm(RegisterName.EBX, 10);  // limit = 10
asm.Label("loop");
asm.Inc(RegisterName.EAX);
asm.Out(RegisterName.EAX);         // print counter
asm.Cmp(RegisterName.EAX, RegisterName.EBX);
asm.Jnz("loop");
asm.Hlt();
byte[] image = asm.Build();
```

Pass the image to a `Process`, load it into an OS, and run it on a `Hardware` instance:

```csharp
OperatingSystem os = OsPluginLoader.Load("BasicOSPlugin.dll", Console.Out);
Hardware hw = new Hardware(memorySize: 32768, registers, os);
os.LoadProcess(new Process(image, requiredMemory: 128, requiredStackSize: 64));
while (os.HasProcesses) { hw.Run(); }
```

See `CSharpOSConsole/Programs.cs` for more examples including memory loads/stores and interactive I/O.

## Documentation

| Document | Description |
|---|---|
| [docs/ISA.md](docs/ISA.md) | Complete instruction set reference — all 48 opcodes, encoding, operand forms, register set, flags, privilege-level restrictions, worked examples, and planned-but-unimplemented instructions. |
| [docs/OS-Architecture.md](docs/OS-Architecture.md) | OS structure and function — memory layout with exact offsets, IVT dispatch model, MLFQ scheduler, buddy allocator, device table, Bin disk, demand paging/COW fork, the ISA filesystem (cache, block allocator, directories, file syscalls), process lifecycle (spawn/fork/exec/wait/exit), and the plugin loading mechanism. |
| [docs/Visualizer-Guide.md](docs/Visualizer-Guide.md) | How to use the console visualizer — launching, every menu option, reading each dashboard panel, key bindings, the interactive **shell** (argv, foreground/background, job-control builtins, Ctrl-C/Ctrl-Z) and **snake**, the disk view, and how the visualizer is implemented. |

## Architecture overview

### CPU

The `Hardware` class emulates a 32-bit CPU. The register file has 24 registers (EAX–EDX, ESI, EDI, ESP, EBP, EIP, EFLAGS, six segment registers, R8–R15). Instructions are fixed-width 4-byte words with one-byte opcodes.

**Privilege levels:** two hardware levels, like real hardware — **User** and **Kernel**. There is no third "privileged" level; atomicity of the OS scheduler/allocator/filesystem routines is the hardware **interrupt-enable flag**, not a level. The syscall handler runs in Kernel mode with interrupts enabled (preemptible); the OS's ISA routines (scheduler, allocator, filesystem, paging) run in Kernel mode with interrupts masked (atomic, run-to-`OSRET`). See `docs/ISA.md` for the full mode-transition rules.

### Instruction set

48 opcodes, grouped by category (full table with encodings in `docs/ISA.md`):

| Mnemonic | Opcode | Description |
|---|---|---|
| MOV reg, reg | 0x01 | Register-to-register move |
| MOV reg, imm8 | 0x02 | Load 8-bit immediate (zero-extended) |
| MOV reg, imm16 | 0x03 | Load 16-bit immediate (zero-extended) |
| LOAD reg, [reg] | 0x05 | Load word from memory |
| STORE [reg], reg | 0x06 | Store word to memory |
| ADD / SUB / MUL / DIV | 0x10–0x13 | Arithmetic (sets flags) |
| CMP | 0x14 | Compare (sets flags) |
| INC / DEC | 0x15–0x16 | Increment / decrement |
| AND / OR / XOR / NOT | 0x17–0x1A | Bitwise logic |
| SHL / SHR | 0x1B–0x1C | Logical shift (count in a register) |
| JMP / JZ / JNZ / CALL / RET / JS / JNS | 0x20–0x26 | Control flow |
| OUT / IN | 0x30–0x31 | User-mode I/O syscalls (trap to kernel) |
| HLT | 0x32 | Halt (terminate process) |
| IRET | 0x33 | Return from a kernel-mode syscall handler |
| FORK / EXEC / WAIT / EXIT / SETFOCUS | 0x34–0x38 | Process control |
| KILL / REAP | 0x39–0x3A | Job control: signal a process (TERM/KILL/STOP/CONT) / non-blocking reap (SIGACTION 0x3B reserved) |
| SAVEREGS / LOADREGS / SETLAYOUT / OSRET | 0x40–0x43 | OS-privileged context switch primitives |
| DREAD / DWRITE / DLEN | 0x44–0x46 | Disk slot transfers (Kernel-only) |
| OUTS / INS | 0x47–0x48 | String output / line input |
| INK / INPOLL | 0x49–0x4A | Raw keypress input (blocking / non-blocking) |
| FBREAD / FBWRITE | 0x4B–0x4C | Filesystem block transfers (Kernel-only) |
| FSYS | 0x4D | User-mode filesystem syscall (open/read/write/close/exec-by-path) |

### OS memory layout

The OS occupies a dedicated region below all process memory. At boot, `BasicOSPlugin` assembles its scheduler and allocator routines into this region as raw ISA bytecode — the scheduler itself runs on the same CPU.

```
[ IVT (interrupt vector table) ]
[ OS code — scheduler, allocator, trap handlers ]
[ OS data — process table, MLFQ state, buddy bitmap ]
```

The data section contains:
- Process count, current index, MLFQ boost timer
- Quantum thresholds (4 levels × 4 bytes)
- Process table (up to 8 entries × 192 bytes each, including an 8-entry file-descriptor table per process)
- Buddy allocator bitmap (256-bit compact bitset)
- Per-process page tables, a shared physical frame pool, and paging/swap/COW bookkeeping (see `docs/OS-Architecture.md`, "Virtual memory and demand paging")
- A write-back buffer cache, filesystem scratch space, and the open-file table backing the ISA filesystem (see the Filesystem section below)

### MLFQ scheduler

Four priority levels (0 = highest, 3 = lowest). New processes start at level 0.

- **Demotion:** when a process exhausts its quantum (1/2/4/∞ ticks per level), it moves down one level.
- **I/O boost:** when a blocked process is woken from I/O, it returns to level 0.
- **Periodic boost:** every 20 ticks, all non-terminated processes reset to level 0 (prevents starvation).

### Buddy allocator

Allocations are power-of-two blocks from a 256-bit compact bitmap. The tree is stored in the OS data region; the allocator and deallocator routines are ISA code that run in Kernel mode with interrupts masked (atomic — see Privilege levels above). The dashboard's buddy tree panel reconstructs the tree from the bitmap and the process table on every frame.

### Trap system

`BasicOSPlugin` registers one trap enforced before each user-mode instruction: **IRET** is forbidden in user mode. Memory protection is handled separately — the MMU is the sole mechanism (see below); there is no LOAD/STORE bounds trap.

New trap handlers implement `ITrapProvider` (in `CSharpOS`) and are discovered automatically by `BasicOS.CollectTraps()` via reflection.

### Memory protection

User-mode `LOAD`/`STORE` (and the `CALL`/`RET` stack) translate through the running process's per-process page table (the MMU). An access to a page outside the process's mapped extent is a **protection fault**: the process is terminated (exit status −1) via the same teardown path as an invalid instruction. There is no linear address fallback. Kernel-mode code addresses memory absolutely and is not translated. Each process can map up to `MaxPagesPerProcess = 128` pages (32 KiB) of virtual space.

### Filesystem

CSharpOS has a complete filesystem, and — like the scheduler and allocator — it is implemented as ISA code that runs on the emulated CPU, not as C# called from outside. The disk (`CSharpOS/Disk/Bin.cs`) has a second, block-addressed region separate from the process-image slots; the filesystem lives there:

- **Buffer cache** — a write-back cache in the OS memory region (`cache_*` ISA routines) with LRU eviction, dirty tracking, pinning, and periodic flush. All filesystem block I/O goes through it.
- **Block allocator** — a superblock + free bitmap + per-block free-chaining, so a file's blocks can be scattered and linked (`fs_format`/`fs_alloc_block`/`fs_free_block`/`fs_chain_*`).
- **Directories** — nested directories with word-per-char names and full path traversal (`/a/b/c`), so files are addressed by absolute path.
- **File syscalls** — a single user-mode instruction, `FSYS`, gives processes open/read/write/close, unlink, mkdir, readdir, plus exec-by-path (replace the running process's image with a program stored as a file in the filesystem rather than a disk image slot).
- **Boot auto-format** — the filesystem is formatted automatically the first time the machine boots on a fresh disk; a `.bin` disk image that was already formatted (and persisted via `Bin.Save`/`Bin.Load`) is left alone.

`OperatingSystem.LoadProcess` now installs every program into the filesystem (under `/bin/p<seq>`) and runs it filesystem-backed rather than from a disk image slot — `BasicOS` opts into this via the `UsesFilesystemBoot` hook. `FSYS` exec-by-path and demand-paged FSYS read/write buffers both go through the same page-table-mediated memory access as ordinary `LOAD`/`STORE`, so a file buffer that lives in swapped-out process memory round-trips correctly.

See `docs/OS-Architecture.md`, section "The ISA filesystem", for the full design, and `docs/ISA.md` for the `FSYS`/`FBREAD`/`FBWRITE` instruction reference.
