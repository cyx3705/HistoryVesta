param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Publish-Transaction.ps1')

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot '..'))
$PublishRoot = Join-Path $RepoRoot 'b-Publish'
$CandidateRoot = Join-Path $PublishRoot 'current\HistoryJanus'
$WorkRoot = Join-Path $PublishRoot 'work'
$HistoryRoot = Join-Path $PublishRoot 'history'
$QuarantineRoot = Join-Path $PublishRoot 'quarantine'
$PackageRoot = Join-Path $RepoRoot 'z-HistoryJanus'
$ModuleProject = Join-Path $ComponentRoot 'Module\HistoryJanus.Module.csproj'
$ModuleManifestSource = Join-Path $ComponentRoot 'Module\module.manifest.json'
$ApiDocumentCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'b-Office\package') -Filter '*.md' -File -ErrorAction SilentlyContinue)
if ($ApiDocumentCandidates.Count -ne 1) { throw 'b-Office/package must contain exactly one API Markdown document' }
$ApiDocumentSource = $ApiDocumentCandidates[0].FullName
$ApiDocumentName = $ApiDocumentCandidates[0].Name
$HistoryVulcanPackageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\2026-023-HistoryVulcan\z-HistoryVulcan'))

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-VersionProperties {
    $output = & dotnet msbuild $ModuleProject -nologo -getProperty:HistoryJanusVersion -getProperty:AssemblyVersion
    if ($LASTEXITCODE -ne 0) { throw 'Unable to evaluate Janus version source' }
    return (($output -join "`n") | ConvertFrom-Json).Properties
}

function New-ModulePackage {
    param(
        [string]$OutputRoot,
        [string]$BuildOutput
    )

    New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'docs') | Out-Null
    Copy-Item -LiteralPath (Join-Path $BuildOutput 'HistoryJanus.dll') -Destination $OutputRoot
    Copy-Item -LiteralPath (Join-Path $BuildOutput 'HistoryJanus.xml') -Destination $OutputRoot
    Copy-Item -LiteralPath $ApiDocumentSource -Destination (Join-Path $OutputRoot ('docs\' + $ApiDocumentName))

    Copy-Item -LiteralPath $ModuleManifestSource -Destination (Join-Path $OutputRoot 'module.manifest.json')

    $relativeFiles = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', "docs/$ApiDocumentName")
    $checksumLines = foreach ($relative in $relativeFiles) {
        $path = Join-Path $OutputRoot $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
        "$(Get-FileHash -LiteralPath $path -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $OutputRoot 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
}

$properties = Get-VersionProperties
$sourceVersion = [string]$properties.HistoryJanusVersion
# 宿主契约版本与产品版本同源，都来自 JanusVersion.props，不在脚本内硬编码。
$requiredVulcan = [string]$properties.RequiredHistoryVulcanVersion
if ([string]::IsNullOrWhiteSpace($requiredVulcan)) {
    throw "JanusVersion.props does not declare RequiredHistoryVulcanVersion"
}
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) { throw "JanusVersion.props declares $sourceVersion; requested $Version" }

$sourceManifest = [IO.File]::ReadAllText($ModuleManifestSource) | ConvertFrom-Json
if ([string]$sourceManifest.version -ne $Version) {
    throw "Module manifest declares $($sourceManifest.version); expected $Version"
}
if (-not (Test-Path -LiteralPath $ApiDocumentSource -PathType Leaf)) {
    throw "Module API document is missing: $ApiDocumentSource"
}

$historyVulcanManifestPath = Join-Path $HistoryVulcanPackageRoot 'manifest.json'
$historyVulcanCorePath = Join-Path $HistoryVulcanPackageRoot 'host\HistoryVulcan.Core.dll'
if (-not (Test-Path -LiteralPath $historyVulcanManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $historyVulcanCorePath -PathType Leaf)) {
    throw "HistoryVulcan formal snapshot is incomplete: $HistoryVulcanPackageRoot"
}
$historyVulcanManifest = [IO.File]::ReadAllText($historyVulcanManifestPath) | ConvertFrom-Json
if ([string]$historyVulcanManifest.product -ne 'HistoryVulcan' -or [string]$historyVulcanManifest.version -ne $requiredVulcan) {
    throw "Janus $Version requires the HistoryVulcan $requiredVulcan formal host; found $($historyVulcanManifest.version)"
}
$historyVulcanCore = [Reflection.AssemblyName]::GetAssemblyName($historyVulcanCorePath)
if ($historyVulcanCore.Version.ToString() -ne "$requiredVulcan.0") {
    throw "HistoryVulcan.Core identity is $($historyVulcanCore.Version), expected $requiredVulcan.0"
}

$sourcePaths = @('b-Code-Studio', 'b-Code-Verify', 'b-Office', 'README.md', '.gitattributes', '.gitignore', 'HistoryJanus.sln')
$sourceStatus = (& git -C $RepoRoot status --porcelain -- @sourcePaths) -join "`n"
$sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
if ($Publish -and $sourceDirty) {
    throw "Formal publish requires committed source and documents:`n$sourceStatus"
}
$transactionId = [Guid]::NewGuid().ToString('N')
$buildRoot = Join-Path $WorkRoot "build-$Version-$transactionId"
$candidateNew = Join-Path $WorkRoot "candidate-$transactionId"
$candidateBackup = Join-Path $WorkRoot "candidate-previous-$transactionId"
$candidateFailed = Join-Path $QuarantineRoot "candidate-failed-$transactionId"
$formalNew = Join-Path $WorkRoot "formal-$transactionId"
$formalFailed = Join-Path $QuarantineRoot "formal-failed-$transactionId"
$archiveStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$succeeded = $false

Push-Location $RepoRoot
try {
    foreach ($path in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $HistoryRoot, (Split-Path -Parent $CandidateRoot) | Out-Null

    Invoke-Dotnet @('restore', 'HistoryJanus.sln', '--locked-mode', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'HistoryJanus.sln', '-c', 'Debug', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'HistoryJanus.sln', '-c', 'Release', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('test', 'b-Code-Verify\Contracts\Contracts.csproj', '-c', 'Debug', '--no-build', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('test', 'b-Code-Verify\Contracts\Contracts.csproj', '-c', 'Release', '--no-build', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\Smoke\Smoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\Smoke\Smoke.csproj', '-c', 'Release', '--no-build', '--no-restore', '--')

    $debugModule = Join-Path $ComponentRoot 'Module\bin\Debug\net8.0-windows'
    $releaseModule = Join-Path $ComponentRoot 'Module\bin\Release\net8.0-windows'
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--', $debugModule)
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj', '-c', 'Release', '--no-build', '--no-restore', '--', $releaseModule)

    New-ModulePackage $candidateNew $releaseModule
    Assert-ModulePackage $candidateNew $Version
    $validateCandidate = { param($Root) Assert-ModulePackage $Root $Version }
    Invoke-DirectoryPromotion $candidateNew $CandidateRoot $candidateBackup $candidateFailed $validateCandidate
    if (Test-Path -LiteralPath $candidateBackup) { Remove-Item -LiteralPath $candidateBackup -Recurse -Force }

    if ($Publish) {
        New-ModulePackage $formalNew $releaseModule
        Assert-ModulePackage $formalNew $Version
        $installedVersion = 'none'
        if (Test-Path -LiteralPath (Join-Path $PackageRoot 'module.manifest.json')) {
            try { $installedVersion = [string](([IO.File]::ReadAllText((Join-Path $PackageRoot 'module.manifest.json')) | ConvertFrom-Json).version) } catch { }
        }
        $formalBackup = Join-Path $HistoryRoot "package-$installedVersion-$archiveStamp"
        $validateFormal = { param($Root) Assert-ModulePackage $Root $Version }
        Invoke-DirectoryPromotion $formalNew $PackageRoot $formalBackup $formalFailed $validateFormal
        Write-Host "Formal HistoryJanus module ${Version}: $PackageRoot"
        if (Test-Path -LiteralPath $formalBackup) { Write-Host "Previous formal package: $formalBackup" }
    }
    else {
        Write-Host "Verified candidate: $CandidateRoot"
    }
    $succeeded = $true
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    if ($succeeded) {
        foreach ($path in @($WorkRoot, $QuarantineRoot)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
}
