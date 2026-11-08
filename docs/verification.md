# Verification Baseline

This page records the point against which correctness and performance changes are compared. It is a
snapshot, not a claim that measurements from a different corpus or host are interchangeable.

The baseline was measured at commit `ebc5cbf` on 1 August 2026, on Windows Server 2022 with .NET
10.0.10 and SDK 10.0.302.

## Unit and Integration Tests

```powershell
dotnet test Inkwright.slnx -c Release --no-restore
```

| Metric | Baseline |
| --- | ---: |
| Discovered | 615 |
| Passed | 612 |
| Failed | 0 |
| Skipped | 3 |
| Wall time | 73.5 s |

The three skips are platform capabilities, not missing fixtures: this Windows host does not expose
SHA3-256 or SHAKE256, so the two SHA3 signing checks and the Ed448 RFC 8032 vector cannot run. The
corpus, system fonts, CFF/JBIG2 fixtures, integrity-protected files, and PDF/A reference files are
present.

### Post-audit result

The completed audit change set was measured on the same host, runtime, SDK, and corpus. The test
suite grew by 714 regression cases while retaining zero failures and the same three capability
skips:

| Metric | Baseline | Post-audit | Change |
| --- | ---: | ---: | ---: |
| Discovered | 615 | 1,329 | +714 |
| Passed | 612 | 1,326 | +714 |
| Failed | 0 | 0 | 0 |
| Skipped | 3 | 3 | 0 |
| Wall time | 73.5 s | 58.8 s | -14.7 s |

Wall time is recorded for diagnostics only: it is not a benchmark and may move with machine load.
All 515 layout/PDF/A tests, 671 core tests, 12 CJK tests, and 66 Forms tests passed. Signing passed 62
tests and retained its three documented platform-capability skips. The protected-reader regressions
include 46 Annex F structural-envelope cases and 11 effective/historical provenance cases; F.4
binary hint-table payloads remain opaque because this reader never uses them for range access.

JBIG2 corpus decoding now reaches 142 of 142 images when known corpus credentials are supplied (141
of 142 in the generic locked-document harness), versus 135 at baseline and 136 at the
retained-context milestone. The retained-context fixture and all three arithmetic/Huffman
refinement-aggregate fixtures match all 638,400 RGBA bytes of the reference image. The final two
unencrypted fixtures are valid page-information-only streams; their decoded blank-white RGBA images
are checked by exact dimensions and SHA-256 digests.

The focused CMS-padding regression first reproduced the intermittent failure after 3 successful
runs. After framing `/Contents` by its ASN.1 length instead of trimming zero bytes, the forced-zero
test passed and the formerly intermittent signing scenario passed 10 of 10 independent repeats.

### 0.1.4 hardening candidate

The complete local candidate was rechecked on 16 August 2026 on Windows Server 2022 with .NET
10.0.11 and SDK 10.0.400. Restore ran in locked mode with package validation enabled against the
published 0.1.3 baseline; the Release build completed with zero warnings and zero errors. The test
run required the content-addressed reference corpus described below rather than accepting skips.

| Project | Passed | Skipped | Failed |
| --- | ---: | ---: | ---: |
| Core | 743 | 0 | 0 |
| Layout | 531 | 0 | 0 |
| Forms | 78 | 0 | 0 |
| Signing | 82 | 3 | 0 |
| CJK | 12 | 0 | 0 |
| **Total** | **1,446** | **3** | **0** |

The three Signing skips remain the documented cryptographic platform capabilities. All corpus and
external JBIG2 fixture tests ran, including the fixed-metric assertions tied to the reference tree
SHA-256. All five committed fuzz seeds also replayed successfully.

## Continuous Integration

The public repository runs the ordinary suite on Windows and Linux, replays committed fuzz seeds,
runs a scheduled bounded fuzz campaign, and separately packs the five NuGet packages. All repository
restores use committed lock files in locked mode. Package CI validates the public API against the
latest published baseline, checks the nuspec dependency graph and repository commit, tests Source
Link for every symbol package, and compiles and runs an external Native AOT consumer from an isolated
NuGet cache. That consumer signs and verifies a PDF, so the smoke covers the packaged CMS
implementation rather than only loading the Signing assembly. These jobs upload prerelease artifacts
only and never publish to NuGet.

These paths are rooted at the public `inkwright` checkout. The project lives below the Git root in
the private monorepo, where `Inkwright/.github/workflows` is not a runnable workflow location; the
mirror must therefore carry `.github`, `.gitattributes`, the security policy, and lock files into the
public repository root before CI results are considered authoritative.

The private corpus job never runs for pull requests. It sets
`INKWRIGHT_REQUIRE_REFERENCE_CORPUS=1` and fails unless the mounted inventory has the published
content-addressed tree identity; matching only the file count and aggregate size is insufficient.
Partial inventories remain useful only for local developer runs. Its
self-hosted runner is a dedicated security
boundary: use a repository-scoped, current runner on an ephemeral or otherwise isolated host, keep
unrelated credentials and workloads off it, and mount the corpus read-only when the runner platform
allows that. A runner label only routes a job; it does not sandbox repository code. The job accepts
only the named PdfPig, iText, pdf.js, and XMP Toolkit corpus roots, requires all four, and does not
upload the source documents.

## Reference Corpus

The reference PDFs are intentionally not part of a public clone. `INKWRIGHT_CORPUS` may name the
common parent of the four allowlisted roots below, or one named root for a partial local run. When
the variable is absent, automatic discovery looks for those same known
`PdfPig-0.1.15`, `itext-dotnet-9.7.0`, `pdf.js-6.1.200`, and `XMP-Toolkit-SDK-main` directories beside
the checkout. Discovery never leaves that allowlist, even for an explicitly configured parent. If
no source exists, the real-file oracles report an actionable xUnit skip instead of failing an
otherwise clean public checkout.

The corpus inventory is the following exact four-root union:

| Root | PDFs | Bytes |
| --- | ---: | ---: |
| `PdfPig-0.1.15` | 216 | 226,509,093 |
| `itext-dotnet-9.7.0` | 6,613 | 561,243,936 |
| `pdf.js-6.1.200` | 966 | 125,282,013 |
| `XMP-Toolkit-SDK-main` | 12 | 5,181,211 |
| **Total** | **7,807** | **918,216,253** |

Its tree SHA-256 is
`9564a7a968078c1565b82a199952384f9d2dee83be93bf43735445860261824f`. The digest is
cross-platform and contains no source-document bytes or retained manifest. For every case-insensitive
`.pdf` match, form a logical path from the canonical root name and relative path, replace separators
with `/`, normalize it to Unicode NFC, and sort all logical paths with .NET ordinal ordering. Hash
each file with SHA-256. The tree-hash input is UTF-8 without a BOM: it starts with
`inkwright-reference-corpus-v1` plus LF, followed for every file by
`logical-path NUL invariant-decimal-length NUL lowercase-file-sha256 LF`. The workflow and test
harness implement this same algorithm; `.github/scripts/Get-CorpusIdentity.ps1` can be used for an
independent mount preflight.

The full read/rewrite check at the baseline produced:

| Metric | Baseline |
| --- | ---: |
| Files opened | 7,800 |
| Intentionally damaged files rejected | 5 |
| Empty files | 2 |
| Pages opened | 57,249 |
| Read failures | 0 |
| Round-trip failures | 0 |
| Page-count mismatches | 0 |
| Unhandled exceptions | 0 |

The first instrumented run against this same inventory retained those headline values and added the
following counters for future comparisons:

| Metric | Instrumented baseline |
| --- | ---: |
| Documents rewritten and reloaded | 7,555 |
| Pages parsed in the 400-file content sample | 694 |
| Content operations parsed | 386,535 |
| Damaged synthetic files recovered | 164 of 200 |
| Markdown candidates selected | 24 |
| Markdown documents exported | 21 |
| Marked-content references | 2,140 |
| Marked-content references resolved | 149 |
| Corpus/Markdown processing failures | 0 |

The post-audit rerun reproduced every instrumented counter above exactly. In particular, all 7,800
readable corpus files still opened, all 7,555 eligible files rewrote and reloaded, and all 57,249
pages remained visible. Read, round-trip, content-stream, and Markdown processing failures remained
zero. For the exact 7,807-file, 918,216,253-byte snapshot these published counts are executable
assertions, not only log output; a one-page metric change therefore fails the corpus test. Partial
or different inventories still exercise every file they discover, but do not inherit fixed metric
assertions from the published snapshot merely because their file count happens to match.

Corpus tests write successful counters through xUnit v3 output as lines beginning with
`INKWRIGHT_METRIC`. Use detailed console output to retain them:

```powershell
dotnet test tests/Inkwright.Tests/Inkwright.Tests.csproj -c Release --no-restore `
  --filter "FullyQualifiedName~CorpusRoundTripTests|FullyQualifiedName~MarkdownCorpusTests" `
  --logger "console;verbosity=detailed"
```

The round-trip line reports the inventory, opened and rewritten documents, pages, rejected and empty
files, I/O failures, and processing failures. The Markdown line reports selected candidates,
successful exports, and resolved versus referenced marked-content identifiers. These are raw,
reproducible counters rather than a subjective quality score.

When comparing results, first require the same tree SHA-256. A different corpus is a different
experiment, even when its file count and total byte count happen to match and all tests pass.

## NuGet Candidates

The local 0.1.4 hardening candidate produced exactly five `.nupkg` and five `.snupkg` files. Package
validation accepted the public API against 0.1.3, and structural inspection accepted the exact
dependency graph, metadata, README/NOTICE files, XML documentation, analyzer/CMap assets, repository
commit, and package-specific portable PDBs. An external `win-x64` Native AOT consumer restored all
five packages from an isolated cache and successfully created, signed, and verified a PDF
(`pages=1; sha256=True; signed=True`). Because this check ran in the private monorepo, it is not
Source Link release evidence: the packages must be rebuilt and `sourcelink test` must pass at the
corresponding public mirror commit before publication.

Version 0.1.3 produced exactly five packages. The SDK package validator accepted every one against
the published 0.1.2 baseline. ZIP inspection confirmed a non-empty package-specific description,
Apache-2.0 metadata, README, NOTICE, `net10.0` DLL/XML documentation, exact 0.1.3 project
dependencies, the Forms analyzer, and the Adobe CMap licence in the CJK package. A consumer with an
isolated NuGet cache installed all five exact versions, compiled a real `[PdfForm]` source-generator
constraint, and loaded every assembly as version 0.1.3.0.

Any monorepo-built package is only a smoke candidate: its nuspec records the private monorepo commit
while `RepositoryUrl` names the public project. A release artifact is rebuilt and rechecked from the
mirrored public commit after that source commit is published, as described in
[packaging.md](packaging.md). Package hashes and the isolated-consumer result belong to the release
handoff rather than this source snapshot, because producing them necessarily happens after the
public commit exists.

## Regression Policy

Correctness regressions have no tolerance: test, read, round-trip, page-count, determinism, and
unhandled-exception failures must remain zero. A conditional test may skip only for the capability or
fixture named in its reason; a new skip is investigated like a failure.

Performance methodology and the current measurements are in [benchmarks.md](benchmarks.md). Compare
BenchmarkDotNet results on the same host and runtime. Raise a warning when managed allocations grow
by more than 5%, or when the median time of three full runs grows by 10–15%. Do not gate on one
wall-clock run: timings move with host load, while allocation counts are substantially more stable.
