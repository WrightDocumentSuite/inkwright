[CmdletBinding()]
param(
    [string] $Project = 'fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj',
    [string] $CorpusRoot = 'fuzz/corpus'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targets = 'reader', 'cff', 'jbig2'
$seedCount = 0
$temporary = [IO.Directory]::CreateTempSubdirectory('inkwright-fuzz-seeds-')

try {
    $materialized = Join-Path $temporary.FullName 'corpus'
    & (Join-Path $PSScriptRoot 'Prepare-FuzzCorpus.ps1') `
        -CorpusRoot $CorpusRoot `
        -OutputRoot $materialized

    foreach ($target in $targets) {
        $targetDirectory = Join-Path $materialized $target
        $seeds = @(Get-ChildItem -LiteralPath $targetDirectory -File | Sort-Object Name)

        foreach ($seed in $seeds) {
            Write-Host "Replaying $target seed $($seed.Name)"
            & dotnet run `
                --project $Project `
                --configuration Release `
                --no-build `
                --no-restore `
                -- `
                $target `
                --replay $seed.FullName
            if ($LASTEXITCODE -ne 0) {
                throw "Fuzz seed replay failed for $target/$($seed.Name) with exit code $LASTEXITCODE."
            }

            $seedCount++
        }
    }
}
finally {
    $temporary.Delete($true)
}

Write-Host "Replayed $seedCount fuzz regression seeds."
