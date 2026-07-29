param(
    [string]$Version,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Publish-Transaction.ps1")

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$PackageRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "z-Package"))
$DeploymentParent = [IO.Path]::GetFullPath("C:\OneHistory\OneHistory-Push")
$DeploymentRoot = [IO.Path]::GetFullPath((Join-Path $DeploymentParent "OneHistoryStudio"))

function Assert-UnderRoot {
    param([string]$Path, [string]$Root, [string]$Name)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name escaped its root: $fullPath"
    }
    return $fullPath
}

function Copy-DirectoryContents {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination $_.Name) -Recurse -Force
    }
}

if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) {
    throw "Formal package root is missing: $PackageRoot"
}

$manifests = @(Get-ChildItem -LiteralPath (Join-Path $PackageRoot "release") -Filter "*.json" -File)
if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($manifests.Count -ne 1) {
        throw "Expected exactly one formal package manifest; found $($manifests.Count)"
    }
    $Version = [IO.Path]::GetFileNameWithoutExtension($manifests[0].Name)
}

Assert-ReleaseTree $PackageRoot $Version "package"

Write-Host "Formal package: $PackageRoot"
Write-Host "Version: $Version"
Write-Host "Deployment target: $DeploymentRoot"
if (-not $Apply) {
    Write-Host "Preview only. Re-run with -Apply after all OneHistoryStudio processes are closed."
    return
}

$running = @(Get-Process -Name "OneHistoryStudio", "OneHistoryStudio.Service" -ErrorAction SilentlyContinue)
if ($running.Count -ne 0) {
    $processes = ($running | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ", "
    throw "Close all OneHistoryStudio processes before deployment: $processes"
}

New-Item -ItemType Directory -Force -Path $DeploymentParent | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$transactionId = [Guid]::NewGuid().ToString("N")
$candidate = Assert-UnderRoot `
    (Join-Path $DeploymentParent "OneHistoryStudio.__new-$transactionId") $DeploymentParent "deployment candidate"

$installedVersion = "unknown"
if (Test-Path -LiteralPath $DeploymentRoot -PathType Container) {
    $installedManifest = @(Get-ChildItem -LiteralPath (Join-Path $DeploymentRoot "release") `
        -Filter "*.json" -File -ErrorAction SilentlyContinue)
    if ($installedManifest.Count -eq 1) {
        $installedVersion = [IO.Path]::GetFileNameWithoutExtension($installedManifest[0].Name)
    }
}
$backup = Assert-UnderRoot `
    (Join-Path $DeploymentParent "OneHistoryStudio-rollback-$installedVersion-$timestamp") `
    $DeploymentParent "deployment backup"
$quarantine = Assert-UnderRoot `
    (Join-Path $DeploymentParent "OneHistoryStudio-failed-$Version-$timestamp-$transactionId") `
    $DeploymentParent "failed deployment"

$applicationDataRoot = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) "OneHistoryStudio"
$databaseCandidates = @(
    (Join-Path (Join-Path $applicationDataRoot "data") "main.db"),
    (Join-Path $applicationDataRoot "main.db")
)
$database = $databaseCandidates | Where-Object {
    Test-Path -LiteralPath $_ -PathType Leaf
} | Select-Object -First 1
if ($null -ne $database) {
    $databaseBackupRoot = Assert-UnderRoot `
        (Join-Path $DeploymentParent "OneHistoryStudio-DatabaseBackups") $DeploymentParent "database backup"
    New-Item -ItemType Directory -Force -Path $databaseBackupRoot | Out-Null
    $databaseBackup = Join-Path $databaseBackupRoot "main-db-predeploy-$Version-$timestamp.db"
    Copy-Item -LiteralPath $database -Destination $databaseBackup
    Write-Host "Database backup: $database -> $databaseBackup"
}

try {
    Copy-DirectoryContents $PackageRoot $candidate
    $validateDeployment = {
        param($Root)
        Assert-ReleaseTree $Root $Version "package"
    }
    Invoke-DirectoryPromotion $candidate $DeploymentRoot $backup $quarantine $validateDeployment
    Write-Host "Deployed OneHistoryStudio $Version to $DeploymentRoot"
    if (Test-Path -LiteralPath $backup) {
        Write-Host "Rollback directory retained at $backup"
    }
    Write-Host "Deployment does not start or restart the application or service."
}
catch {
    $deploymentError = $_
    if ((Test-Path -LiteralPath $candidate) -and (-not (Test-Path -LiteralPath $quarantine))) {
        try {
            Move-Item -LiteralPath $candidate -Destination $quarantine
        }
        catch {
            throw "Deployment failed: $($deploymentError.Exception.Message); candidate quarantine failed: $($_.Exception.Message)"
        }
    }
    throw $deploymentError
}
