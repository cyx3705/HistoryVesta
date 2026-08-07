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
$CandidateRoot = Join-Path $PublishRoot 'candidate'
$WorkRoot = Join-Path $PublishRoot 'work'
$HistoryRoot = Join-Path $PublishRoot 'history'
$QuarantineRoot = Join-Path $PublishRoot 'quarantine'
$PackageRoot = Join-Path $RepoRoot 'z-Package-OneHistoryStudio'
$ModuleProject = Join-Path $ComponentRoot 'Module\OneHistoryStudio.Module.csproj'
$ModuleManifestSource = Join-Path $ComponentRoot 'Module\module.manifest.json'
$ApiDocumentCandidates = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'b-Office\package') -Filter '*.md' -File -ErrorAction SilentlyContinue)
if ($ApiDocumentCandidates.Count -ne 1) { throw 'b-Office/package must contain exactly one API Markdown document' }
$ApiDocumentSource = $ApiDocumentCandidates[0].FullName
$ApiDocumentName = $ApiDocumentCandidates[0].Name
$AppShellPackageRoot = 'C:\OneHistory\OneHistory-Projects\2026-023-AppShell\z-Package-AppShell'

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-VersionProperties {
    $output = & dotnet msbuild $ModuleProject -nologo -getProperty:OneHistoryStudioVersion -getProperty:AssemblyVersion
    if ($LASTEXITCODE -ne 0) { throw 'Unable to evaluate OHS version source' }
    return (($output -join "`n") | ConvertFrom-Json).Properties
}

function New-ModulePackage {
    param(
        [string]$OutputRoot,
        [string]$BuildOutput,
        [string]$Channel,
        [string]$SourceCommit,
        [bool]$SourceDirty,
        [string]$AppShellVersion,
        [string]$AppShellManifestSha256
    )

    New-Item -ItemType Directory -Force -Path (Join-Path $OutputRoot 'package') | Out-Null
    Copy-Item -LiteralPath (Join-Path $BuildOutput 'OneHistoryStudio.dll') -Destination $OutputRoot
    Copy-Item -LiteralPath (Join-Path $BuildOutput 'OneHistoryStudio.xml') -Destination $OutputRoot
    Copy-Item -LiteralPath $ApiDocumentSource -Destination (Join-Path $OutputRoot ('package\' + $ApiDocumentName))

    $manifest = [IO.File]::ReadAllText($ModuleManifestSource) | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName channel -NotePropertyValue $Channel -Force
    $manifest | Add-Member -NotePropertyName sourceCommit -NotePropertyValue $SourceCommit -Force
    $manifest | Add-Member -NotePropertyName sourceDirty -NotePropertyValue $SourceDirty -Force
    $manifest | Add-Member -NotePropertyName appShellVersion -NotePropertyValue $AppShellVersion -Force
    $manifest | Add-Member -NotePropertyName appShellManifestSha256 -NotePropertyValue $AppShellManifestSha256 -Force
    $manifest | Add-Member -NotePropertyName publishedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
    [IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'module.manifest.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))

    $relativeFiles = @('OneHistoryStudio.dll', 'OneHistoryStudio.xml', 'module.manifest.json', "package/$ApiDocumentName")
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
$sourceVersion = [string]$properties.OneHistoryStudioVersion
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $sourceVersion }
if ($Version -ne $sourceVersion) { throw "StudioVersion.props declares $sourceVersion; requested $Version" }

$sourceManifest = [IO.File]::ReadAllText($ModuleManifestSource) | ConvertFrom-Json
if ([string]$sourceManifest.version -ne $Version) {
    throw "Module manifest declares $($sourceManifest.version); expected $Version"
}
if (-not (Test-Path -LiteralPath $ApiDocumentSource -PathType Leaf)) {
    throw "Module API document is missing: $ApiDocumentSource"
}

$appShellManifestPath = Join-Path $AppShellPackageRoot 'manifest.json'
$appShellChecksumPath = Join-Path $AppShellPackageRoot 'SHA256SUMS'
if (-not (Test-Path -LiteralPath $appShellManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $appShellChecksumPath -PathType Leaf)) {
    throw "AppShell formal snapshot is incomplete: $AppShellPackageRoot"
}
$appShellManifest = [IO.File]::ReadAllText($appShellManifestPath) | ConvertFrom-Json
$appShellVersion = [string]$appShellManifest.version
$appShellManifestSha256 = (Get-FileHash -LiteralPath $appShellManifestPath -Algorithm SHA256).Hash
if ([version]$appShellVersion -lt [version]'3.1.9') {
    throw "OHS $Version requires AppShell 3.1.9 or newer; found $appShellVersion"
}

$sourcePaths = @('b-Code-Studio', 'b-Code-Verify', 'b-Office', 'README.md', '.gitattributes', '.gitignore', 'OHS.sln')
$sourceStatus = (& git -C $RepoRoot status --porcelain -- @sourcePaths) -join "`n"
$sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
if ($Publish -and $sourceDirty) {
    throw "Formal publish requires committed source and documents:`n$sourceStatus"
}
$sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()

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
    New-Item -ItemType Directory -Force -Path $HistoryRoot | Out-Null

    Invoke-Dotnet @('restore', 'OHS.sln', '--locked-mode', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'OHS.sln', '-c', 'Debug', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('build', 'OHS.sln', '-c', 'Release', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('test', 'b-Code-Verify\Contracts\Contracts.csproj', '-c', 'Debug', '--no-build', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('test', 'b-Code-Verify\Contracts\Contracts.csproj', '-c', 'Release', '--no-build', '--no-restore', '-p:NuGetAudit=false')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\Smoke\Smoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--')
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\Smoke\Smoke.csproj', '-c', 'Release', '--no-build', '--no-restore', '--')

    $debugModule = Join-Path $ComponentRoot 'Module\bin\Debug\net8.0-windows'
    $releaseModule = Join-Path $ComponentRoot 'Module\bin\Release\net8.0-windows'
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj', '-c', 'Debug', '--no-build', '--no-restore', '--', $debugModule)
    Invoke-Dotnet @('run', '--project', 'b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj', '-c', 'Release', '--no-build', '--no-restore', '--', $releaseModule)

    New-ModulePackage $candidateNew $releaseModule 'candidate' $sourceCommit $sourceDirty $appShellVersion $appShellManifestSha256
    Assert-ModulePackage $candidateNew $Version 'candidate'
    $validateCandidate = { param($Root) Assert-ModulePackage $Root $Version 'candidate' }
    Invoke-DirectoryPromotion $candidateNew $CandidateRoot $candidateBackup $candidateFailed $validateCandidate
    if (Test-Path -LiteralPath $candidateBackup) { Remove-Item -LiteralPath $candidateBackup -Recurse -Force }

    if ($Publish) {
        New-ModulePackage $formalNew $releaseModule 'formal' $sourceCommit $false $appShellVersion $appShellManifestSha256
        Assert-ModulePackage $formalNew $Version 'formal'
        $installedVersion = 'none'
        if (Test-Path -LiteralPath (Join-Path $PackageRoot 'module.manifest.json')) {
            try { $installedVersion = [string](([IO.File]::ReadAllText((Join-Path $PackageRoot 'module.manifest.json')) | ConvertFrom-Json).version) } catch { }
        }
        $formalBackup = Join-Path $HistoryRoot "package-$installedVersion-$archiveStamp"
        $validateFormal = { param($Root) Assert-ModulePackage $Root $Version 'formal' }
        Invoke-DirectoryPromotion $formalNew $PackageRoot $formalBackup $formalFailed $validateFormal
        Write-Host "Formal OneHistoryStudio module ${Version}: $PackageRoot"
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
