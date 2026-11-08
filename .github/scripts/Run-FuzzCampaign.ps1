[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('reader', 'cff', 'jbig2')]
    [string] $Target,
    [ValidateRange(30, 86400)]
    [int] $DurationSeconds = 300,
    [ValidateRange(100, 60000)]
    [int] $ExecutionTimeoutMilliseconds = 5000,
    [string] $Project = 'fuzz/Inkwright.Fuzz/Inkwright.Fuzz.csproj',
    [string] $CorpusRoot = 'fuzz/corpus',
    [string] $OutputRoot = 'artifacts/fuzz',
    [string] $SharpFuzzCommand = 'sharpfuzz',
    [string] $AflFuzzCommand = 'afl-fuzz'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsLinux) {
    throw 'The AFL++ campaign runs on Linux. Use seed replay for cross-platform regression checks.'
}

foreach ($command in $SharpFuzzCommand, $AflFuzzCommand) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required fuzzing command was not found: $command"
    }
}

$runRoot = Join-Path $OutputRoot $Target
if (Test-Path -LiteralPath $runRoot) {
    throw "The fuzz output directory already exists: $runRoot"
}

$publishDirectory = Join-Path $runRoot 'publish'
$materializedCorpus = Join-Path $runRoot 'seeds'
$findingsDirectory = Join-Path $runRoot 'findings'
[void](New-Item -ItemType Directory -Path $runRoot)

& (Join-Path $PSScriptRoot 'Prepare-FuzzCorpus.ps1') `
    -CorpusRoot $CorpusRoot `
    -OutputRoot $materializedCorpus

& dotnet publish $Project `
    --configuration Release `
    --no-restore `
    --output $publishDirectory `
    -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the fuzz harness failed with exit code $LASTEXITCODE."
}

$projectName = (Get-Item -LiteralPath $Project).BaseName
$harnessDll = Join-Path $publishDirectory "$projectName.dll"
$excluded = 'dnlib.dll', 'SharpFuzz.dll', 'SharpFuzz.Common.dll', "$projectName.dll"
$instrumentationTargets = @(
    Get-ChildItem -LiteralPath $publishDirectory -Filter '*.dll' |
        Where-Object { $_.Name -notin $excluded } |
        Where-Object { $_.Name -notlike 'System.*.dll' }
)

if ($instrumentationTargets.Count -eq 0) {
    throw "No library assembly was found to instrument in $publishDirectory"
}

foreach ($assembly in $instrumentationTargets) {
    Write-Host "Instrumenting $($assembly.Name)"
    & $SharpFuzzCommand $assembly.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "SharpFuzz failed to instrument $($assembly.Name) with exit code $LASTEXITCODE."
    }
}

$env:AFL_SKIP_BIN_CHECK = '1'
$env:AFL_SKIP_CPUFREQ = '1'
$env:AFL_NO_AFFINITY = '1'
$env:AFL_NO_UI = '1'
$env:AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES = '1'

$seedDirectory = Join-Path $materializedCorpus $Target
$arguments = @(
    '-i', $seedDirectory,
    '-o', $findingsDirectory,
    '-t', "$ExecutionTimeoutMilliseconds+",
    '-m', 'none',
    '-V', $DurationSeconds,
    '--',
    'dotnet', $harnessDll, $Target
)

Write-Host "Fuzzing $Target for $DurationSeconds seconds"
& $AflFuzzCommand @arguments
if ($LASTEXITCODE -ne 0) {
    throw "AFL++ exited with code $LASTEXITCODE. Findings remain in $findingsDirectory."
}

$statsFiles = @(Get-ChildItem -LiteralPath $findingsDirectory -Recurse -File -Filter 'fuzzer_stats')
if ($statsFiles.Count -eq 0) {
    throw "AFL++ produced no fuzzer_stats file; the campaign cannot prove that the target ran."
}

$coverageObserved = $false
foreach ($statsFile in $statsFiles) {
    $stats = @{}
    foreach ($line in Get-Content -LiteralPath $statsFile.FullName) {
        if ($line -match '^\s*([^:]+?)\s*:\s*(.*?)\s*$') {
            $stats[$Matches[1]] = $Matches[2]
        }
    }

    $execsDone = if ($stats.ContainsKey('execs_done')) { [long]$stats['execs_done'] } else { 0 }
    $edgesFound = if ($stats.ContainsKey('edges_found')) { [long]$stats['edges_found'] } else { 0 }
    $bitmapCoverage = 0.0
    if ($stats.ContainsKey('bitmap_cvg') -and $stats['bitmap_cvg'] -match '([0-9]+(?:\.[0-9]+)?)%') {
        $bitmapCoverage = [double]::Parse(
            $Matches[1],
            [Globalization.CultureInfo]::InvariantCulture)
    }

    # paths_total includes the initial queue and can be positive even when the managed target has
    # no effective instrumentation. Only an AFL coverage-map signal proves that SharpFuzz worked.
    if ($execsDone -gt 0 -and ($edgesFound -gt 0 -or $bitmapCoverage -gt 0)) {
        $coverageObserved = $true
        break
    }
}

if (-not $coverageObserved) {
    throw 'AFL++ reported no executed, coverage-producing target. Verify SharpFuzz instrumentation.'
}

$queueInputs = @(
    Get-ChildItem -LiteralPath $findingsDirectory -Recurse -File |
        Where-Object { $_.Directory.Name -eq 'queue' -and $_.Name -like 'id:*' }
)
if ($queueInputs.Count -eq 0) {
    throw 'AFL++ produced no queue inputs; the campaign result is not usable coverage evidence.'
}

$failures = @(
    Get-ChildItem -LiteralPath $findingsDirectory -Recurse -File |
        Where-Object { $_.Directory.Name -in 'crashes', 'hangs' } |
        Where-Object { $_.Name -notlike 'README*' }
)

if ($failures.Count -gt 0) {
    $relative = $failures | ForEach-Object {
        [IO.Path]::GetRelativePath($findingsDirectory, $_.FullName)
    }
    throw "AFL++ found $($failures.Count) crash or hang input(s): $($relative -join ', ')"
}

Write-Host "Fuzz campaign completed without crashes or hangs. Findings: $findingsDirectory"
