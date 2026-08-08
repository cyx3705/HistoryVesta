param(
    [string]$Version,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Publish-Transaction.ps1')

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot '..'))
$PackageRoot = Join-Path $RepoRoot 'z-Package-HistoryJanus'
$appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$Targets = @(
    (Join-Path $appData 'AppShell\Modules\HistoryJanus'),
    (Join-Path $appData 'AppShell\service\Modules\HistoryJanus')
)

if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot 'module.manifest.json') -PathType Leaf)) {
    throw "Formal module package is missing: $PackageRoot"
}
$manifest = [IO.File]::ReadAllText((Join-Path $PackageRoot 'module.manifest.json')) | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = [string]$manifest.version }
Assert-ModulePackage $PackageRoot $Version 'formal'

Write-Host "Formal module: $PackageRoot"
Write-Host "Version: $Version"
foreach ($target in $Targets) { Write-Host "Deployment target: $target" }
if (-not $Apply) {
    Write-Host 'Preview only. Re-run with -Apply while AppShell is closed.'
    return
}

$running = @(Get-Process -Name 'AppShell' -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) {
    $summary = ($running | ForEach-Object { "AppShell($($_.Id))" }) -join ', '
    throw "Close AppShell before module deployment: $summary"
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$transactionId = [Guid]::NewGuid().ToString('N')
$completed = [Collections.Generic.List[object]]::new()

try {
    foreach ($target in $Targets) {
        $parent = Split-Path -Parent $target
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        $candidate = Join-Path $parent "HistoryJanus.__new-$transactionId"
        $backup = Join-Path $parent "HistoryJanus-rollback-$timestamp"
        if (Test-Path -LiteralPath $candidate) { throw "Deployment candidate already exists: $candidate" }
        if (Test-Path -LiteralPath $backup) { throw "Deployment backup already exists: $backup" }
        Copy-Item -LiteralPath $PackageRoot -Destination $candidate -Recurse
        Assert-ModulePackage $candidate $Version 'formal'
        if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }
        try {
            Move-Item -LiteralPath $candidate -Destination $target
            Assert-ModulePackage $target $Version 'formal'
            $completed.Add([pscustomobject]@{ Target = $target; Backup = $backup })
        }
        catch {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }
            throw
        }
    }
}
catch {
    $deploymentError = $_
    for ($index = $completed.Count - 1; $index -ge 0; $index--) {
        $entry = $completed[$index]
        if (Test-Path -LiteralPath $entry.Target) { Remove-Item -LiteralPath $entry.Target -Recurse -Force }
        if (Test-Path -LiteralPath $entry.Backup) { Move-Item -LiteralPath $entry.Backup -Destination $entry.Target }
    }
    throw $deploymentError
}

Write-Host "Deployed HistoryJanus module $Version to both AppShell module slots."
foreach ($entry in $completed) {
    if (Test-Path -LiteralPath $entry.Backup) { Write-Host "Rollback retained: $($entry.Backup)" }
}
Write-Host 'Deployment did not start AppShell and did not modify startup settings.'
