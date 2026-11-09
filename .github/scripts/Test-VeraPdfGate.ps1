[CmdletBinding()]
param()

# Exercise failure handling without downloading veraPDF or depending on a broken PDF fixture.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('verapdf-gate-' + [guid]::NewGuid().ToString('N'))
$samples = New-Item -ItemType Directory -Path (Join-Path $temporary 'samples')
$tool = New-Item -ItemType Directory -Path (Join-Path $temporary 'tool/verapdf')
$gate = Join-Path $PSScriptRoot 'Test-VeraPdf.ps1'
$names = 'pdfa-1a', 'pdfa-1b', 'pdfa-2a', 'pdfa-2b', 'pdfa-2u', 'pdfa-3a', 'pdfa-3b', 'pdfa-3u', 'pdfa-4', 'pdfua-1', 'pdfua-2'
foreach ($name in $names) { [IO.File]::WriteAllBytes((Join-Path $samples "$name.pdf"), [byte[]]@()) }

function Test-Case([string] $Name, [string] $Report, [int] $ExitCode, [bool] $Passes, [string] $Known = '') {
    $Report | Set-Content -LiteralPath (Join-Path $tool 'report.xml') -Encoding utf8
    $executable = Join-Path $tool ($IsWindows ? 'verapdf.bat' : 'verapdf')
    if ($IsWindows) {
        @"
@echo off
if "%~1"=="--version" (
  echo veraPDF 1.30.3
  exit /b 0
)
type "%~dp0report.xml"
exit /b $ExitCode
"@ | Set-Content -LiteralPath $executable -Encoding ascii
    }
    else {
        @'
#!/usr/bin/env bash
if [[ "$1" == "--version" ]]; then
  echo 'veraPDF 1.30.3'
  exit 0
fi
cat "$(dirname "$0")/report.xml"
'@ + "`nexit $ExitCode`n" | Set-Content -LiteralPath $executable -Encoding utf8
        & chmod +x $executable
        if ($LASTEXITCODE -ne 0) { throw 'Could not make the fake CLI executable.' }
    }
    $knownPath = Join-Path $temporary 'known.txt'
    $Known | Set-Content -LiteralPath $knownPath -Encoding utf8
    $output = & pwsh -NoProfile -File $gate -SamplesDirectory $samples.FullName -WorkDirectory (Join-Path $temporary 'tool') -KnownFailures $knownPath 2>&1 | Out-String
    $actualPasses = $LASTEXITCODE -eq 0
    if ($actualPasses -ne $Passes) { throw "Gate case '$Name' returned the wrong result:`n$output" }
    Write-Host "Passed gate case: $Name"
}

try {
    $valid = '<?xml version="1.0"?><report><validationReport isCompliant="true"/></report>'
    Test-Case 'all samples compliant' $valid 0 $true
    Test-Case 'CLI error despite valid XML' $valid 7 $false
    Test-Case 'missing report' 'no XML' 0 $false
    Test-Case 'missing validation result' '<?xml version="1.0"?><report/>' 0 $false
    Test-Case 'missing status' '<?xml version="1.0"?><report><validationReport/></report>' 0 $false
    Test-Case 'noncompliant without failed rules' '<?xml version="1.0"?><report><validationReport isCompliant="false"/></report>' 0 $false
    $rule = '<rule status="failed" clause="6.1" testNumber="1"><description>broken</description></rule>'
    Test-Case 'unexpected failed rule' "<?xml version=`"1.0`"?><report><validationReport isCompliant=`"false`">$rule</validationReport></report>" 0 $false
    Test-Case 'contradictory report' "<?xml version=`"1.0`"?><report><validationReport isCompliant=`"true`">$rule</validationReport></report>" 0 $false
    Test-Case 'stale exception' $valid 0 $false 'pdfa-1a:6.1-1 # historical failure'
    $known = ($names | ForEach-Object { "${_}:6.1-1 # synthetic failure for the gate test" }) -join "`n"
    Test-Case 'explicit exceptions' "<?xml version=`"1.0`"?><report><validationReport isCompliant=`"false`">$rule</validationReport></report>" 0 $true $known
    Remove-Item -LiteralPath (Join-Path $samples 'pdfua-2.pdf')
    Test-Case 'missing sample' $valid 0 $false
}
finally {
    # This path is generated directly under the system temporary directory by this test.
    $resolved = [IO.Path]::GetFullPath($temporary)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $parent -or
        [IO.Path]::GetFileName($resolved) -notlike 'verapdf-gate-*') { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
