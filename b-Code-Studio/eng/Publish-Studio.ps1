param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Publish-Transaction.ps1")

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$PublishRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "b-Publish"))
$CandidateRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "candidate"))
$LegacyStagingRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "current"))
$WorkRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "work"))
$HistoryRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "history"))
$QuarantineRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "quarantine"))
$PackageRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "z-Package"))

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-StudioVersionProperties {
    $project = Join-Path $ComponentRoot "Studio.csproj"
    $output = & dotnet msbuild $project -nologo `
        -getProperty:OneHistoryStudioVersion -getProperty:FileVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to evaluate the OHS version source"
    }

    try {
        return (($output -join "`n") | ConvertFrom-Json).Properties
    }
    catch {
        throw "OHS version evaluation returned invalid JSON: $($output -join ' ')"
    }
}

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

function Assert-File {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file was not produced: $Path"
    }
}

function Get-RelativeArtifactPath {
    param([string]$Path, [string]$Root)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact escaped the publish root: $fullPath"
    }
    $relative = $fullPath.Substring($fullRoot.Length)
    return $relative.Replace([IO.Path]::DirectorySeparatorChar, '/')
}

function Copy-DirectoryContents {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination $_.Name) -Recurse -Force
    }
}

function Get-ReleaseVersionTag {
    param([string]$Root)

    $metadataRoot = Join-Path $Root "release"
    if (-not (Test-Path -LiteralPath $metadataRoot -PathType Container)) {
        return "unknown"
    }
    $manifests = @(Get-ChildItem -LiteralPath $metadataRoot -Filter "*.json" -File)
    if ($manifests.Count -ne 1) {
        return "unknown"
    }
    try {
        $version = [string](([IO.File]::ReadAllText($manifests[0].FullName) | ConvertFrom-Json).version)
    }
    catch {
        return "unknown"
    }
    if ([string]::IsNullOrWhiteSpace($version)) {
        return "unknown"
    }
    return [Text.RegularExpressions.Regex]::Replace($version, '[^A-Za-z0-9._-]', '_')
}

$VersionProperties = Get-StudioVersionProperties
$SourceVersion = [string]$VersionProperties.OneHistoryStudioVersion
$ExpectedFileVersion = [string]$VersionProperties.FileVersion
if ([string]::IsNullOrWhiteSpace($SourceVersion) -or
    [string]::IsNullOrWhiteSpace($ExpectedFileVersion)) {
    throw "OneHistoryStudioVersion or FileVersion evaluated to an empty value"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $SourceVersion
}
elseif ($Version -ne $SourceVersion) {
    throw "StudioVersion.props declares $SourceVersion; requested $Version"
}

$transactionId = [Guid]::NewGuid().ToString("N")
$archiveStamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$BuildRoot = Assert-UnderRoot `
    (Join-Path $WorkRoot "build-$Version-$transactionId") $WorkRoot "build path"
$AppRoot = Join-Path $BuildRoot "app"
$ManifestPath = Join-Path $BuildRoot "$Version.json"
$ChecksumPath = Join-Path $BuildRoot "$Version.sha256"
$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexHash = (($sha256.ComputeHash($mutexInput) | ForEach-Object { $_.ToString("X2") }) -join "").Substring(0, 16)
}
finally {
    $sha256.Dispose()
}
$publishMutex = [Threading.Mutex]::new($false, "Local\OneHistoryStudio.Publish.$mutexHash")
$mutexAcquired = $false
$publishSucceeded = $false
try {
    $mutexAcquired = $publishMutex.WaitOne(0)
}
catch [Threading.AbandonedMutexException] {
    $mutexAcquired = $true
}
if (-not $mutexAcquired) {
    $publishMutex.Dispose()
    throw "Another OHS publish is already running for $ComponentRoot"
}

Push-Location $RepoRoot
try {
    foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
        if (Test-Path -LiteralPath $transientRoot) {
            Remove-Item -LiteralPath $transientRoot -Recurse -Force
        }
    }
    foreach ($directory in @($PublishRoot, $WorkRoot, $HistoryRoot, $QuarantineRoot)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    if (Test-Path -LiteralPath $LegacyStagingRoot) {
        Remove-Item -LiteralPath $LegacyStagingRoot -Recurse -Force
        Write-Host "Removed legacy b-Publish/current staging slot"
    }
    if (Test-Path -LiteralPath $BuildRoot) {
        throw "Refusing to overwrite immutable build directory: $BuildRoot"
    }

    [string[]]$sourceStatusArguments = @(
        "-C", $RepoRoot, "status", "--porcelain", "--",
        "b-Code-Studio", "b-Code-Verify", "b-Office",
        ":(exclude,glob)b-Office/*.txt"
    )
    $sourceStatus = (& git @sourceStatusArguments) -join "`n"
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
    if ($Publish -and $sourceDirty) {
        throw "Formal publish requires clean b-Code-Studio, b-Code-Verify, and b-Office source trees: $sourceStatus"
    }

    Invoke-Dotnet @( "restore", "OHS.sln", "--locked-mode", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "build", "OHS.sln", "-c", "Debug", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "build", "OHS.sln", "-c", "Release", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "test", "b-Code-Verify\Contracts\Contracts.csproj", "-c", "Debug",
        "--no-build", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "test", "b-Code-Verify\Contracts\Contracts.csproj", "-c", "Release",
        "--no-build", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "run", "--project", "b-Code-Verify\Smoke\Smoke.csproj", "-c", "Debug", "--no-build", "--no-restore", "--" )
    Invoke-Dotnet @( "run", "--project", "b-Code-Verify\Smoke\Smoke.csproj", "-c", "Release", "--no-build", "--no-restore", "--" )

    New-Item -ItemType Directory -Force -Path $AppRoot | Out-Null
    Invoke-Dotnet @( "publish", "b-Code-Studio\Studio.csproj", "-c", "Release", "-r", "win-x64",
        "--self-contained", "false", "-o", $AppRoot, "--no-restore" )

    $DocumentationPackageRoot = Join-Path $RepoRoot "b-Office\package"
    $manualCandidates = @(Get-ChildItem -LiteralPath $DocumentationPackageRoot -Filter "*.md" -File | Where-Object {
        Select-String -LiteralPath $_.FullName -SimpleMatch "<!-- command-count:" -Quiet
    })
    if ($manualCandidates.Count -ne 1) {
        throw "Expected exactly one generated command manual in b-Office/package; found $($manualCandidates.Count)"
    }
    $sourceManual = $manualCandidates[0].FullName
    $manualPath = Join-Path (Join-Path $AppRoot "docs") $manualCandidates[0].Name
    Invoke-Dotnet @( "run", "--project", "b-Code-Studio\Studio.csproj", "-c", "Release", "--no-build", "--no-restore",
        "--", "--generate-manual", $manualPath )
    Assert-File $sourceManual
    Assert-File $manualPath
    $sourceManualHash = (Get-FileHash -LiteralPath $sourceManual -Algorithm SHA256).Hash
    $publishedManualHash = (Get-FileHash -LiteralPath $manualPath -Algorithm SHA256).Hash
    if ($sourceManualHash -ne $publishedManualHash) {
        throw "Generated command manual differs from the b-Office/package source"
    }

    $exe = Join-Path $AppRoot "OneHistoryStudio.exe"
    Assert-File $exe
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($fileVersion -ne $ExpectedFileVersion) {
        throw "OHS file version is $fileVersion, expected $ExpectedFileVersion"
    }
    [xml]$studioProject = Get-Content -LiteralPath (Join-Path $ComponentRoot "Studio.csproj") -Raw
    $documentMappings = @($studioProject.SelectNodes("/Project/ItemGroup/Content") | Where-Object {
        $_.Include -like "*b-Office\current\*.md" -or $_.Include -like "*b-Office\package\*.md"
    } | Where-Object {
        $_.Link -like "docs\*.md"
    })
    if ($documentMappings.Count -ne 6) {
        throw "Studio.csproj must publish exactly six current Help documents"
    }
    foreach ($mapping in $documentMappings) {
        Assert-File (Join-Path $AppRoot ([string]$mapping.Link))
    }

    $artifactFiles = @(Get-ChildItem -LiteralPath $AppRoot -Recurse -File | Sort-Object FullName)
    if ($artifactFiles.Count -eq 0) {
        throw "OHS publish produced no files"
    }
    $checksumLines = foreach ($file in $artifactFiles) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$hash  $(Get-RelativeArtifactPath $file.FullName $AppRoot)"
    }
    [IO.File]::WriteAllLines($ChecksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $manifestArtifacts = foreach ($file in $artifactFiles) {
        [ordered]@{
            file = Get-RelativeArtifactPath $file.FullName $AppRoot
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "OneHistoryStudio"
        version = $Version
        channel = "candidate"
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        sdk = (& dotnet --version).Trim()
        targetFramework = "net8.0-windows"
        runtime = "win-x64"
        selfContained = $false
        commandManualSha256 = $publishedManualHash
        artifacts = @($manifestArtifacts)
    }
    [IO.File]::WriteAllText($ManifestPath, (($manifest | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))

    $temporaryCandidate = Assert-UnderRoot `
        (Join-Path $WorkRoot "candidate-new-$transactionId") $WorkRoot "temporary candidate"
    $previousCandidateVersion = Get-ReleaseVersionTag $CandidateRoot
    $backupCandidate = Assert-UnderRoot `
        (Join-Path $WorkRoot "candidate-previous-$previousCandidateVersion-$transactionId") `
        $WorkRoot "candidate rollback"
    $quarantineCandidate = Assert-UnderRoot `
        (Join-Path $QuarantineRoot "candidate-failed-$transactionId") `
        $QuarantineRoot "failed candidate"
    try {
        Copy-DirectoryContents $AppRoot $temporaryCandidate
        $temporaryMetadata = Join-Path $temporaryCandidate "release"
        New-Item -ItemType Directory -Force -Path $temporaryMetadata | Out-Null
        Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $temporaryMetadata "$Version.json")
        Copy-Item -LiteralPath $ChecksumPath -Destination (Join-Path $temporaryMetadata "$Version.sha256")
        $validateCandidate = {
            param($Root)
            Assert-ReleaseTree $Root $Version "candidate"
        }
        Invoke-DirectoryPromotion `
            $temporaryCandidate $CandidateRoot $backupCandidate $quarantineCandidate $validateCandidate
        Write-Host "Validated OneHistoryStudio $Version candidate at $CandidateRoot"
        if (Test-Path -LiteralPath $backupCandidate) {
            Remove-Item -LiteralPath $backupCandidate -Recurse -Force
            Write-Host "Previous candidate discarded after successful replacement"
        }
        Remove-Item -LiteralPath $BuildRoot -Recurse -Force
    }
    catch {
        $candidateError = $_
        if ((Test-Path -LiteralPath $temporaryCandidate) -and
            (-not (Test-Path -LiteralPath $quarantineCandidate))) {
            try {
                Move-Item -LiteralPath $temporaryCandidate -Destination $quarantineCandidate
            }
            catch {
                throw "Candidate failed: $($candidateError.Exception.Message); candidate quarantine failed: $($_.Exception.Message)"
            }
        }
        throw $candidateError
    }

    if ($Publish) {
        $temporaryPackage = Assert-UnderRoot `
            (Join-Path $WorkRoot "package-new-$transactionId") $WorkRoot "temporary package"
        $previousPackageVersion = Get-ReleaseVersionTag $PackageRoot
        $backupPackage = Assert-UnderRoot `
            (Join-Path $HistoryRoot "package-$previousPackageVersion-$archiveStamp-$transactionId") `
            $HistoryRoot "package history"
        $quarantinePackage = Assert-UnderRoot `
            (Join-Path $QuarantineRoot "package-failed-$Version-$archiveStamp-$transactionId") `
            $QuarantineRoot "failed package"
        try {
            Copy-DirectoryContents $CandidateRoot $temporaryPackage
            $packageManifestPath = Join-Path (Join-Path $temporaryPackage "release") "$Version.json"
            $packageManifest = [IO.File]::ReadAllText($packageManifestPath) | ConvertFrom-Json
            $packageManifest.channel = "package"
            $packageManifest | Add-Member -NotePropertyName packagedAtUtc `
                -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString("O")) -Force
            [IO.File]::WriteAllText(
                $packageManifestPath,
                (($packageManifest | ConvertTo-Json -Depth 8) + "`n"),
                [Text.UTF8Encoding]::new($false))
            $validatePackage = {
                param($Root)
                Assert-ReleaseTree $Root $Version "package"
            }
            Invoke-DirectoryPromotion `
                $temporaryPackage $PackageRoot $backupPackage $quarantinePackage $validatePackage
            Write-Host "Published OneHistoryStudio $Version package to $PackageRoot"
            if (Test-Path -LiteralPath $backupPackage) {
                Write-Host "Previous package retained at $backupPackage"
            }
        }
        catch {
            $packageError = $_
            if ((Test-Path -LiteralPath $temporaryPackage) -and
                (-not (Test-Path -LiteralPath $quarantinePackage))) {
                try {
                    Move-Item -LiteralPath $temporaryPackage -Destination $quarantinePackage
                }
                catch {
                    throw "Package publish failed: $($packageError.Exception.Message); candidate quarantine failed: $($_.Exception.Message)"
                }
            }
            throw $packageError
        }
    }
    $publishSucceeded = $true
}
catch {
    $publishError = $_
    if (Test-Path -LiteralPath $BuildRoot) {
        $failedBuild = Assert-UnderRoot `
            (Join-Path $QuarantineRoot "build-failed-$Version-$archiveStamp-$transactionId") `
            $QuarantineRoot "failed build"
        try {
            Move-Item -LiteralPath $BuildRoot -Destination $failedBuild
        }
        catch {
            throw "Publish failed: $($publishError.Exception.Message); build quarantine failed: $($_.Exception.Message)"
        }
    }
    throw $publishError
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    if ($publishSucceeded) {
        foreach ($transientRoot in @($WorkRoot, $QuarantineRoot)) {
            if (Test-Path -LiteralPath $transientRoot) {
                Remove-Item -LiteralPath $transientRoot -Recurse -Force
            }
        }
    }
    if ($mutexAcquired) {
        $publishMutex.ReleaseMutex()
    }
    $publishMutex.Dispose()
}
