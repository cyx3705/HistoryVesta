#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projects = @(
    'AppShell.Core'
    'AppShell.Services'
    'AppShell.ServiceHost'
    'AppShell.Shell'
)

$componentRoot = Split-Path -Parent $PSScriptRoot
$violations = @()

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

    if ($entries.Count -ne 0) {
        $violations += "$project : found $($entries.Count) unshipped API entries -> $($entries -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Public API freeze gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'AppShell 3.0.x is frozen and must not add public APIs. Open and review a new version line instead.'
    exit 1
}

Write-Host "Public API freeze gate passed: $($projects.Count) package baselines are unchanged."
