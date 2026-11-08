[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,

    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $RepositoryCommit,

    [string] $RepositoryUrl = 'https://github.com/WrightDocumentSuite/inkwright.git'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$packageIds = @(
    'Inkwright',
    'Inkwright.Cjk',
    'Inkwright.Forms',
    'Inkwright.Layout',
    'Inkwright.Signing'
)

$centralPackagesPath = Join-Path $PSScriptRoot '../../Directory.Packages.props'
[xml] $centralPackages = Get-Content -LiteralPath $centralPackagesPath -Raw
$pkcsVersions = @(
    $centralPackages.Project.ItemGroup.PackageVersion |
        Where-Object { [string] $_.Include -ceq 'System.Security.Cryptography.Pkcs' }
)
if ($pkcsVersions.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string] $pkcsVersions[0].Version)) {
    throw "Expected exactly one literal System.Security.Cryptography.Pkcs version in $centralPackagesPath."
}

$pkcsVersion = [string] $pkcsVersions[0].Version
$expectedDependencies = @{
    'Inkwright' = @(
        [pscustomobject] @{ Id = 'System.Security.Cryptography.Pkcs'; Version = $pkcsVersion; Exclude = 'Build,Analyzers' }
    )
    'Inkwright.Cjk' = @(
        [pscustomobject] @{ Id = 'Inkwright'; Version = $Version; Exclude = 'Build,Analyzers' }
    )
    'Inkwright.Forms' = @(
        [pscustomobject] @{ Id = 'Inkwright'; Version = $Version; Exclude = 'Build,Analyzers' }
    )
    'Inkwright.Layout' = @(
        [pscustomobject] @{ Id = 'Inkwright'; Version = $Version; Exclude = 'Build,Analyzers' }
    )
    'Inkwright.Signing' = @(
        [pscustomobject] @{ Id = 'Inkwright'; Version = $Version; Exclude = 'Build,Analyzers' }
        [pscustomobject] @{ Id = 'Inkwright.Forms'; Version = $Version; Exclude = 'Build,Analyzers' }
        [pscustomobject] @{ Id = 'System.Security.Cryptography.Pkcs'; Version = $pkcsVersion; Exclude = 'Build,Analyzers' }
    )
}

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$ordinaryPackages = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter '*.nupkg')
$symbolPackages = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -File -Filter '*.snupkg')
$expectedOrdinaryNames = @($packageIds | ForEach-Object { "$($_).$Version.nupkg" })
$expectedSymbolNames = @($packageIds | ForEach-Object { "$($_).$Version.snupkg" })

function Assert-ExactNames {
    param(
        [Parameter(Mandatory)]
        [string[]] $Actual,

        [Parameter(Mandatory)]
        [string[]] $Expected,

        [Parameter(Mandatory)]
        [string] $Kind
    )

    $difference = @(
        Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual -CaseSensitive
    )
    if ($difference.Count -gt 0) {
        $rendered = $difference | Out-String
        throw "Unexpected $Kind package set:`n$rendered"
    }
}

function ConvertTo-DependencySpec {
    param(
        [Parameter(Mandatory)]
        [string] $Id,

        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [string] $Exclude
    )

    $excludedAssets = @(
        $Exclude.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { $_.Trim() } |
            Sort-Object
    )
    return "$Id|$Version|$($excludedAssets -join ',')"
}

function Assert-PackageDependencies {
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlElement] $Metadata,

        [Parameter(Mandatory)]
        [string] $PackageId
    )

    $groups = @($Metadata.dependencies.group)
    if ($groups.Count -ne 1 -or [string] $groups[0].targetFramework -cne 'net10.0') {
        throw "$PackageId must declare exactly one net10.0 dependency group."
    }

    $actual = @(
        $groups[0].dependency | ForEach-Object {
            ConvertTo-DependencySpec `
                -Id ([string] $_.id) `
                -Version ([string] $_.version) `
                -Exclude ([string] $_.exclude)
        }
    )
    $expected = @(
        $expectedDependencies[$PackageId] | ForEach-Object {
            ConvertTo-DependencySpec -Id $_.Id -Version $_.Version -Exclude $_.Exclude
        }
    )

    Assert-ExactNames -Actual $actual -Expected $expected -Kind "$PackageId dependency"
}

function Get-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive] $Archive,

        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry] $Entry
    )

    $stream = $Entry.Open()
    $reader = [System.IO.StreamReader]::new($stream)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Test-PackageArchive {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $PackageId,

        [switch] $Symbols
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        $nuspecEntries = @(
            $archive.Entries |
                Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) }
        )
        if ($nuspecEntries.Count -ne 1) {
            throw "$Path must contain exactly one nuspec; found $($nuspecEntries.Count)."
        }

        [xml] $nuspec = Get-ZipEntryText -Archive $archive -Entry $nuspecEntries[0]
        $metadata = $nuspec.package.metadata
        if ([string] $metadata.id -cne $PackageId) {
            throw "$Path declares package id '$($metadata.id)' instead of '$PackageId'."
        }
        if ([string] $metadata.version -cne $Version) {
            throw "$Path declares version '$($metadata.version)' instead of '$Version'."
        }
        if ([string] $metadata.repository.url -cne $RepositoryUrl) {
            throw "$Path declares repository '$($metadata.repository.url)' instead of '$RepositoryUrl'."
        }
        if ([string] $metadata.repository.commit -cne $RepositoryCommit) {
            throw "$Path declares commit '$($metadata.repository.commit)' instead of '$RepositoryCommit'."
        }
        if ([string] $metadata.repository.type -cne 'git') {
            throw "$Path must declare the git repository type."
        }
        if ([string] $metadata.projectUrl -cne 'https://github.com/WrightDocumentSuite/inkwright') {
            throw "$Path has an unexpected project URL '$($metadata.projectUrl)'."
        }
        foreach ($requiredText in 'description', 'copyright', 'tags') {
            if ([string]::IsNullOrWhiteSpace([string] $metadata.$requiredText)) {
                throw "$Path has empty $requiredText metadata."
            }
        }
        Assert-PackageDependencies -Metadata $metadata -PackageId $PackageId

        if ($Symbols) {
            $expectedPdb = "lib/net10.0/$PackageId.pdb"
            $pdbEntries = @(
                $archive.Entries |
                    Where-Object { $_.FullName.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) }
            )
            Assert-ExactNames `
                -Actual @($pdbEntries.FullName) `
                -Expected @($expectedPdb) `
                -Kind "$PackageId symbol"
            if ($pdbEntries[0].Length -le 0) {
                throw "$Path contains an empty $expectedPdb."
            }
            return
        }

        if ([string] $metadata.authors -cne 'Pavel Deecalov') {
            throw "$Path has unexpected authors metadata '$($metadata.authors)'."
        }
        if ([string] $metadata.license.type -cne 'expression' -or
            [string] $metadata.license.InnerText -cne 'Apache-2.0') {
            throw "$Path must declare the Apache-2.0 licence expression."
        }
        if ([string] $metadata.readme -cne 'README.md') {
            throw "$Path must declare README.md as its package readme."
        }

        foreach ($requiredEntry in 'README.md', 'NOTICE') {
            $entry = $archive.GetEntry($requiredEntry)
            if ($null -eq $entry) {
                throw "$Path is missing $requiredEntry."
            }
            if ($entry.Length -le 0) {
                throw "$Path contains an empty $requiredEntry."
            }
        }

        $assemblyPath = "lib/net10.0/$PackageId.dll"
        $assembly = $archive.GetEntry($assemblyPath)
        if ($null -eq $assembly -or $assembly.Length -le 0) {
            throw "$Path is missing $assemblyPath."
        }

        $documentationPath = "lib/net10.0/$PackageId.xml"
        $documentation = $archive.GetEntry($documentationPath)
        if ($null -eq $documentation -or $documentation.Length -le 0) {
            throw "$Path is missing non-empty XML documentation at $documentationPath."
        }

        if ($PackageId -ceq 'Inkwright.Forms') {
            $generator = $archive.GetEntry('analyzers/dotnet/cs/Inkwright.Forms.Generator.dll')
            if ($null -eq $generator -or $generator.Length -le 0) {
                throw "$Path is missing the forms source generator."
            }
        }

        if ($PackageId -ceq 'Inkwright.Cjk') {
            $cmapLicence = $archive.GetEntry('LICENSE-ADOBE-CMAP.txt')
            if ($null -eq $cmapLicence -or $cmapLicence.Length -le 0) {
                throw "$Path is missing the Adobe CMap licence."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

Assert-ExactNames -Actual @($ordinaryPackages.Name) -Expected $expectedOrdinaryNames -Kind ordinary
Assert-ExactNames -Actual @($symbolPackages.Name) -Expected $expectedSymbolNames -Kind symbol

foreach ($packageId in $packageIds) {
    Test-PackageArchive -Path (Join-Path $resolvedPackageDirectory "$packageId.$Version.nupkg") -PackageId $packageId
    Test-PackageArchive -Path (Join-Path $resolvedPackageDirectory "$packageId.$Version.snupkg") -PackageId $packageId -Symbols
}

Write-Host "Validated five NuGet packages and five symbol packages for $Version."
