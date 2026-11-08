[CmdletBinding()]
param(
    [string] $Solution = 'Inkwright.slnx'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-PackageAudit {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('vulnerable', 'deprecated')]
        [string] $Kind
    )

    $output = & dotnet package list `
        --project $Solution `
        --no-restore `
        --include-transitive `
        "--$Kind" `
        --format json `
        --output-version 1

    if ($LASTEXITCODE -ne 0) {
        throw "The $Kind package audit failed with exit code $LASTEXITCODE."
    }

    $report = ($output -join [Environment]::NewLine) | ConvertFrom-Json
    if ($report.PSObject.Properties.Name -notcontains 'sources' -or @($report.sources).Count -eq 0) {
        throw "The $Kind package audit did not report an audit source."
    }

    $affectedProjects = @($report.projects | Where-Object {
        $_.PSObject.Properties.Name -contains 'frameworks' -and @($_.frameworks).Count -gt 0
    })

    if ($affectedProjects.Count -gt 0) {
        $output | Write-Host
        throw "The dependency graph contains $Kind packages."
    }

    Write-Host "No $Kind packages were found in direct or transitive dependencies."
}

Invoke-PackageAudit -Kind vulnerable
Invoke-PackageAudit -Kind deprecated
