# Building NuGet Packages

The solution ships five packages: `Inkwright`, `Inkwright.Cjk`, `Inkwright.Forms`, `Inkwright.Layout`, and `Inkwright.Signing`. Test projects, examples, benchmarks, and the standalone forms generator build are not packaged. The generator is included in `Inkwright.Forms` as an analyzer asset.

Shared metadata lives in `Directory.Build.props`: version, authors, the Apache-2.0 licence, README, project URL, and repository URL. Each package's description and search tags live in its `.csproj` because the add-ons serve different purposes. NuGet shows the `Description` element from the nuspec on the package card and `README.md` as the detailed description below it.

## Build

From the `Inkwright` root:

```bash
dotnet restore Inkwright.slnx
dotnet test Inkwright.slnx -c Release --no-restore
dotnet pack Inkwright.slnx -c Release --no-restore \
  -p:Version=0.1.0 \
  --output artifacts/packages
```

The `Version` parameter is intentionally set on the release command: the same commit can be built as prerelease (`0.2.0-preview.1`) or stable without editing the project. If the parameter is omitted, the value from `Directory.Build.props` is used.

The result is one `.nupkg` per published project in `artifacts/packages`. Before publishing, open the packages as ZIP and check `.nuspec`, `README.md`, `lib/net10.0`, dependencies, and for `Inkwright.Forms`, `analyzers/dotnet/cs/Inkwright.Forms.Generator.dll`.

## Verify Installation Locally

A new empty project lets you verify not only the nuspec but real dependency resolution. Create it **outside the repository tree**: otherwise the project's central package management will intercept `dotnet add package` and the command will try to modify the working `Directory.Packages.props`.

### Clear the old version from cache first

This is not a precaution but a required step; without it verification is meaningless. NuGet versions are immutable **by identity**: once `Inkwright/0.1.0` is in `~/.nuget/packages`, restore takes it from there and no longer looks at the local feed. A rebuilt package with the same number sits untouched while you verify yesterday's bytes and never know.

```bash
for id in inkwright inkwright.cjk inkwright.forms inkwright.layout inkwright.signing; do
  rm -rf ~/.nuget/packages/$id
done
```

Only these five entries are removed: `dotnet nuget locals global-packages --clear` wipes the entire cache and forces every other project on the machine to download dependencies again.

You can confirm the fresh build was picked up by file timestamp:

```bash
ls -l ~/.nuget/packages/inkwright/0.1.0/lib/net10.0/Inkwright.dll
```

It should match the time of `dotnet pack`, not be earlier.

```bash
cd /path/outside/PDFLib
dotnet new console -n PackageSmoke -f net10.0
dotnet new nugetconfig -o PackageSmoke --force
dotnet nuget add source "/absolute/path/to/Inkwright/artifacts/packages" \
  --name inkwright-local \
  --configfile PackageSmoke/nuget.config
cd PackageSmoke
dotnet add package Inkwright.Signing --version 0.1.0
dotnet build -c Release --no-restore
```

Specify the source path in Windows form (`E:\...`): `dotnet nuget add source` rejects paths with forward slashes as invalid.

The created config keeps nuget.org for external dependencies and adds the local source. For full verification add all five package IDs. Temporary `PackageSmoke` is not part of the repository.

### Verify behavior, not just compilation

An empty `Main` that builds only proves the nuspec is not broken. More useful is a short program that exercises through the built binaries what changed in this release: it also checks the **public API surface**. Internal types like `PdfNames` are visible to tests via `InternalsVisibleTo`, but not to package consumers — code in documentation examples that accidentally relied on such a type compiles for you and not for the user.

## Publish

After verifying a specific version:

```bash
dotnet nuget push "artifacts/packages/*.nupkg" \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate
```

NuGet versions are immutable: rebuilding with the same number does not replace a published package. The API secret must not be stored in the repository or passed as an MSBuild property that ends up in logs.
