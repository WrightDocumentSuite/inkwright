[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SamplesDirectory,
    [Parameter(Mandatory)][string] $WorkDirectory,
    [string] $VeraPdfVersion = '1.30.3',
    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string] $InstallerSha256 = 'b512bf24945503a33fc10738ecffc1f2dccaaf8593a448e94ee9ac86a9559a59',
    [string] $KnownFailures = (Join-Path $PSScriptRoot 'verapdf-known-failures.txt')
)

# Checks Inkwright's conformance claims with veraPDF, which shares no code with the library's own
# PDF/A and PDF/UA validators and so cannot share their mistakes. A failed rule fails the gate
# unless it is listed, with a reason, in verapdf-known-failures.txt. A listed rule that now passes
# fails too, so that the list cannot quietly go stale.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$work = (New-Item -ItemType Directory -Force -Path $WorkDirectory).FullName
$installRoot = Join-Path $work 'verapdf'
$executable = Join-Path $installRoot ($IsWindows ? 'verapdf.bat' : 'verapdf')

if (-not (Test-Path -LiteralPath $executable)) {
    $major = ($VeraPdfVersion -split '\.')[0..1] -join '.'
    $zip = Join-Path $work "verapdf-$VeraPdfVersion.zip"
    Invoke-WebRequest -Uri "https://software.verapdf.org/rel/$major/verapdf-greenfield-$VeraPdfVersion-installer.zip" -OutFile $zip

    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($actual -ine $InstallerSha256) {
        throw "veraPDF installer hash mismatch: expected $InstallerSha256, got $actual."
    }

    $expanded = Join-Path $work 'verapdf-installer'
    Expand-Archive -LiteralPath $zip -DestinationPath $expanded -Force
    $jar = Get-ChildItem -LiteralPath $expanded -Recurse -Filter 'verapdf-izpack-installer-*.jar' | Select-Object -First 1
    if ($null -eq $jar) {
        throw 'The veraPDF archive holds no installer jar.'
    }

    $response = Join-Path $work 'auto-install.xml'
    @"
<?xml version="1.0" encoding="UTF-8" standalone="no"?>
<AutomatedInstallation langpack="eng">
  <com.izforge.izpack.panels.htmlhello.HTMLHelloPanel id="welcome"/>
  <com.izforge.izpack.panels.target.TargetPanel id="install_dir"><installpath>$([Security.SecurityElement]::Escape($installRoot))</installpath></com.izforge.izpack.panels.target.TargetPanel>
  <com.izforge.izpack.panels.packs.PacksPanel id="sdk_pack_select">
    <pack index="0" name="veraPDF GUI" selected="true"/>
    <pack index="1" name="veraPDF CLI" selected="true"/>
    <pack index="2" name="veraPDF Documentation" selected="false"/>
    <pack index="3" name="veraPDF Sample Plugins" selected="false"/>
  </com.izforge.izpack.panels.packs.PacksPanel>
  <com.izforge.izpack.panels.install.InstallPanel id="install"/>
  <com.izforge.izpack.panels.finish.FinishPanel id="finish"/>
</AutomatedInstallation>
"@ | Set-Content -LiteralPath $response -Encoding utf8

    & java -jar $jar.FullName $response | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $executable)) {
        throw "The veraPDF installer did not produce $executable."
    }
}

$version = & $executable --version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $version -notmatch "(?m)^veraPDF $([regex]::Escape($VeraPdfVersion))\s*$") {
    throw "Expected veraPDF $VeraPdfVersion, got: $version"
}
Write-Host $version.Trim()
$reports = (New-Item -ItemType Directory -Force -Path (Join-Path $work 'reports')).FullName

$known = @{}
if (Test-Path -LiteralPath $KnownFailures) {
    foreach ($line in Get-Content -LiteralPath $KnownFailures) {
        if ($line -match '^\s*(#|$)') { continue }
        $key, $reason = $line -split '\s+#\s*', 2
        if (-not $reason) { throw "A known failure must say why it is accepted: $line" }
        $known[$key.Trim()] = $reason
    }
}

# The flavour is named per file rather than detected from the file's own claim, so that a sample
# which declares the wrong thing is checked against what it was meant to be.
$flavours = @{
    'pdfa-1a' = '1a'; 'pdfa-1b' = '1b'; 'pdfa-2a' = '2a'; 'pdfa-2b' = '2b'; 'pdfa-2u' = '2u'
    'pdfa-3a' = '3a'; 'pdfa-3b' = '3b'; 'pdfa-3u' = '3u'; 'pdfa-4'  = '4';  'pdfua-1' = 'ua1'
    'pdfua-2' = 'ua2'
}

$failures = [Collections.Generic.List[string]]::new()
$seen = [Collections.Generic.HashSet[string]]::new()
$samples = @(Get-ChildItem -LiteralPath $SamplesDirectory -Filter '*.pdf' | Sort-Object Name)
if ($samples.Count -ne $flavours.Count) {
    throw "Expected $($flavours.Count) samples, found $($samples.Count)."
}
foreach ($name in $flavours.Keys) {
    if (@($samples | Where-Object BaseName -EQ $name).Count -ne 1) {
        throw "Expected exactly one $name.pdf sample."
    }
}

foreach ($sample in $samples) {
    $flavour = $flavours[$sample.BaseName]
    if (-not $flavour) { throw "No flavour is declared for $($sample.Name)." }

    $report = & $executable --format mrr --flavour $flavour $sample.FullName 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $report | Set-Content -LiteralPath (Join-Path $reports ($sample.BaseName + '.xml')) -Encoding utf8
    if ($exitCode -ne 0) { throw "veraPDF exited with code $exitCode for $($sample.Name):`n$report" }
    $xmlStart = $report.IndexOf('<?xml')
    if ($xmlStart -lt 0) { throw "veraPDF produced no report for $($sample.Name):`n$report" }

    [xml] $document = $report.Substring($xmlStart)
    $validation = $document.SelectSingleNode('//validationReport')
    if ($null -eq $validation) {
        $failures.Add("$($sample.BaseName): veraPDF could not validate it (flavour $flavour)")
        continue
    }

    $status = $validation.GetAttribute('isCompliant')
    if ($status -notin 'true', 'false') { throw "Missing validation status for $($sample.Name)." }
    $compliant = $status -eq 'true'
    Write-Host ("{0,-10} {1,-4} {2}" -f $sample.BaseName, $flavour, ($compliant ? 'compliant' : 'NOT compliant'))

    $failedRules = @($validation.SelectNodes('.//rule[@status="failed"]'))
    if (-not $compliant -and $failedRules.Count -eq 0) {
        $failures.Add("$($sample.BaseName): not compliant, but no failed rules were reported")
    }
    if ($compliant -and $failedRules.Count -gt 0) {
        $failures.Add("$($sample.BaseName): contradictory validation status and failed rules")
    }
    foreach ($rule in $failedRules) {
        $key = '{0}:{1}-{2}' -f $sample.BaseName, $rule.GetAttribute('clause'), $rule.GetAttribute('testNumber')
        [void] $seen.Add($key)
        if (-not $known.ContainsKey($key)) {
            $description = $rule.SelectSingleNode('description').InnerText
            $failures.Add("$key  $description")
        }
    }
}

foreach ($key in $known.Keys) {
    if (-not $seen.Contains($key)) {
        $failures.Add("$key  is listed as a known failure but now passes; remove it from the list")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "::error::$_" }
    throw "veraPDF found $($failures.Count) problem(s)."
}

Write-Host "veraPDF accepted all $($samples.Count) samples ($($known.Count) known failure(s) listed)."
