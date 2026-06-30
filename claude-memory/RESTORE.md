# Claude Memory — Restore Instructions

On a new machine, copy these files into the Claude Code memory directory after cloning:

```powershell
$encoded = "C--Users-Tex-OneDrive-Documents-VisualStudio2026-CSharpOS"
$dest = "$env:USERPROFILE\.claude\projects\$encoded\memory"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "claude-memory\*" $dest -Force
```

Also restore the global workflow preferences from the claude-config repo:
```powershell
Copy-Item "CLAUDE.md" "$env:USERPROFILE\.claude\CLAUDE.md"
```
