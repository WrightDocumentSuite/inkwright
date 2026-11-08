# Fuzzing

The fuzz harness is a security regression tool for byte-oriented surfaces that accept untrusted
data. It is deliberately separate from the ordinary test suite: seed replay is deterministic and
runs on every pull request, while coverage-guided mutation is bounded and runs weekly or on demand.

## Targets and failure policy

`fuzz/Inkwright.Fuzz` exposes three targets:

- `reader` opens each input with recovery both enabled and disabled, then forces lazy page content,
  text and image paths under a per-document resource budget;
- `cff` parses a Compact Font Format program, reads its charset, subsets selected glyphs, and parses
  the rebuilt program;
- `jbig2` bounds the synthetic page dimensions before exercising segment and bitmap decoding.

Malformed and unsupported syntax is ordinary input, not a crash. The harness suppresses only the
library exceptions that explicitly report those outcomes. Arithmetic, indexing, allocation and
unexpected state exceptions remain visible to the fuzzer. A per-execution timeout also records
algorithmic hangs.

## One corpus, identical bytes

Binary seeds may be committed raw or as `*.base64`. Both replay and AFL++ first run
`.github/scripts/Prepare-FuzzCorpus.ps1`; it copies raw seeds and decodes encoded seeds into a fresh
directory. Consequently both modes feed exactly the same bytes to the target. Do not point AFL++
directly at the committed directory, because it would mutate the textual base64 representation.

Every target has a small accepted seed that reaches useful parser depth as well as malformed
regression material. Prefer minimal, redistributable inputs. A newly discovered crash belongs in
the corpus only after it is minimized and the underlying defect has a focused unit test.

## Deterministic replay

Build once and replay every committed seed:

```powershell
dotnet restore fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj --locked-mode
dotnet build fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj -c Release --no-restore
./.github/scripts/Replay-FuzzSeeds.ps1
```

To replay one already-materialized crash without hiding its exception:

```powershell
dotnet run --project fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj -c Release --no-build --no-restore -- reader --replay ./crash.pdf
```

Replace `reader` with `cff` or `jbig2` as appropriate.

## Coverage-guided campaign

The campaign follows the upstream [SharpFuzz AFL flow](https://github.com/Metalnem/sharpfuzz): it
publishes the harness, instruments non-framework library assemblies, and lets AFL++ mutate raw bytes
through standard input. Linux needs `afl-fuzz` from AFL++ and the pinned instrumenter:

```powershell
dotnet restore fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj --locked-mode
dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path artifacts/fuzz-tools
./.github/scripts/Run-FuzzCampaign.ps1 `
  -Target reader `
  -DurationSeconds 300 `
  -SharpFuzzCommand ./artifacts/fuzz-tools/sharpfuzz
```

The script refuses to reuse an existing output directory. Results are left under
`artifacts/fuzz/<target>/findings`; any crash or hang makes the run fail without deleting the input.
Because a managed target requires AFL++'s binary pre-check to be disabled, the wrapper validates
the resulting `fuzzer_stats`: executions and a non-zero coverage signal must be present, along with
at least one queue entry. A process that merely stayed alive without effective instrumentation
therefore fails the job.
The `Bounded fuzz campaign` workflow runs all three targets independently every Wednesday and can
also be started manually for 60, 300, or 900 seconds per target. Its findings are retained as CI
artifacts even when a target fails.

Fuzzing demonstrates exercised behavior, not parser safety by itself. Keep the resource limits,
corpus tests, targeted damage tests and dependency audit in the normal release gate.
