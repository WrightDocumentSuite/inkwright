param(
    [Parameter(Mandatory)]
    [string] $CorpusParent
)

$ErrorActionPreference = 'Stop'

$rootNames = @(
    'PdfPig-0.1.15',
    'itext-dotnet-9.7.0',
    'pdf.js-6.1.200',
    'XMP-Toolkit-SDK-main'
)
$domain = "inkwright-reference-corpus-v1`n"
$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
$entries = [System.Collections.Generic.SortedDictionary[string, System.IO.FileInfo]]::new(
    [System.StringComparer]::Ordinal)

$parent = [System.IO.Path]::GetFullPath($CorpusParent)
foreach ($rootName in $rootNames) {
    $root = Join-Path $parent $rootName
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "The reference corpus root is missing: $root"
    }

    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        if (-not [string]::Equals($file.Extension, '.pdf', [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $relative = [System.IO.Path]::GetRelativePath($root, $file.FullName)
        $relative = $relative.Replace([System.IO.Path]::DirectorySeparatorChar, '/')
        if ([System.IO.Path]::AltDirectorySeparatorChar -ne '/') {
            $relative = $relative.Replace([System.IO.Path]::AltDirectorySeparatorChar, '/')
        }

        $logicalPath = "$rootName/$relative".Normalize([System.Text.NormalizationForm]::FormC)
        if ($entries.ContainsKey($logicalPath)) {
            throw "The corpus contains duplicate normalized path '$logicalPath'."
        }

        $entries.Add($logicalPath, $file)
    }
}

$treeHash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$totalBytes = [long]0
try {
    $treeHash.AppendData($utf8.GetBytes($domain))

    foreach ($entry in $entries.GetEnumerator()) {
        $stream = [System.IO.File]::OpenRead($entry.Value.FullName)
        $fileHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $length = $stream.Length
            $fileHash = [System.Convert]::ToHexString(
                $fileHashAlgorithm.ComputeHash($stream)).ToLowerInvariant()
        }
        finally {
            $fileHashAlgorithm.Dispose()
            $stream.Dispose()
        }

        $totalBytes += $length
        $lengthText = $length.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        $record = "$($entry.Key)`0$lengthText`0$fileHash`n"
        $treeHash.AppendData($utf8.GetBytes($record))
    }

    $digest = [System.Convert]::ToHexString($treeHash.GetHashAndReset()).ToLowerInvariant()
}
finally {
    $treeHash.Dispose()
}

[pscustomobject]@{
    Count = $entries.Count
    Bytes = $totalBytes
    Sha256 = $digest
}
