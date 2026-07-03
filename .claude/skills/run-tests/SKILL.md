---
name: run-tests
description: Run the CSharpOS xUnit suite (all of OSTests, or a filtered subset) and get back only a compact summary — build errors if the compile fails, one line per failing test (name + first error-message line), then the final Passed/Failed/Skipped counts — instead of the full multi-hundred-line dotnet build+test output. Use this for any "run the tests" / "run test X" / verify-after-a-change step in this repo; prefer it over calling `dotnet test` directly so a test cycle costs a few lines of context, not a screenful.
---

# run-tests

Wraps `dotnet test` and prints only what matters, so a test cycle doesn't burn context on restore/build spam.

## Run

From the repo root:

```
pwsh .claude/skills/run-tests/run-tests.ps1 [-Filter <substring>]
```

- No `-Filter` → the full suite.
- `-Filter <substring>` → `dotnet test --filter FullyQualifiedName~<substring>`, where `<substring>` is a test class (`FsBootTests`) or a fully-qualified method fragment (`Fsys_WriteThenRead`).

## Output

- **Build failure:** `BUILD FAILED:` followed by the unique `error CSxxxx` lines. (Exits 1.)
- **Test failures:** `FAIL  <fully.qualified.TestName>` and, indented under it, the first line of that test's error message — one stanza per failing test.
- **Always:** a blank line then the final count line, e.g. `Passed! - Failed: 0, Passed: 616, Skipped: 0, ...`.

The script's exit code mirrors `dotnet test` (0 = all passed).

## When to use

- Any time you'd otherwise run `dotnet test` in this repo — after an edit, to verify a fix, or to check the whole suite is green.
- Filter to the relevant class/method while iterating (fast + focused); run with no filter for the final green check before handing work back.

## Notes

- If a failing test's message isn't self-explanatory, re-run that one test directly (`dotnet test --filter FullyQualifiedName~<method>`) for the full error + stack trace — this skill deliberately trims to the first message line.
- Debugging an infinite loop / hang? A hung test won't reach the summary; run the single test directly so you can see it stall.
