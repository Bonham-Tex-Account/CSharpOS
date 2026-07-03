#!/usr/bin/env pwsh
# Runs the OSTests suite (optionally filtered) and prints a COMPACT summary: build errors if the
# compile fails, then one line per failing test (name + first error-message line), then the final
# pass/fail/skip counts. Suppresses the normal multi-hundred-line dotnet build/restore/test spam so
# a test cycle costs a few lines instead of a screenful.
#
# Usage:  pwsh .claude/skills/run-tests/run-tests.ps1 [-Filter <substring>]
#   -Filter maps to `dotnet test --filter FullyQualifiedName~<substring>` (a class or method name).

param(
    [string]$Filter = ""
)

$ErrorActionPreference = "Continue"

$testArgs = @("test", "--nologo")
if ($Filter -ne "") {
    $testArgs += @("--filter", "FullyQualifiedName~$Filter")
    Write-Output "dotnet test --filter FullyQualifiedName~$Filter"
}
else {
    Write-Output "dotnet test (full suite)"
}

$raw = & dotnet @testArgs 2>&1 | Out-String
$exit = $LASTEXITCODE
$lines = $raw -split "`r?`n"

# 1) Compile errors short-circuit everything else.
$buildErrors = $lines | Where-Object { $_ -match 'error [A-Z]{2}\d+' } | ForEach-Object { $_.Trim() } | Select-Object -Unique
if ($buildErrors) {
    Write-Output "BUILD FAILED:"
    foreach ($e in $buildErrors) { Write-Output "  $e" }
    exit 1
}

# 2) One line per failing test: "  Failed <name> [<dur>]" followed a couple of lines later by
#    "  Error Message:" and then the message on the next line.
$anyFail = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*Failed\s+(\S+)\s+\[') {
        $anyFail = $true
        $name = $Matches[1]
        $msg = ""
        $hi = [Math]::Min($i + 6, $lines.Count - 1)
        for ($j = $i + 1; $j -le $hi; $j++) {
            if ($lines[$j] -match 'Error Message:') {
                if ($j + 1 -lt $lines.Count) { $msg = $lines[$j + 1].Trim() }
                break
            }
        }
        Write-Output "FAIL  $name"
        if ($msg -ne "") { Write-Output "      $msg" }
    }
}

# 3) Final count line ("Passed! - Failed: 0, Passed: 616, ..." or "Failed! - Failed: N, ...").
$summary = $lines | Where-Object { $_ -match '(Passed|Failed)!\s+-\s+Failed:' } | ForEach-Object { $_.Trim() }
Write-Output ""
if ($summary) {
    foreach ($s in $summary) { Write-Output $s }
}
elseif (-not $anyFail) {
    Write-Output "(no test summary found — check the filter matched any tests)"
}

exit $exit
