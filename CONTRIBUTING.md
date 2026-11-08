# Contributing

Contributions are welcome. Keep changes focused, add regression coverage for behavioural changes,
and update the existing document in `docs/` that explains the affected feature.

## Build and test

Install the .NET SDK selected by `global.json`, then run from the public repository root:

```powershell
dotnet restore Inkwright.slnx --locked-mode
dotnet build Inkwright.slnx -c Release --no-restore
dotnet test Inkwright.slnx -c Release --no-build --no-restore
```

The build treats warnings as errors. Dependency changes are intentional repository changes: run an
unlocked restore once, inspect every changed `packages.lock.json`, and then repeat the locked restore.
Do not hand-edit lock files.

Before opening a pull request, also run:

```powershell
./.github/scripts/Test-PackageHealth.ps1 -Solution Inkwright.slnx
./.github/scripts/Replay-FuzzSeeds.ps1
git diff --check
```

Package work has additional API, content, Source Link, and Native AOT checks documented in
[`docs/packaging.md`](docs/packaging.md). Corpus and performance claims require the exact fixtures,
machine details, and commands recorded in their documentation; do not update a published number from
an incomparable run.

## Tests and compatibility

- Put a focused regression test beside the component that owns the behaviour.
- Preserve tolerant reading for malformed-but-recoverable PDFs unless strict mode or a configured
  resource limit explicitly requires rejection.
- Keep public API additions compatible with trimming and Native AOT.
- Do not add a native or copyleft dependency without discussing the packaging impact first.
- Never commit private PDFs, credentials, certificates with private keys, or vulnerability details.

Report security issues through the private process in [`SECURITY.md`](SECURITY.md), not through a
public issue or pull request.
