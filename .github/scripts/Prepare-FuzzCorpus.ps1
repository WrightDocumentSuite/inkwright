[CmdletBinding()]
param(
    [string] $CorpusRoot = 'fuzz/corpus',
    [Parameter(Mandatory = $true)]
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = (Resolve-Path -LiteralPath $CorpusRoot).Path
$destinationRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    [IO.Path]::GetFullPath($OutputRoot)
}
else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputRoot))
}

$sourcePrefix = $sourceRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($destinationRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $destinationRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The materialized corpus must be outside the committed corpus directory.'
}

if (Test-Path -LiteralPath $destinationRoot) {
    throw "The materialized corpus directory already exists: $destinationRoot"
}

$targets = 'reader', 'cff', 'jbig2'
[void](New-Item -ItemType Directory -Path $destinationRoot)

foreach ($target in $targets) {
    $sourceDirectory = Join-Path $sourceRoot $target
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        throw "The '$target' corpus directory is missing: $sourceDirectory"
    }

    $destinationDirectory = Join-Path $destinationRoot $target
    [void](New-Item -ItemType Directory -Path $destinationDirectory)
    $seedCount = 0

    foreach ($seed in Get-ChildItem -LiteralPath $sourceDirectory -File | Sort-Object Name) {
        $isBase64 = $seed.Extension.Equals('.base64', [StringComparison]::OrdinalIgnoreCase)
        $destinationName = if ($isBase64) {
            [IO.Path]::GetFileNameWithoutExtension($seed.Name)
        }
        else {
            $seed.Name
        }

        $destination = Join-Path $destinationDirectory $destinationName
        if (Test-Path -LiteralPath $destination) {
            throw "Two committed seeds materialize to the same path: $destination"
        }

        if ($isBase64) {
            try {
                $bytes = [Convert]::FromBase64String(([IO.File]::ReadAllText($seed.FullName)).Trim())
            }
            catch [FormatException] {
                throw [FormatException]::new(
                    "The seed '$($seed.FullName)' is not valid base64.",
                    $_.Exception)
            }

            [IO.File]::WriteAllBytes($destination, $bytes)
        }
        else {
            Copy-Item -LiteralPath $seed.FullName -Destination $destination
        }

        $seedCount++
    }

    if ($seedCount -eq 0) {
        throw "The '$target' fuzz target has no seed."
    }
}

Write-Host "Materialized fuzz corpus at $destinationRoot"
