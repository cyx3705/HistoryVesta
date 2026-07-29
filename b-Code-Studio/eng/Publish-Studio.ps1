param(
    [string]$Version,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Publish-Transaction.ps1")

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$StageParent = [IO.Path]::GetFullPath((Join-Path $RepoRoot "stage"))
$StagingRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "b-Publish"))
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
$StageRoot = Assert-UnderRoot `
    (Join-Path $StageParent "ohs-$Version-$transactionId") $StageParent "candidate path"
$AppRoot = Join-Path $StageRoot "app"
$ManifestPath = Join-Path $StageRoot "$Version.json"
$ChecksumPath = Join-Path $StageRoot "$Version.sha256"
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
    if (Test-Path -LiteralPath $StageRoot) {
        throw "Refusing to overwrite immutable staging directory: $StageRoot"
    }

    $sourceStatus = (& git -C $RepoRoot status --porcelain -- b-Code-Studio b-Code-Studio.Service b-Office) -join "`n"
    $sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
    if ($Publish -and $sourceDirty) {
        throw "Formal publish requires clean b-Code-Studio, b-Code-Studio.Service and b-Office source trees."
    }

    Invoke-Dotnet @( "restore", "OHS.sln", "--locked-mode", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "build", "OHS.sln", "-c", "Debug", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "build", "OHS.sln", "-c", "Release", "--no-restore", "-p:NuGetAudit=false" )
    Invoke-Dotnet @( "run", "--project", "b-Code-Studio\tests\Smoke\Smoke.csproj", "-c", "Debug", "--no-build", "--no-restore", "--" )
    Invoke-Dotnet @( "run", "--project", "b-Code-Studio\tests\Smoke\Smoke.csproj", "-c", "Release", "--no-build", "--no-restore", "--" )

    New-Item -ItemType Directory -Force -Path $AppRoot | Out-Null
    Invoke-Dotnet @( "publish", "b-Code-Studio\Studio.csproj", "-c", "Release", "-r", "win-x64",
        "--self-contained", "false", "-o", $AppRoot, "--no-restore" )

    $metaRoot = Join-Path $RepoRoot "b-Office\meta"
    $manualCandidates = @(Get-ChildItem -LiteralPath $metaRoot -Filter "*.md" -File | Where-Object {
        Select-String -LiteralPath $_.FullName -SimpleMatch "<!-- command-count:" -Quiet
    })
    if ($manualCandidates.Count -ne 1) {
        throw "Expected exactly one generated command manual in b-Office/meta; found $($manualCandidates.Count)"
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
        throw "Generated command manual differs from the current b-Office/meta source"
    }

    $exe = Join-Path $AppRoot "OneHistoryStudio.exe"
    Assert-File $exe
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($fileVersion -ne $ExpectedFileVersion) {
        throw "OHS file version is $fileVersion, expected $ExpectedFileVersion"
    }
    [xml]$studioProject = Get-Content -LiteralPath (Join-Path $ComponentRoot "Studio.csproj") -Raw
    $documentMappings = @($studioProject.SelectNodes("/Project/ItemGroup/Content") | Where-Object {
        $_.Include -like "*b-Office\meta\*.md" -and $_.Link -like "docs\*.md"
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
        channel = "staging"
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

    $publishParent = Split-Path -Parent $StagingRoot
    $temporaryStaging = Assert-UnderRoot `
        (Join-Path $publishParent "b-Publish.__new-$transactionId") $publishParent "temporary staging"
    $backupStaging = Assert-UnderRoot `
        (Join-Path $StageParent "b-Publish-pre-$Version-$transactionId") $StageParent "backup staging"
    $quarantineStaging = Assert-UnderRoot `
        (Join-Path $StageParent "b-Publish-failed-$Version-$transactionId") $StageParent "failed staging"
    try {
        Copy-DirectoryContents $AppRoot $temporaryStaging
        $temporaryMetadata = Join-Path $temporaryStaging "release"
        New-Item -ItemType Directory -Force -Path $temporaryMetadata | Out-Null
        Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $temporaryMetadata "$Version.json")
        Copy-Item -LiteralPath $ChecksumPath -Destination (Join-Path $temporaryMetadata "$Version.sha256")
        $validateStaging = {
            param($Root)
            Assert-ReleaseTree $Root $Version "staging"
        }
        Invoke-DirectoryPromotion `
            $temporaryStaging $StagingRoot $backupStaging $quarantineStaging $validateStaging
        Write-Host "Staged OneHistoryStudio $Version at $StagingRoot"
        if (Test-Path -LiteralPath $backupStaging) {
            Write-Host "Previous staging retained at $backupStaging"
        }
        Remove-Item -LiteralPath $StageRoot -Recurse -Force
    }
    catch {
        $stagingError = $_
        if ((Test-Path -LiteralPath $temporaryStaging) -and
            (-not (Test-Path -LiteralPath $quarantineStaging))) {
            try {
                Move-Item -LiteralPath $temporaryStaging -Destination $quarantineStaging
            }
            catch {
                throw "Staging failed: $($stagingError.Exception.Message); candidate quarantine failed: $($_.Exception.Message)"
            }
        }
        throw $stagingError
    }

    if ($Publish) {
        $temporaryPackage = Assert-UnderRoot `
            (Join-Path $publishParent "z-Package.__new-$transactionId") $publishParent "temporary package"
        $backupPackage = Assert-UnderRoot `
            (Join-Path $StageParent "z-Package-pre-$Version-$transactionId") $StageParent "backup package"
        $quarantinePackage = Assert-UnderRoot `
            (Join-Path $StageParent "z-Package-failed-$Version-$transactionId") $StageParent "failed package"
        try {
            Copy-DirectoryContents $StagingRoot $temporaryPackage
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
}
finally {
    & dotnet build-server shutdown | Out-Null
    Pop-Location
    if ($mutexAcquired) {
        $publishMutex.ReleaseMutex()
    }
    $publishMutex.Dispose()
}
