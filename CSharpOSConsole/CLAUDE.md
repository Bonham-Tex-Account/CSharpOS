# CSharpOSConsole Quick Reference

The console host: a Spectre.Console TUI that boots the OS plugin and runs demo/interactive programs.
`Visualization/` has its own CLAUDE.md (dashboard, model, bridge, key bindings) — this file covers the
top-level host + the program library.

## Files

| File | Role |
|------|------|
| Program.cs | Entry point: the mode menu (`while(true)` loop), per-mode `Run*` launchers, `StagedProgram`. `MemorySize = OsLayout.TotalSize + 32768` (tracks the OS region so it can't go stale) |
| Programs.cs | The program library — one method per assembled program image (see the jump table below). 1067 lines; every program has a `// ===== Name =====` marker → **Grep the marker, don't read the file** |
| ConsoleVisualizer.cs | Test coordinator: `ConsoleVisualizer(hw, os, writer)` wrapping model + bridge + PlainTextRenderer |
| VisualizerMode.cs | `VisualizerMode` enum (per-process window modes 1–5, churn 6–8, shell 9) + `DetailLevel` |

## Programs.cs — jump table (Grep `// ===== <Name>`)

Every program is `public static byte[] <Name>()` returning an assembled image. **To read one, Grep its
marker and `Read(offset=…, limit=…)`** — never read the whole file.

| Marker | Group | What it does |
|--------|-------|--------------|
| `CounterToTen` | simple demo | prints 1..10, halts |
| `AverageOfList` | simple demo | builds [10,20,30,40] in memory, prints the average (25) |
| `BusyThenHalt` | churn | `(iterations, printValue)` — spin then print+halt; used by memory-churn modes |
| `GuessingGame` | interactive | INS/OUT number-guess (secret 42) |
| `StringsDemo` | interactive | OUTS/INS string I/O; returns `(image, requiredMemory)` |
| `SpawnChildren` | demo | parent FORKs 3 children → process-tree demo |
| `FilesystemDemo` | FS demo | creates a file, FSYS write/read, prints "HI!" from disk |
| `Ls` / `Cat` / `Rm` / `Mkdir` / `Echo` / `Help` | **/bin programs** | the shell's command programs (argv ABI: EAX=argc, EBX=argv). Installed into `/bin` by `RunShell` |
| `Edit` | **/bin program (§4.0)** | `edit <file>` — a line editor: INS lines → append each + `\n` to the file (FSYS write), a lone `.` ends input. The source-authoring brick for the write→compile→run toolchain; content is word-per-char (what a future `/bin/as` reads). Installed by `RunShell` |
| **`Shell`** | shell (§2 + §2.5) | the interactive shell. ~340 lines. prompt→INS→`&`-detect→FORK→exec / builtins. Builtins: `jobs`/`kill`/`stop`/`bg`/`fg` (job control). Grep internal ISA labels: `drain`, `amp`, `parent`, `do_jobs`/`do_kill`/`do_stop`/`do_bg`/`do_fg`, helpers `cmd_is`/`parse_uint`/`job_lookup`/`job_clear`. DATA offsets: strings @1024+, LineBuf @1408, JobsBase @1664 |
| **`Snake`** | game (§3) | the snake game. ~240 lines. W=8×H=8 grid, life-countdown body, INPOLL arrows, LCG food, whole-grid OUTS/frame. Grep internal labels: `main`, `move`, `nogrow`, `dead`, `place_food`, `render`, `border`. DATA: state @2048, GRID @2112, REND @2624. Local C# helpers `Ld`/`StR`/`StI`/`EmitR11` |

## Program.cs — mode menu

`1`–`5` per-process window modes (counter/average/guess/combos), `6`–`8` memory churn (`BusyThenHalt`
via `ScheduleStaggeredLoads`), `9` = **Shell** (`RunShell`), `10`–`13` (two-guess/spawn-tree/strings/
FS demo). Each mode calls `PromptMode()` + `PromptDetail()` then a `Run*` launcher.

**`RunShell`** (mode 9): `FsImage.EnsureDir("/bin")` + `WriteFile` the /bin programs (ls/cat/rm/mkdir/
echo/help/counter/average/guess) **and `/bin/snake`** + a `/note` file, then loads `Programs.Shell()`
with **memory 4096** (so an exec'd program — esp. snake's grid+render buffer — has DATA room; exec
preserves `RequiredMemory`). Type absolute commands (`/bin/ls /`, `/bin/snake`, `/bin/echo hi &`).

## Conventions
- Program images are staged onto `hw.Disk` (slot ≤ `DefaultDiskSlotSize`=2048) then referenced by a
  `Process`; FS-backed loading installs them to `/bin/p<seq>` (see OS/CLAUDE.md `LoadProcess`).
- ISA-heavy programs (Shell, Snake) follow the same clobber/8-bit-immediate rules as OS ISA — see
  `BasicOSPlugin/CLAUDE.md` "ISA Authoring & Debugging" before editing them.
