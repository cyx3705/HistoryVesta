#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projects = @(
    'HistoryVulcan.Core'
    'HistoryVulcan.Services'
    'HistoryVulcan.ServiceHost'
    'HistoryVulcan.Shell'
)

$componentRoot = Split-Path -Parent $PSScriptRoot
$violations = @()
$versionProps = Get-Content -LiteralPath (Join-Path $componentRoot 'VulcanVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($versionProps, '<VulcanVersion>(?<version>[^<]+)</VulcanVersion>')
if (-not $versionMatch.Success) {
    throw 'VulcanVersion.props does not contain VulcanVersion'
}
$version = $versionMatch.Groups['version'].Value
$approved = @{
    'HistoryVulcan.Core' = @()
    'HistoryVulcan.Services' = @()
    'HistoryVulcan.ServiceHost' = @()
    'HistoryVulcan.Shell' = @()
}

if ($version -eq '3.2.1') {
    $approved['HistoryVulcan.Core'] = @(
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void'
    )
    $approved['HistoryVulcan.Shell'] = @(
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void'
    )
}

foreach ($project in $projects) {
    $path = Join-Path $componentRoot "src\$project\PublicAPI.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $violations += "$project : PublicAPI.Unshipped.txt missing"
        continue
    }

    $entries = @(
        Get-Content -LiteralPath $path -Encoding UTF8 |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )

    $difference = @(Compare-Object -ReferenceObject @($approved[$project]) -DifferenceObject $entries)
    if ($difference.Count -ne 0) {
        $violations += "$project : unshipped API differs from approved $version baseline -> $($difference -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Public API freeze gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Public API changes must exactly match the version-approved Unshipped baseline.'
    exit 1
}

Write-Host "Public API baseline gate passed: $($projects.Count) package Unshipped files match the approved $version baseline."
