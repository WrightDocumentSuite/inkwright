[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $SourceLinkTool,
    [Parameter(Mandatory)][string] $RepositoryCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packages = (Resolve-Path -LiteralPath $PackageDirectory).Path
$pdbRoot = Join-Path (Split-Path $packages -Parent) ('sourcelink-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $pdbRoot | Out-Null
$expectedUrl = "https://raw.githubusercontent.com/WrightDocumentSuite/inkwright/$RepositoryCommit/*"
$checked = 0

# sourcelink 3.1.1 accepts a portable PDB; a .snupkg path exits 4 without diagnostics.
# Validate the PDB actually shipped in each symbol archive, rather than a build-directory copy.
foreach ($package in Get-ChildItem -LiteralPath $packages -Filter '*.snupkg') {
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = @($archive.Entries | Where-Object FullName -Like '*.pdb')
        if ($entries.Count -ne 1) {
            throw "$($package.Name) must contain exactly one portable PDB."
        }
        $pdbPath = Join-Path $pdbRoot ($package.BaseName + '.pdb')
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $pdbPath)
    }
    finally {
        $archive.Dispose()
    }

    $json = & $SourceLinkTool print-json $pdbPath
    if ($LASTEXITCODE -ne 0) { throw "Cannot read Source Link from $($package.Name)." }
    $mapping = ($json | ConvertFrom-Json).documents.PSObject.Properties
    if (@($mapping).Count -eq 0) { throw "$($package.Name) has no Source Link mapping." }
    foreach ($document in $mapping) {
        if ($document.Value -cne $expectedUrl) {
            throw "$($package.Name) has an unexpected Source Link URL: $($document.Value)"
        }
    }
    & $SourceLinkTool test $pdbPath
    if ($LASTEXITCODE -ne 0) { throw "Source Link checksums failed for $($package.Name)." }
    $checked++
}
if ($checked -ne 5) { throw "Expected five symbol packages, checked $checked." }
Write-Host 'Verified the canonical Source Link mapping and public source checksums for all five symbol packages.'
