# CSharpOS

A learning project that emulates a complete operating system in C#. The emulator includes a custom CPU with its own instruction set, a multi-level feedback queue (MLFQ) scheduler, a buddy memory allocator, a trap system, and a live Spectre.Console TUI dashboard with time-travel debugging.

## Projects

| Project | Purpose |
|---|---|
| `CSharpOS` | Core library — CPU, assembler, OS base classes, OS layout, plugin loader |
| `BasicOSPlugin` | Concrete OS personality — MLFQ scheduler, buddy allocator, I/O routines (all compiled to ISA at boot) |
| `CSharpOSConsole` | Console host — interactive menu, per-process terminal windows, Spectre.Console dashboard |
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

The menu lets you pick one of 8 demo scenarios. After selecting a scenario you are also prompted for visualizer detail level (1 minimal / 2 normal / 3 high) and rendering performance level.

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

Modes 1–5 open a separate terminal window per process for I/O. Modes 6–8 mirror program output inside the dashboard panel instead.

### Dashboard controls (during a run)

| Key | Action |
|---|---|
| `a` | Toggle auto-run (runs continuously) |
| `s` | Single-step one instruction |
| `←` / `→` | Scrub backward / forward through recorded history |
| `o` | Toggle program I/O panel |
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

## Architecture overview

### CPU

The `Hardware` class emulates a 32-bit CPU. The register file has 24 registers (EAX–EDX, ESI, EDI, ESP, EBP, EIP, EFLAGS, six segment registers, R8–R15). Instructions are fixed-width 4-byte words with one-byte opcodes.

**Privilege levels:**
- **User** — preemptible; traps are evaluated before each instruction.
- **Kernel** — preemptible; used when a process is executing a kernel-mode handler.
- **Privileged** — atomic run-to-OSRET; used for OS scheduler/allocator routines in the OS memory region.

### Instruction set

| Mnemonic | Opcode | Description |
|---|---|---|
| MOV reg, reg | 0x01 | Register-to-register move |
| MOV reg, imm32 | 0x02 | Load 32-bit immediate |
| MOV reg, imm16 | 0x03 | Load 16-bit immediate (zero-extended) |
| LOAD reg, [reg] | 0x05 | Load word from memory |
| STORE [reg], reg | 0x06 | Store word to memory |
| ADD | 0x10 | Add |
| SUB | 0x11 | Subtract |
| MUL | 0x12 | Multiply |
| DIV | 0x13 | Divide |
| CMP | 0x14 | Compare (sets flags) |
| INC | 0x15 | Increment |
| DEC | 0x16 | Decrement |
| JMP | 0x20 | Unconditional jump |
| JZ | 0x21 | Jump if zero |
| JNZ | 0x22 | Jump if not zero |
| CALL | 0x23 | Call subroutine |
| RET | 0x24 | Return |
| JS | 0x25 | Jump if sign (negative) |
| JNS | 0x26 | Jump if not sign |
| OUT reg | 0x30 | Output register value |
| IN reg | 0x31 | Read input into register |
| HLT | 0x32 | Halt (terminate process) |
| IRET | 0x33 | Return from interrupt |
| SAVEREGS | 0x40 | Save register file to process table |
| LOADREGS | 0x41 | Load register file from process table |
| SETLAYOUT | 0x42 | Set per-process memory layout |
| OSRET | 0x43 | Return from OS privileged routine |

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
- Process table (up to 8 entries × 160 bytes each)
- Buddy allocator bitmap (256-bit compact bitset)

### MLFQ scheduler

Four priority levels (0 = highest, 3 = lowest). New processes start at level 0.

- **Demotion:** when a process exhausts its quantum (1/2/4/∞ ticks per level), it moves down one level.
- **I/O boost:** when a blocked process is woken from I/O, it returns to level 0.
- **Periodic boost:** every 20 ticks, all non-terminated processes reset to level 0 (prevents starvation).

### Buddy allocator

Allocations are power-of-two blocks from a 256-bit compact bitmap. The tree is stored in the OS data region; the allocator and deallocator routines are ISA code that run in Privileged mode. The dashboard's buddy tree panel reconstructs the tree from the bitmap and the process table on every frame.

### Trap system

`BasicOSPlugin` registers three traps enforced before each user-mode instruction:
- **IRET** is forbidden in user mode.
- **LOAD/STORE** from outside the process's own memory range raises a fault.

New trap handlers implement `ITrapProvider` (in `CSharpOS`) and are discovered automatically by `BasicOS.CollectTraps()` via reflection.
