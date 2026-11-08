# Building NuGet Packages

The solution ships five packages: `Inkwright`, `Inkwright.Cjk`, `Inkwright.Forms`, `Inkwright.Layout`, and `Inkwright.Signing`. Test projects, examples, benchmarks, and the standalone forms generator build are not packaged. The generator is included in `Inkwright.Forms` as an analyzer asset.

Shared metadata lives in `Directory.Build.props`: version, authors, the Apache-2.0 licence, copyright, README, NOTICE, project URL, and repository URL. Each package's description and search tags live in its `.csproj` because the add-ons serve different purposes. NuGet shows the `Description` element from the nuspec on the package card and `README.md` as the detailed description below it.

## Build

The release package must be built from a clean checkout of
`https://github.com/WrightDocumentSuite/inkwright`, after the audited monorepo subtree has been
mirrored and committed there. `RepositoryUrl` names that public repository, and NuGet records the
current Git commit in every nuspec. Packing directly from the private monorepo would therefore
publish a commit hash that cannot be resolved in the repository named by the package.

The mirror must preserve top-level dotfiles and directories, especially `.github`,
`.gitattributes`, `SECURITY.md`, and every project-level `packages.lock.json`. GitHub only discovers
`.github/workflows` at the public repository root; the same directory is intentionally inert while
it is nested inside the private monorepo. After mirroring, compare the public tree with the subtree
and check `git status` before treating its CI result as release evidence.

From the clean public `inkwright` root:

```bash
VERSION=0.1.4
BASELINE_VERSION=0.1.3
PACKAGE_DIR="artifacts/packages/$VERSION"
PUBLIC_SHA=$(git rev-parse HEAD)
export NUGET_PACKAGES="$PWD/artifacts/nuget-cache/$VERSION"
dotnet restore Inkwright.slnx --locked-mode \
  -p:Version="$VERSION" \
  -p:EnablePackageValidation=true \
  -p:PackageValidationBaselineVersion="$BASELINE_VERSION"
dotnet test Inkwright.slnx -c Release --no-restore -p:Version="$VERSION"
dotnet pack Inkwright.slnx -c Release --no-restore \
  -p:Version="$VERSION" \
  -p:ContinuousIntegrationBuild=true \
  -p:EnablePackageValidation=true \
  -p:PackageValidationBaselineVersion="$BASELINE_VERSION" \
  --output "$PACKAGE_DIR"
```

The validation properties must also be present during restore: they add the five 0.1.3 packages as
`PackageDownload` baselines. Passing them only to `dotnet pack --no-restore` leaves no baseline
`.nupkg` for the API compatibility task.

`Directory.Build.props` creates one `packages.lock.json` per project. Release and CI restores use
`--locked-mode`, so a package graph or content-hash change fails instead of silently rewriting those
files. When a dependency is intentionally changed, run an ordinary restore locally, review every
lock-file diff, commit it with the dependency change, and then repeat the locked restore.

The `Version` parameter is intentionally set on the release command: the same commit can be built as prerelease (`0.2.0-preview.1`) or stable without editing the project. If the parameter is omitted, the value from `Directory.Build.props` is used.

Versions `0.1.0` through `0.1.3` have already been published and cannot be replaced. The examples
use `0.1.4`; query the NuGet flat-container index immediately before a later release and raise
`VERSION` when needed rather than relying on `--skip-duplicate`, which would silently leave the old
package on nuget.org.

The result is exactly five `.nupkg` files in the version-specific directory; this prevents an old
package from being caught by a later wildcard. Before publishing, open the packages as ZIP and
check `.nuspec`, `README.md`, `NOTICE`, `lib/net10.0`, dependencies, and for `Inkwright.Forms`,
`analyzers/dotnet/cs/Inkwright.Forms.Generator.dll`; `Inkwright.Cjk` must also contain
`LICENSE-ADOBE-CMAP.txt`. Every nuspec must contain the public repository URL and
`repository.commit == $PUBLIC_SHA`.

Run the repository gates against that exact directory rather than inspecting an older wildcard:

```powershell
$VERSION = '0.1.4'
$PACKAGE_DIR = "artifacts/packages/$VERSION"
$PUBLIC_SHA = (git rev-parse HEAD).Trim()
./.github/scripts/Test-Packages.ps1 `
  -PackageDirectory $PACKAGE_DIR `
  -Version $VERSION `
  -RepositoryCommit $PUBLIC_SHA
./.github/scripts/Run-PackageAotSmoke.ps1 `
  -PackageDirectory $PACKAGE_DIR `
  -PackageVersion $VERSION `
  -RuntimeIdentifier win-x64 `
  -WorkingDirectory (Join-Path $env:TEMP "inkwright-aot-smoke-$VERSION")
```

The first command verifies metadata, exact dependency versions, runtime assemblies and XML docs,
the Forms analyzer, the CJK licence, and the matching PDB in every symbol package. The second builds
an external Native AOT consumer from an isolated cache, then signs and verifies a PDF through the
packed Signing assembly. Its working directory must stay outside the repository so central package
management and other repository build settings cannot make the consumer test pass accidentally.
Public CI extracts each shipped portable PDB and runs `sourcelink test` from the mirrored commit.

## Verify Installation Locally

A new empty project lets you verify not only the nuspec but real dependency resolution. Create it **outside the repository tree**: otherwise the project's central package management will intercept `dotnet add package` and the command will try to modify the working `Directory.Packages.props`.

### Use an isolated package cache

This is not a precaution but a required step; without it verification is meaningless. NuGet versions
are immutable **by identity**: once `Inkwright/$VERSION` is in the global packages folder, restore
takes it from there and no longer looks at the local feed. A rebuilt package with the same number
can sit untouched while the smoke test verifies yesterday's bytes.

```bash
VERSION=0.1.4
PUBLIC_ROOT="/absolute/path/to/public/inkwright"
PACKAGE_DIR="$PUBLIC_ROOT/artifacts/packages/$VERSION"
SMOKE_ROOT=$(mktemp -d)
export NUGET_PACKAGES="$SMOKE_ROOT/packages"
```

The temporary cache avoids deleting any of the developer's existing packages. Remove `SMOKE_ROOT`
after verification.

You can confirm the fresh build was picked up by file timestamp:

```bash
ls -l "$NUGET_PACKAGES/inkwright/$VERSION/lib/net10.0/Inkwright.dll"
```

It should match the time of `dotnet pack`, not be earlier.

```bash
cd "$SMOKE_ROOT"
dotnet new console -n PackageSmoke -f net10.0
dotnet new nugetconfig -o PackageSmoke --force
dotnet nuget add source "$PACKAGE_DIR" \
  --name inkwright-local \
  --configfile PackageSmoke/nuget.config
cd PackageSmoke
for id in Inkwright Inkwright.Cjk Inkwright.Forms Inkwright.Layout Inkwright.Signing; do
  dotnet add package "$id" --version "$VERSION"
done
dotnet build -c Release --no-restore
```

When using Windows `dotnet`, pass a native absolute path such as `E:\...`; a WSL path such as
`/mnt/e/...` is not a valid source for that process. The created config keeps nuget.org for external
dependencies and adds the local source. Temporary `PackageSmoke` is not part of the repository.

### Verify behavior, not just compilation

An empty `Main` that builds only proves the nuspec is not broken. More useful is a short program that exercises through the built binaries what changed in this release: it also checks the **public API surface**. Internal types like `PdfNames` are visible to tests via `InternalsVisibleTo`, but not to package consumers — code in documentation examples that accidentally relied on such a type compiles for you and not for the user.

## Publish

Publishing is deliberately separate from packing. Query the five flat-container indexes again just
before the first push; none may already list the version. Push dependencies in order and wait until
each prerequisite is indexed before pushing its dependants:

```bash
VERSION=0.1.4
PACKAGE_DIR="artifacts/packages/$VERSION"
SOURCE=https://api.nuget.org/v3/index.json

dotnet nuget push "$PACKAGE_DIR/Inkwright.$VERSION.nupkg" \
  --api-key "$NUGET_API_KEY" --source "$SOURCE"
# Wait until Inkwright/$VERSION appears in the flat-container index.
dotnet nuget push "$PACKAGE_DIR/Inkwright.Cjk.$VERSION.nupkg" \
  --api-key "$NUGET_API_KEY" --source "$SOURCE"
dotnet nuget push "$PACKAGE_DIR/Inkwright.Forms.$VERSION.nupkg" \
  --api-key "$NUGET_API_KEY" --source "$SOURCE"
dotnet nuget push "$PACKAGE_DIR/Inkwright.Layout.$VERSION.nupkg" \
  --api-key "$NUGET_API_KEY" --source "$SOURCE"
# Wait until Inkwright.Forms/$VERSION appears, then publish Signing.
dotnet nuget push "$PACKAGE_DIR/Inkwright.Signing.$VERSION.nupkg" \
  --api-key "$NUGET_API_KEY" \
  --source "$SOURCE"
```

Do not use a wildcard or `--skip-duplicate` for a release: either can hide that the wrong artifact or
an already occupied version was selected. NuGet versions are immutable, and the API secret must not
be stored in the repository or passed as an MSBuild property that ends up in logs.

The `Packages` workflow deliberately creates a unique `-ci.<run>.<attempt>` prerelease, has only
read access to repository contents, and uploads validation artifacts; it has no NuGet credential and
contains no push step. Keep publishing separate from pull-request and branch CI. Never rename a CI
artifact to a stable version or publish it as though it came from the clean release procedure above.

## Release handoff

Before the public release commit is built, move the relevant entries from `Unreleased` in
`CHANGELOG.md` to `## $VERSION — YYYY-MM-DD`, leave a fresh empty `Unreleased` section, and review
the diff. After all five package pushes succeed:

1. Verify all five exact versions are indexed on nuget.org, their dependency links resolve, and
   each package page reports its matching symbol package.
2. Reinstall all five packages into a clean-cache consumer from nuget.org and repeat the behavioral
   smoke; a local feed is no longer sufficient evidence at this point.
3. Create an annotated `v$VERSION` tag at exactly `$PUBLIC_SHA`, verify it with
   `git rev-parse "v$VERSION^{commit}"`, and push that tag. Do not move or reuse a release
   tag.
4. Create the GitHub Release from that tag and attach a SHA-256 manifest for the five `.nupkg` and
   five `.snupkg` files. Record any indexing delay or exceptional manual step in the release notes.

If package publication stops part-way, do not create the tag or GitHub Release. Resolve the partial
NuGet state explicitly—published versions are immutable—then choose a new patch version for rebuilt
artifacts.

Packing also produces one `.snupkg` beside every ordinary package. Source Link maps its portable PDB
to the exact public repository commit recorded in the package metadata, so publish each symbol
package together with its matching `.nupkg`. The release check must inspect the repository URL and
commit in both the package and PDB before either artifact is pushed. CI additionally requires the
exact runtime PDB in each symbol package and validates the canonical Source Link mapping and public source checksums in the PDB extracted from each `.snupkg`. The tool does not accept a `.snupkg` path directly (exit 4); use `.github/scripts/Test-SourceLink.ps1`.

## SDK servicing and lock files

This release uses exactly .NET SDK 10.0.403 (`global.json`, roll-forward disabled).
The SDK supplies implicit linker dependencies, so changing its servicing patch can
invalidate a locked restore even when the explicit package references are unchanged.
To service the SDK, update `global.json`, restore with `--force-evaluate`, review the
lock-file diff, and repeat the locked restore, source tests, and package consumer gates.
Commit these changes together; do not disable locked restore in CI.

The test projects deliberately use `xunit.v3.mtp-off` 3.2.2 with the VSTest adapter.
This retains the existing `dotnet test`, TRX, filters, and coverage commands while
removing the unused, deprecated Microsoft.Testing.Platform.MSBuild 1.9.1 dependency.
Do not switch to the MTP v2 package without migrating those commands together.
