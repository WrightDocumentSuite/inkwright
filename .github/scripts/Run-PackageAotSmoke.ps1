[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [string] $PackageVersion,

    [Parameter(Mandatory)]
    [string] $RuntimeIdentifier,

    [string] $WorkingDirectory = [System.IO.Path]::GetTempPath()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$templateDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../package-smoke')).Path
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$resolvedWorkingDirectory = if ([System.IO.Path]::IsPathRooted($WorkingDirectory)) {
    [System.IO.Path]::GetFullPath($WorkingDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $WorkingDirectory))
}
$relativeToRepository = [System.IO.Path]::GetRelativePath($repositoryRoot, $resolvedWorkingDirectory)
$outsideRepository = [System.IO.Path]::IsPathRooted($relativeToRepository) -or
    $relativeToRepository -eq '..' -or
    $relativeToRepository.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
    $relativeToRepository.StartsWith("..$([System.IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)
if (-not $outsideRepository) {
    throw 'WorkingDirectory must be outside the repository so the smoke test cannot inherit repository build configuration.'
}
# Keep the generated path comfortably below legacy MAX_PATH on Windows. Runtime-pack assembly
# names are long, and a descriptive nested cache root can otherwise surface as a misleading
# MSB3030 "file not found" even though NuGet extracted the file successfully.
$workRoot = Join-Path $resolvedWorkingDirectory ("iw-" + [Guid]::NewGuid().ToString('N'))
$projectDirectory = Join-Path $workRoot 'src'
$packageCache = Join-Path $workRoot 'pkg'
$publishDirectory = Join-Path $workRoot 'out'

New-Item -ItemType Directory -Path $projectDirectory, $packageCache, $publishDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $templateDirectory 'PackageSmoke.csproj') -Destination $projectDirectory
Copy-Item -LiteralPath (Join-Path $templateDirectory 'Program.cs') -Destination $projectDirectory

$escapedFeed = [System.Security.SecurityElement]::Escape($resolvedPackageDirectory)
$nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="inkwright-local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="inkwright-local">
      <package pattern="Inkwright" />
      <package pattern="Inkwright.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="runtime.*" />
      <package pattern="System.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
$nugetConfigPath = Join-Path $projectDirectory 'NuGet.Config'
$nugetConfig | Set-Content -LiteralPath $nugetConfigPath -Encoding utf8NoBOM

$projectPath = Join-Path $projectDirectory 'PackageSmoke.csproj'
$previousPackageCache = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $packageCache

try {
    & dotnet restore $projectPath `
        --configfile $nugetConfigPath `
        --runtime $RuntimeIdentifier `
        "-p:InkwrightPackageVersion=$PackageVersion" `
        "-p:RuntimeIdentifier=$RuntimeIdentifier" `
        -p:SelfContained=true `
        -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) {
        throw "Package smoke restore failed with exit code $LASTEXITCODE."
    }

    foreach ($packageId in 'inkwright', 'inkwright.cjk', 'inkwright.forms', 'inkwright.layout', 'inkwright.signing') {
        $nuspec = Join-Path $packageCache "$packageId/$($PackageVersion.ToLowerInvariant())/$packageId.nuspec"
        if (-not (Test-Path -LiteralPath $nuspec -PathType Leaf)) {
            throw "The isolated cache does not contain $packageId/$PackageVersion from the local feed."
        }
    }

    & dotnet publish $projectPath `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --no-restore `
        --output $publishDirectory `
        "-p:InkwrightPackageVersion=$PackageVersion" `
        -p:PublishAot=true
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT publish failed with exit code $LASTEXITCODE."
    }

    $executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
        'PackageSmoke.exe'
    }
    else {
        'PackageSmoke'
    }

    $executable = Join-Path $publishDirectory $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Native executable was not produced at $executable."
    }

    & $executable
    if ($LASTEXITCODE -ne 0) {
        throw "Native AOT package smoke failed with exit code $LASTEXITCODE."
    }

    Write-Host "Native AOT package smoke passed. Working directory: $workRoot"
}
finally {
    $env:NUGET_PACKAGES = $previousPackageCache
}
