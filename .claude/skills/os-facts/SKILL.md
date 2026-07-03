---
name: os-facts
description: Print ground-truth CSharpOS constants (OsLayout memory-map offsets and TotalSize, IVT slot numbers, ProcessEntry field offsets, paging/disk/kernel-stack constants, FsLayout on-disk offsets, and the Cache/FsOp/FSYS op-selector numbers) computed straight from the compiled source. Use this whenever you need an exact offset, IVT slot, struct-field offset, size, or op-selector value — instead of reading OsLayout.cs / Hardware.cs / FsLayout.cs and doing the arithmetic, or trusting a CLAUDE.md table that may have drifted. Especially before editing OsLayout/IVT/ProcessEntry layout or writing a test that hardcodes an address.
---

# os-facts

Reflects over the compiled `CSharpOS` library and prints its `public const int` values, grouped and sorted, so layout numbers come from the source of truth rather than a hand-maintained table.

## Run

From the repo root:

```
dotnet run --project .claude/skills/os-facts/dump -- <section>
```

`<section>` (optional, default `all`):

| section | prints |
|---------|--------|
| `layout` | `OsLayout` — every constant, sorted by value = a memory map from the IVT up to `TotalSize`. Values are **absolute** OS-image offsets (they already include `DataBase`). |
| `ivt` | `Hardware` IVT slot numbers (`Ivt*`), plus `IvtSlotCount` / `IvtSize`. |
| `entry` | `Hardware` process-table field offsets (`ProcessEntry*`) and `ProcessEntrySize`. |
| `paging` | all other `Hardware` constants: paging, disk geometry, kernel-stack, device ids, key codes, and the `CacheOp*` / `FsOp*` / `Fsys*` op-selector numbers. |
| `fs` | `FsLayout` on-disk structure (block map, superblock fields, directory-entry offsets). |
| `all` | every section. |

The command builds the helper (and `CSharpOS` if stale) on demand — a few seconds cold, instant when up to date. It only reads compile-time constants; it runs no OS/emulator code.

## When to use

- You need a specific number — `TotalSize`, a scratch-region base, an IVT slot, a `ProcessEntry*` offset, an `FsOp*`/`Fsys*` selector — for an edit or a test assertion.
- You are about to change `OsLayout.cs` / the IVT / the process entry, and want the current values before/after.
- A CLAUDE.md table looks suspect or you want to confirm it hasn't drifted (the root `CLAUDE.md` tables are hand-maintained; this is the authoritative cross-check).

Prefer this over opening the source files just to look a number up. If a CLAUDE.md table disagrees with this output, **this output wins** — fix the table.

## Adding constants

Nothing to update here — the dumper auto-discovers every `public const int` on `OsLayout`, `Hardware`, and `FsLayout` by reflection. New constants appear automatically. To cover a new type, add a `Dump(...)` call in `dump/Program.cs`.
