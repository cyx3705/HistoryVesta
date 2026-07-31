param(
    [string]$Version,
    [switch]$Publish,
    [switch]$VirtualPublish,
    [switch]$DeployToZ
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($Publish -and $VirtualPublish) {
    throw "-Publish and -VirtualPublish are mutually exclusive"
}
if ($DeployToZ -and -not $VirtualPublish) {
    throw "-DeployToZ requires -VirtualPublish"
}

$ComponentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $ComponentRoot ".."))
$PublishRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "b-Publish"))
$FormalRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "z-Package-AppShell"))
$OfficeRoot = Join-Path $RepoRoot "b-Office"
$PackageDocumentRoot = Join-Path $OfficeRoot "package"
$ReleaseDocumentRoot = Join-Path $ComponentRoot "eng\release"
$ConsumerDocumentManifest = Join-Path $ReleaseDocumentRoot "consumer-docs.json"
$ReuseDocumentTemplate = Join-Path $ReleaseDocumentRoot "AppShell.reuse.template.md"
$CurrentSnapshotReadmeTemplate = Join-Path $ReleaseDocumentRoot "CurrentSnapshot.README.template.md"
$ConsumerDocumentConfig = Get-Content -LiteralPath $ConsumerDocumentManifest -Raw -Encoding UTF8 |
    ConvertFrom-Json
if ($ConsumerDocumentConfig.schemaVersion -ne 1) {
    throw "Unsupported consumer document manifest schema: $($ConsumerDocumentConfig.schemaVersion)"
}
$ConsumerDocumentNames = @($ConsumerDocumentConfig.documents | ForEach-Object { [string]$_.file })
if ($ConsumerDocumentNames.Count -eq 0 -or
    @($ConsumerDocumentNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0 -or
    (@($ConsumerDocumentNames | Select-Object -Unique).Count -ne $ConsumerDocumentNames.Count) -or
    @($ConsumerDocumentNames | Where-Object {
        $_ -ne [IO.Path]::GetFileName($_) -or
        [IO.Path]::GetExtension($_) -ne ".md"
    }).Count -ne 0) {
    throw "The consumer document manifest must contain unique Markdown file names without directory segments"
}

function Get-AppShellVersionProperties {
    $project = Join-Path $ComponentRoot "src\App\App.csproj"
    $output = & dotnet msbuild $project -nologo `
        -getProperty:AppShellVersion -getProperty:FileVersion
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to evaluate the AppShell version source"
    }

    try {
        return (($output -join "`n") | ConvertFrom-Json).Properties
    }
    catch {
        throw "AppShell version evaluation returned invalid JSON: $($output -join ' ')"
    }
}

$VersionProperties = Get-AppShellVersionProperties
$SourceVersion = [string]$VersionProperties.AppShellVersion
$ExpectedFileVersion = [string]$VersionProperties.FileVersion
if ([string]::IsNullOrWhiteSpace($SourceVersion) -or
    [string]::IsNullOrWhiteSpace($ExpectedFileVersion)) {
    throw "AppShellVersion or FileVersion evaluated to an empty value"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    if ($VirtualPublish) {
        throw "Virtual publish requires an explicit -Version"
    }
    $Version = $SourceVersion
}
elseif (-not $VirtualPublish -and $Version -ne $SourceVersion) {
    throw "AppShellVersion.props declares $SourceVersion; requested $Version"
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$StageRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "current"))
$publishRootPrefix = $PublishRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $StageRoot.StartsWith($publishRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Candidate path escaped the publish root: $StageRoot"
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-File {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected file was not produced: $Path"
    }
}

foreach ($documentName in $ConsumerDocumentNames) {
    Assert-File (Join-Path $PackageDocumentRoot $documentName)
}
Assert-File $ReuseDocumentTemplate
Assert-File $CurrentSnapshotReadmeTemplate

$ReleaseInputs = @(
    "b-Code-AppShell"
    $ConsumerDocumentNames | ForEach-Object { "b-Office/package/$_" }
)
$sourceStatus = (& git -C $RepoRoot status --porcelain -- @ReleaseInputs) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect AppShell release inputs with git status"
}
$sourceDirty = -not [string]::IsNullOrWhiteSpace($sourceStatus)
if ($Publish -and $sourceDirty) {
    throw "Formal publish requires committed, clean code and consumer document inputs. " +
        "Build and review b-Publish/current without -Publish first."
}

function Publish-ImmutableDirectory {
    param([string]$Source, [string]$Destination)

    if (Test-Path -LiteralPath $Destination) {
        throw "Refusing to overwrite immutable release directory: $Destination"
    }
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $temporary = Join-Path $parent (".$([IO.Path]::GetFileName($Destination)).tmp-" +
        [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $temporary | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $temporary -Recurse
        }
        $sourceRoot = [IO.Path]::GetFullPath($Source)
        $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File)
        $temporaryFiles = @(Get-ChildItem -LiteralPath $temporary -Recurse -File)
        if ($sourceFiles.Count -ne $temporaryFiles.Count) {
            throw "Immutable release copy has a different file count"
        }
        foreach ($sourceFile in $sourceFiles) {
            $relative = $sourceFile.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
            $copiedFile = Join-Path $temporary $relative
            if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $copiedFile -Algorithm SHA256).Hash) {
                throw "Immutable release copy differs from its source: $relative"
            }
        }
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-ZSnapshot {
    param([string]$SourceDirectory)

    $source = [IO.Path]::GetFullPath($SourceDirectory)
    $publishPrefix = $PublishRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $source.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "The z-level snapshot source must be a directory under b-Publish: $source"
    }

    foreach ($requiredName in @("AppShell.reuse.md", "manifest.json", "README.md", "SHA256SUMS")) {
        Assert-File (Join-Path $source $requiredName)
    }
    $sourceFeed = Join-Path $source "feed"
    $sourceDocs = Join-Path $source "docs"
    if (-not (Test-Path -LiteralPath $sourceFeed -PathType Container) -or
        -not (Test-Path -LiteralPath $sourceDocs -PathType Container)) {
        throw "The z-level snapshot requires feed and docs directories"
    }
    $expectedPackageNames = @(
        "OneHistory.AppShell.Core.$Version.nupkg",
        "OneHistory.AppShell.Services.$Version.nupkg",
        "OneHistory.AppShell.Shell.$Version.nupkg",
        "OneHistory.AppShell.ServiceHost.$Version.nupkg"
    )
    $actualPackageNames = @(Get-ChildItem -LiteralPath $sourceFeed -File | ForEach-Object Name)
    if (@(Compare-Object $expectedPackageNames $actualPackageNames).Count -ne 0) {
        throw "The z-level snapshot must contain exactly the four $Version runtime packages"
    }
    $actualDocumentNames = @(Get-ChildItem -LiteralPath $sourceDocs -File | ForEach-Object Name)
    if (@(Compare-Object $ConsumerDocumentNames $actualDocumentNames).Count -ne 0) {
        throw "The z-level snapshot documents do not match the consumer document manifest"
    }

    $manifest = Get-Content -LiteralPath (Join-Path $source "manifest.json") -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ([string]$manifest.version -ne $Version) {
        throw "Snapshot manifest version $($manifest.version) does not match $Version"
    }
    $sourcePrefix = $source.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $checksumLines = @(Get-Content -LiteralPath (Join-Path $source "SHA256SUMS") -Encoding UTF8)
    foreach ($line in $checksumLines) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            throw "Malformed snapshot checksum line: $line"
        }
        $checksumTarget = [IO.Path]::GetFullPath((Join-Path $source $matches[2].Replace('/', '\')))
        if (-not $checksumTarget.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $checksumTarget -PathType Leaf)) {
            throw "Snapshot checksum target escaped or is missing: $($matches[2])"
        }
        if ((Get-FileHash -LiteralPath $checksumTarget -Algorithm SHA256).Hash -ne $matches[1].ToUpperInvariant()) {
            throw "Snapshot checksum mismatch: $($matches[2])"
        }
    }
    $payloadFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File |
        Where-Object { $_.Name -ne "SHA256SUMS" })
    if ($checksumLines.Count -ne $payloadFiles.Count) {
        throw "Snapshot checksum coverage mismatch"
    }

    $parent = Split-Path -Parent $FormalRoot
    $leaf = Split-Path -Leaf $FormalRoot
    $candidate = Join-Path $parent ("$leaf.next-" + [Guid]::NewGuid().ToString("N"))
    $backup = Join-Path $parent ("$leaf.previous-" + [Guid]::NewGuid().ToString("N"))
    $currentMoved = $false
    $installed = $false
    try {
        New-Item -ItemType Directory -Force -Path $candidate | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $candidate -Recurse
        }
        $candidateFiles = @(Get-ChildItem -LiteralPath $candidate -Recurse -File)
        $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File)
        if ($candidateFiles.Count -ne $sourceFiles.Count) {
            throw "The prepared z-level snapshot file count differs from its source"
        }
        foreach ($sourceFile in $sourceFiles) {
            $relative = $sourceFile.FullName.Substring($source.Length).TrimStart('\', '/')
            $candidateFile = Join-Path $candidate $relative
            if ((Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $candidateFile -Algorithm SHA256).Hash) {
                throw "The prepared z-level snapshot differs from its source: $relative"
            }
        }

        if (Test-Path -LiteralPath $FormalRoot) {
            Move-Item -LiteralPath $FormalRoot -Destination $backup
            $currentMoved = $true
        }
        Move-Item -LiteralPath $candidate -Destination $FormalRoot
        $installed = $true
        if ($currentMoved) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    catch {
        if (-not $installed -and $currentMoved -and
            -not (Test-Path -LiteralPath $FormalRoot) -and
            (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $FormalRoot
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Replaced the z-level current snapshot from $source"
}

function Publish-CurrentSnapshot {
    param(
        [System.IO.FileInfo[]]$Packages,
        [string]$ReuseDocument,
        [string]$DocumentsDirectory
    )

    if ($Packages.Count -ne 4 -or @($Packages | Where-Object Extension -ne ".nupkg").Count -ne 0) {
        throw "The formal snapshot requires exactly four runtime .nupkg files"
    }

    $historySnapshot = Join-Path $PublishRoot "history\$Version"
    if (Test-Path -LiteralPath $historySnapshot) {
        throw "Refusing to overwrite immutable release history: $historySnapshot"
    }
    $snapshot = Join-Path $StageRoot "release-snapshot"
    if (Test-Path -LiteralPath $snapshot) {
        Remove-Item -LiteralPath $snapshot -Recurse -Force
    }
    $snapshotFeed = Join-Path $snapshot "feed"
    $snapshotDocs = Join-Path $snapshot "docs"
    New-Item -ItemType Directory -Force -Path $snapshotFeed, $snapshotDocs | Out-Null
    $readmeTemplateText = Get-Content -LiteralPath $CurrentSnapshotReadmeTemplate -Raw -Encoding UTF8
    if ($readmeTemplateText.IndexOf("{{VERSION}}", [StringComparison]::Ordinal) -lt 0) {
        throw "Current snapshot README template is missing the {{VERSION}} placeholder"
    }
    [IO.File]::WriteAllText(
        (Join-Path $snapshot "README.md"),
        $readmeTemplateText.Replace("{{VERSION}}", $Version),
        [Text.UTF8Encoding]::new($false))
    $snapshotReuse = Join-Path $snapshot "AppShell.reuse.md"
    Copy-Item -LiteralPath $ReuseDocument -Destination $snapshotReuse

    $packageEntries = foreach ($package in $Packages) {
        $destination = Join-Path $snapshotFeed $package.Name
        Copy-Item -LiteralPath $package.FullName -Destination $destination
        [ordered]@{
            file = "feed/$($package.Name)"
            bytes = (Get-Item -LiteralPath $destination).Length
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
    }
    $documentEntries = foreach ($documentName in $ConsumerDocumentNames) {
        $sourceDocument = Join-Path $DocumentsDirectory $documentName
        $destination = Join-Path $snapshotDocs $documentName
        Copy-Item -LiteralPath $sourceDocument -Destination $destination
        [ordered]@{
            file = "docs/$documentName"
            bytes = (Get-Item -LiteralPath $destination).Length
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        }
    }
    $snapshotManifest = [ordered]@{
        schemaVersion = 1
        product = "AppShell"
        version = $Version
        channel = "formal"
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
        sourceDirty = $sourceDirty
        sourceContractVersion = $SourceVersion
        compatibilityValidated = $true
        packages = @($packageEntries)
        documents = @($documentEntries)
        reuse = [ordered]@{
            file = "AppShell.reuse.md"
            bytes = (Get-Item -LiteralPath $snapshotReuse).Length
            sha256 = (Get-FileHash -LiteralPath $snapshotReuse -Algorithm SHA256).Hash
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $snapshot "manifest.json"),
        ($snapshotManifest | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
    $snapshotChecksums = Get-ChildItem -LiteralPath $snapshot -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($snapshot.Length).TrimStart('\', '/').Replace('\', '/')
            "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
        }
    [IO.File]::WriteAllLines(
        (Join-Path $snapshot "SHA256SUMS"),
        $snapshotChecksums,
        [Text.UTF8Encoding]::new($false))

    Install-ZSnapshot -SourceDirectory $snapshot
    Publish-ImmutableDirectory -Source $snapshot -Destination $historySnapshot
}

function Assert-CurrentCandidate {
    $packagesDirectory = Join-Path $StageRoot "packages"
    $documentsDirectory = Join-Path $StageRoot "docs"
    $reuseDocument = Join-Path $StageRoot "AppShell.reuse.md"
    $checksumPath = Join-Path $StageRoot "$Version.sha256"
    $manifestPath = Join-Path $StageRoot "$Version.json"
    foreach ($required in @($reuseDocument, $checksumPath, $manifestPath,
            (Join-Path $StageRoot "vulnerability-audit.json"))) {
        Assert-File $required
    }
    if (-not (Test-Path -LiteralPath $packagesDirectory -PathType Container) -or
        -not (Test-Path -LiteralPath $documentsDirectory -PathType Container)) {
        throw "Current candidate is missing packages or docs: $StageRoot"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $currentCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    if ([int]$manifest.schemaVersion -ne 1 -or [string]$manifest.product -ne "AppShell" -or
        [string]$manifest.version -ne $Version -or [string]$manifest.channel -ne "candidate" -or
        [bool]$manifest.sourceDirty -or [string]$manifest.sourceCommit -ne $currentCommit) {
        throw "Current candidate identity does not match clean HEAD $currentCommit"
    }

    $artifactFiles = @(Get-ChildItem -LiteralPath $packagesDirectory -File | Sort-Object Name)
    $manifestArtifacts = @($manifest.artifacts)
    $manifestArtifactNames = @($manifestArtifacts | ForEach-Object { [string]$_.file })
    if (@(Compare-Object @($artifactFiles.Name) $manifestArtifactNames).Count -ne 0 -or
        @($artifactFiles | Where-Object Extension -eq ".nupkg").Count -ne 4 -or
        @($artifactFiles | Where-Object Extension -eq ".snupkg").Count -ne 4 -or
        @($artifactFiles | Where-Object Extension -eq ".zip").Count -ne 1) {
        throw "Staged artifacts do not match the expected four packages, four symbol packages, and demo ZIP"
    }
    foreach ($entry in $manifestArtifacts) {
        $name = [string]$entry.file
        if ($name -ne [IO.Path]::GetFileName($name)) {
            throw "Staged manifest artifact contains a directory segment: $name"
        }
        $file = Join-Path $packagesDirectory $name
        if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
            throw "Staged artifact differs from its manifest entry: $name"
        }
    }

    $actualDocumentNames = @(Get-ChildItem -LiteralPath $documentsDirectory -File | ForEach-Object Name)
    if (@(Compare-Object $ConsumerDocumentNames $actualDocumentNames).Count -ne 0) {
        throw "Staged docs do not match the consumer document manifest"
    }
    $expectedPayloads = @{}
    foreach ($documentName in $ConsumerDocumentNames) {
        $sourceDocument = Join-Path $PackageDocumentRoot $documentName
        $stagedDocument = Join-Path $documentsDirectory $documentName
        $hash = (Get-FileHash -LiteralPath $stagedDocument -Algorithm SHA256).Hash
        if ($hash -ne (Get-FileHash -LiteralPath $sourceDocument -Algorithm SHA256).Hash) {
            throw "Staged consumer document differs from its source: $documentName"
        }
        $expectedPayloads["docs/$documentName"] = [ordered]@{
            bytes = (Get-Item -LiteralPath $stagedDocument).Length
            sha256 = $hash
        }
    }
    $reuseTemplateText = Get-Content -LiteralPath $ReuseDocumentTemplate -Raw -Encoding UTF8
    $expectedReuseText = $reuseTemplateText.Replace("{{VERSION}}", $Version)
    $actualReuseText = Get-Content -LiteralPath $reuseDocument -Raw -Encoding UTF8
    if ($actualReuseText -cne $expectedReuseText) {
        throw "Staged reuse document differs from the current release template"
    }
    $expectedPayloads["AppShell.reuse.md"] = [ordered]@{
        bytes = (Get-Item -LiteralPath $reuseDocument).Length
        sha256 = (Get-FileHash -LiteralPath $reuseDocument -Algorithm SHA256).Hash
    }
    foreach ($file in $artifactFiles) {
        $expectedPayloads[$file.Name] = [ordered]@{
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }

    $manifestDocuments = @($manifest.documents)
    if ($manifestDocuments.Count -ne ($ConsumerDocumentNames.Count + 1)) {
        throw "Staged manifest must contain four consumer documents and one reuse document"
    }
    foreach ($entry in $manifestDocuments) {
        $name = [string]$entry.file
        if (-not $expectedPayloads.ContainsKey($name) -or
            [long]$entry.bytes -ne [long]$expectedPayloads[$name].bytes -or
            [string]$entry.sha256 -ne [string]$expectedPayloads[$name].sha256) {
            throw "Staged document differs from its manifest entry: $name"
        }
    }

    $checksumLines = @(Get-Content -LiteralPath $checksumPath -Encoding UTF8)
    if ($checksumLines.Count -ne $expectedPayloads.Count) {
        throw "Staged checksum coverage mismatch"
    }
    $seenChecksums = @{}
    foreach ($line in $checksumLines) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            throw "Malformed staged checksum line: $line"
        }
        $name = $matches[2]
        if ($seenChecksums.ContainsKey($name) -or -not $expectedPayloads.ContainsKey($name) -or
            $matches[1].ToUpperInvariant() -ne [string]$expectedPayloads[$name].sha256) {
            throw "Staged checksum differs from the candidate payload: $name"
        }
        $seenChecksums[$name] = $true
    }

    return [pscustomobject]@{
        Artifacts = $artifactFiles
        RuntimePackages = @($artifactFiles | Where-Object Extension -eq ".nupkg")
        DocumentsDirectory = $documentsDirectory
        ReuseDocument = $reuseDocument
        ChecksumPath = $checksumPath
        Manifest = $manifest
        ManifestPath = $manifestPath
    }
}

function Publish-CurrentCandidate {
    $candidate = Assert-CurrentCandidate
    Publish-CurrentSnapshot -Packages $candidate.RuntimePackages `
        -ReuseDocument $candidate.ReuseDocument -DocumentsDirectory $candidate.DocumentsDirectory
    Write-Host "Promoted the reviewed AppShell $Version candidate without rebuilding packages"
    Write-Host "Published the expanded current snapshot to $FormalRoot"
    Write-Host "Archived the same minimal snapshot at $(Join-Path $PublishRoot "history\$Version")"
}

function Publish-VirtualSnapshot {
    param([string]$VirtualVersion)

    $sourceFeed = Join-Path $PublishRoot "history\$VirtualVersion\feed"
    $virtualRoot = [IO.Path]::GetFullPath((Join-Path $PublishRoot "virtual"))
    $virtualPrefix = $virtualRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $target = [IO.Path]::GetFullPath((Join-Path $virtualRoot $VirtualVersion))
    if (-not $target.StartsWith($virtualPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Virtual publish target escaped the b-Publish root: $target"
    }

    $expectedIds = @(
        "OneHistory.AppShell.Core",
        "OneHistory.AppShell.Services",
        "OneHistory.AppShell.Shell",
        "OneHistory.AppShell.ServiceHost"
    )
    $expectedNames = @($expectedIds | ForEach-Object { "$($_).$($VirtualVersion).nupkg" })
    $sourcePackages = @(
        foreach ($packageName in $expectedNames) {
            $packagePath = Join-Path $sourceFeed $packageName
            Assert-File $packagePath
            Get-Item -LiteralPath $packagePath
        }
    )
    if ($sourcePackages.Count -ne $expectedNames.Count) {
        throw "Virtual publish requires four archived $VirtualVersion runtime packages"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in $sourcePackages) {
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $nuspec = $archive.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
            if ($null -eq $nuspec) {
                throw "$($package.Name) is missing a nuspec"
            }
            $reader = [IO.StreamReader]::new($nuspec.Open())
            try { $nuspecText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $id = [Regex]::Match($nuspecText, '<id>([^<]+)</id>').Groups[1].Value
            $packageVersion = [Regex]::Match($nuspecText, '<version>([^<]+)</version>').Groups[1].Value
            if ($id -notin $expectedIds -or $packageVersion -ne $VirtualVersion -or
                $package.Name -ne "$id.$($VirtualVersion).nupkg") {
                throw "$($package.Name) identity does not match virtual version $VirtualVersion"
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    New-Item -ItemType Directory -Force -Path $virtualRoot | Out-Null
    $candidate = [IO.Path]::GetFullPath((Join-Path $virtualRoot (".$VirtualVersion.next-" + [Guid]::NewGuid().ToString("N"))))
    $backup = [IO.Path]::GetFullPath((Join-Path $virtualRoot (".$VirtualVersion.previous-" + [Guid]::NewGuid().ToString("N"))))
    foreach ($path in @($candidate, $backup)) {
        if (-not $path.StartsWith($virtualPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Virtual publish working path escaped the b-Publish root: $path"
        }
    }

    $targetMoved = $false
    $published = $false
    try {
        $candidateFeed = Join-Path $candidate "feed"
        $candidateDocs = Join-Path $candidate "docs"
        New-Item -ItemType Directory -Force -Path $candidateFeed, $candidateDocs | Out-Null

        $packageEntries = foreach ($package in $sourcePackages) {
            $destination = Join-Path $candidateFeed $package.Name
            Copy-Item -LiteralPath $package.FullName -Destination $destination
            [ordered]@{
                file = "feed/$($package.Name)"
                bytes = $package.Length
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            }
        }

        $warning = @"
> **Virtual publish validation only; this is not a formal compatibility contract.**
> Package version: $VirtualVersion. The document body comes from the AppShell $SourceVersion consumer-contract source and validates only the release-generation pipeline. Compatibility with $VirtualVersion is not asserted.

"@
        $documentEntries = foreach ($documentName in $ConsumerDocumentNames) {
            $sourceDocument = Join-Path $PackageDocumentRoot $documentName
            $destination = Join-Path $candidateDocs $documentName
            $sourceText = Get-Content -LiteralPath $sourceDocument -Raw -Encoding UTF8
            [IO.File]::WriteAllText($destination, $warning + $sourceText, [Text.UTF8Encoding]::new($false))
            [ordered]@{
                file = "docs/$documentName"
                bytes = (Get-Item -LiteralPath $destination).Length
                sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            }
        }

        $templateText = Get-Content -LiteralPath $ReuseDocumentTemplate -Raw -Encoding UTF8
        if ($templateText.IndexOf("{{VERSION}}", [StringComparison]::Ordinal) -lt 0) {
            throw "Reuse document template is missing the {{VERSION}} placeholder"
        }
        $reusePath = Join-Path $candidate "AppShell.reuse.md"
        [IO.File]::WriteAllText(
            $reusePath,
            $warning + $templateText.Replace("{{VERSION}}", $VirtualVersion),
            [Text.UTF8Encoding]::new($false))

        $readmePath = Join-Path $candidate "README.md"
        $readmeText = @"
# AppShell $VirtualVersion virtual publish

This directory validates the historical packages, consumer-document generation, manifest, and SHA-256 release chain. It is not the formal feed and declares compatibilityValidated=false. The z-Package-AppShell root remains the formal entry point.

- feed/: four $VirtualVersion packages copied from the immutable b-Publish archive after identity validation.
- docs/: generated from the current consumer-contract sources with a virtual-publish warning.
- AppShell.reuse.md: generated from the release template with a virtual-publish warning.
- manifest.json and SHA256SUMS: the virtual release manifest and file checksums.
"@
        [IO.File]::WriteAllText($readmePath, $readmeText, [Text.UTF8Encoding]::new($false))

        $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
        $manifest = [ordered]@{
            schemaVersion = 1
            product = "AppShell"
            version = $VirtualVersion
            channel = "virtual"
            generatedAtUtc = [DateTime]::UtcNow.ToString("o")
            sourceCommit = $sourceCommit
            sourceDirty = $sourceDirty
            sourceContractVersion = $SourceVersion
            compatibilityValidated = $false
            packages = @($packageEntries)
            documents = @($documentEntries)
            reuse = [ordered]@{
                file = "AppShell.reuse.md"
                bytes = (Get-Item -LiteralPath $reusePath).Length
                sha256 = (Get-FileHash -LiteralPath $reusePath -Algorithm SHA256).Hash
            }
        }
        $manifestPath = Join-Path $candidate "manifest.json"
        [IO.File]::WriteAllText(
            $manifestPath,
            ($manifest | ConvertTo-Json -Depth 8) + "`n",
            [Text.UTF8Encoding]::new($false))

        $checksumLines = Get-ChildItem -LiteralPath $candidate -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($candidate.Length).TrimStart('\', '/').Replace('\', '/')
                "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
            }
        [IO.File]::WriteAllLines(
            (Join-Path $candidate "SHA256SUMS"),
            $checksumLines,
            [Text.UTF8Encoding]::new($false))

        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination $backup
            $targetMoved = $true
        }
        Move-Item -LiteralPath $candidate -Destination $target
        $published = $true
        if ($targetMoved) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    catch {
        if (-not $published -and $targetMoved -and
            -not (Test-Path -LiteralPath $target) -and
            (Test-Path -LiteralPath $backup)) {
            Move-Item -LiteralPath $backup -Destination $target
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $candidate) {
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Virtual-published AppShell $VirtualVersion to $target"
    return $target
}

$mutexInput = [Text.Encoding]::UTF8.GetBytes($ComponentRoot.ToUpperInvariant())
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $mutexHash = (($sha256.ComputeHash($mutexInput) | ForEach-Object { $_.ToString("X2") }) -join "").Substring(0, 16)
}
finally {
    $sha256.Dispose()
}
$publishMutex = [Threading.Mutex]::new($false, "Local\OneHistory.AppShell.Publish.$mutexHash")
$mutexAcquired = $false
try {
    $mutexAcquired = $publishMutex.WaitOne(0)
}
catch [Threading.AbandonedMutexException] {
    $mutexAcquired = $true
}
if (-not $mutexAcquired) {
    $publishMutex.Dispose()
    throw "Another AppShell publish is already running for $ComponentRoot"
}

if ($VirtualPublish) {
    try {
        $virtualSnapshot = Publish-VirtualSnapshot -VirtualVersion $Version
        if ($DeployToZ) {
            Install-ZSnapshot -SourceDirectory $virtualSnapshot
        }
    }
    finally {
        if ($mutexAcquired) {
            $publishMutex.ReleaseMutex()
        }
        $publishMutex.Dispose()
    }
    return
}

if ($Publish) {
    try {
        Publish-CurrentCandidate
    }
    finally {
        if ($mutexAcquired) {
            $publishMutex.ReleaseMutex()
        }
        $publishMutex.Dispose()
    }
    return
}

Push-Location $ComponentRoot
$PreviousNugetPackages = $env:NUGET_PACKAGES
$NugetCache = Join-Path ([IO.Path]::GetTempPath()) ("appshell-release-cache-" + [Guid]::NewGuid().ToString("N"))
$PipelineSucceeded = $false
try {
    if (Test-Path -LiteralPath $StageRoot) {
        & dotnet build-server shutdown | Out-Null
        Remove-Item -LiteralPath $StageRoot -Recurse -Force
    }
    $PackagesDir = Join-Path $StageRoot "packages"
    $StageDocsDir = Join-Path $StageRoot "docs"
    $DemoDir = Join-Path $StageRoot "demo"
    $BuildArtifacts = Join-Path $StageRoot "artifacts"
    $SmokeArtifacts = Join-Path $StageRoot "package-smoke-artifacts"
    New-Item -ItemType Directory -Force -Path `
        $PackagesDir, $StageDocsDir, $DemoDir, $BuildArtifacts, $SmokeArtifacts, $NugetCache | Out-Null
    $env:NUGET_PACKAGES = $NugetCache

    foreach ($documentName in $ConsumerDocumentNames) {
        $sourceDocument = Join-Path $PackageDocumentRoot $documentName
        $stagedDocument = Join-Path $StageDocsDir $documentName
        Copy-Item -LiteralPath $sourceDocument -Destination $stagedDocument
        if ((Get-FileHash -LiteralPath $sourceDocument -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $stagedDocument -Algorithm SHA256).Hash) {
            throw "Staged consumer document differs from its b-Office/package source: $documentName"
        }
    }
    $reuseTemplateText = Get-Content -LiteralPath $ReuseDocumentTemplate -Raw -Encoding UTF8
    if ($reuseTemplateText.IndexOf("{{VERSION}}", [StringComparison]::Ordinal) -lt 0) {
        throw "Reuse document template is missing the {{VERSION}} placeholder"
    }
    $ReuseDocumentPath = Join-Path $StageRoot "AppShell.reuse.md"
    $reuseDocumentText = $reuseTemplateText.Replace("{{VERSION}}", $Version)
    [IO.File]::WriteAllText($ReuseDocumentPath, $reuseDocumentText, [Text.UTF8Encoding]::new($false))

    $artifactsProperty = "-p:ArtifactsPath=$BuildArtifacts"
    Invoke-Dotnet @("restore", "AppShell.sln", "--locked-mode", $artifactsProperty)
    Invoke-Dotnet @("build", "AppShell.sln", "-c", "Debug", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("test", "tests\AppShell.Tests\AppShell.Tests.csproj", "-c", "Debug",
        "--no-build", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("build", "AppShell.sln", "-c", "Release", "--no-restore", $artifactsProperty)
    Invoke-Dotnet @("test", "tests\AppShell.Tests\AppShell.Tests.csproj", "-c", "Release",
        "--no-build", "--no-restore", $artifactsProperty)

    $auditPath = Join-Path $StageRoot "vulnerability-audit.json"
    $auditOutput = & dotnet list AppShell.sln package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) {
        throw "NuGet vulnerability audit failed"
    }
    $auditText = $auditOutput -join "`n"
    [IO.File]::WriteAllText($auditPath, $auditText, [Text.UTF8Encoding]::new($false))
    $audit = $auditText | ConvertFrom-Json
    $vulnerabilities = @()
    foreach ($project in $audit.projects) {
        $frameworksProperty = $project.PSObject.Properties["frameworks"]
        if ($null -eq $frameworksProperty) { continue }
        foreach ($framework in $frameworksProperty.Value) {
            foreach ($collectionName in @("topLevelPackages", "transitivePackages")) {
                $collectionProperty = $framework.PSObject.Properties[$collectionName]
                if ($null -eq $collectionProperty) { continue }
                foreach ($package in $collectionProperty.Value) {
                    $packageVulnerabilities = $package.PSObject.Properties["vulnerabilities"]
                    if ($null -ne $packageVulnerabilities) {
                        $vulnerabilities += @($packageVulnerabilities.Value)
                    }
                }
            }
        }
    }
    if ($vulnerabilities.Count -ne 0) {
        throw "NuGet vulnerability audit found $($vulnerabilities.Count) vulnerable dependency entries"
    }

    $packProjects = @(
        "src\AppShell.Core\AppShell.Core.csproj",
        "src\AppShell.Services\AppShell.Services.csproj",
        "src\AppShell.Shell\AppShell.Shell.csproj",
        "src\AppShell.ServiceHost\AppShell.ServiceHost.csproj"
    )
    foreach ($project in $packProjects) {
        Invoke-Dotnet @("pack", $project, "-c", "Release", "--no-build", "--no-restore",
            $artifactsProperty, "-o", $PackagesDir)
    }

    $packageIds = @(
        "OneHistory.AppShell.Core",
        "OneHistory.AppShell.Services",
        "OneHistory.AppShell.Shell",
        "OneHistory.AppShell.ServiceHost"
    )
    foreach ($id in $packageIds) {
        Assert-File (Join-Path $PackagesDir "$id.$Version.nupkg")
        Assert-File (Join-Path $PackagesDir "$id.$Version.snupkg")
    }
    if (Get-ChildItem -LiteralPath $PackagesDir -Filter "AppShell.$Version.nupkg" -ErrorAction SilentlyContinue) {
        throw "The demo WinExe was packed as a NuGet library"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($id in $packageIds) {
        $path = Join-Path $PackagesDir "$id.$Version.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entryNames = @($archive.Entries | ForEach-Object FullName)
            if ($entryNames -notcontains "PACKAGE.md") {
                throw "$id package is missing PACKAGE.md"
            }
            $guideEntries = @($entryNames | Where-Object {
                $_.StartsWith("docs/", [StringComparison]::Ordinal) -and
                $_.EndsWith(".md", [StringComparison]::OrdinalIgnoreCase)
            })
            if ($guideEntries.Count -ne 0) {
                throw "$id package must not embed consumer Markdown: $($guideEntries -join ', ')"
            }
            if (-not ($entryNames | Where-Object { $_ -like "lib/*.xml" })) {
                throw "$id package is missing XML documentation"
            }
            $nuspec = $archive.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
            $reader = [IO.StreamReader]::new($nuspec.Open())
            try { $nuspecText = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($nuspecText -notmatch "<id>$([Regex]::Escape($id))</id>" -or
                $nuspecText -notmatch "<version>$([Regex]::Escape($Version))</version>") {
                throw "$id nuspec identity mismatch"
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $smokeProject = "tests\PackageSmoke\PackageSmoke.csproj"
    $smokeConfig = Join-Path $StageRoot "PackageSmoke.NuGet.Config"
    $escapedPackagesDir = [Security.SecurityElement]::Escape($PackagesDir)
    $smokeConfigText = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="AppShell staging" value="$escapedPackagesDir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText($smokeConfig, $smokeConfigText, [Text.UTF8Encoding]::new($false))
    $smokeArtifactsProperty = "-p:ArtifactsPath=$SmokeArtifacts"
    Invoke-Dotnet @("restore", $smokeProject, "--force-evaluate", "--configfile", $smokeConfig,
        "--packages", (Join-Path $StageRoot "smoke-cache"), $smokeArtifactsProperty)
    Invoke-Dotnet @("build", $smokeProject, "-c", "Release", "--no-restore", $smokeArtifactsProperty)
    $runtimeConfigs = @(Get-ChildItem -LiteralPath (Join-Path $SmokeArtifacts "bin\PackageSmoke") `
        -Recurse -Filter "PackageSmoke.runtimeconfig.json" -File)
    if ($runtimeConfigs.Count -ne 1) {
        throw "Expected one PackageSmoke runtimeconfig, found $($runtimeConfigs.Count)"
    }
    $SmokeOutput = $runtimeConfigs[0].Directory.FullName
    $smokeDll = Join-Path $SmokeOutput "PackageSmoke.dll"
    Assert-File $smokeDll
    Invoke-Dotnet @($smokeDll)

    $smokeFiles = @(Get-ChildItem -LiteralPath $SmokeOutput -Recurse -File)
    $smokeBytes = ($smokeFiles | Measure-Object -Property Length -Sum).Sum
    $forbiddenAssets = @($smokeFiles | Where-Object {
        $relative = $_.FullName.Substring($SmokeOutput.Length).TrimStart('\', '/')
        $relative -match '(^|[\\/])runtimes[\\/](linux|osx|ios|android|win-(arm|arm64|x86))'
    })
    if ($forbiddenAssets.Count -ne 0) {
        throw "PackageSmoke contains non-target runtime assets: $($forbiddenAssets.FullName -join ', ')"
    }
    $smokeLimit = 10MB
    if ($smokeBytes -gt $smokeLimit) {
        $largest = $smokeFiles | Sort-Object Length -Descending | Select-Object -First 20
        throw "PackageSmoke output is $smokeBytes bytes, limit is $smokeLimit. Largest files: " +
            (($largest | ForEach-Object { "$($_.Length):$($_.FullName)" }) -join '; ')
    }
    Write-Host "PackageSmoke footprint: $smokeBytes bytes ($($smokeFiles.Count) files), RID win-x64"

    Invoke-Dotnet @("publish", "src\App\App.csproj", "-c", "Release", "--no-restore",
        $artifactsProperty, "--self-contained", "false", "-r", "win-x64", "-o", $DemoDir)
    $demoExe = Join-Path $DemoDir "AppShell.exe"
    Assert-File $demoExe
    $demoVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($demoExe).FileVersion
    if ($demoVersion -ne $ExpectedFileVersion) {
        throw "Demo file version is $demoVersion, expected $ExpectedFileVersion"
    }

    $demoZip = Join-Path $PackagesDir "AppShell-Demo-$Version-win-x64-framework-dependent.zip"
    Compress-Archive -Path (Join-Path $DemoDir "*") -DestinationPath $demoZip -CompressionLevel Optimal

    $artifactFiles = @(Get-ChildItem -LiteralPath $PackagesDir -File | Sort-Object Name)
    $checksumLines = @(
        foreach ($file in $artifactFiles) {
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            "$hash  $($file.Name)"
        }
        foreach ($documentName in $ConsumerDocumentNames) {
            $documentPath = Join-Path $StageDocsDir $documentName
            $hash = (Get-FileHash -LiteralPath $documentPath -Algorithm SHA256).Hash
            "$hash  docs/$documentName"
        }
        $reuseHash = (Get-FileHash -LiteralPath $ReuseDocumentPath -Algorithm SHA256).Hash
        "$reuseHash  AppShell.reuse.md"
    )
    $checksumPath = Join-Path $StageRoot "$Version.sha256"
    [IO.File]::WriteAllLines($checksumPath, $checksumLines, [Text.UTF8Encoding]::new($false))

    $sourceCommit = (& git -C $RepoRoot rev-parse HEAD).Trim()
    $manifestArtifacts = foreach ($file in $artifactFiles) {
        [ordered]@{
            file = $file.Name
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $manifestDocuments = foreach ($documentName in $ConsumerDocumentNames) {
        $documentPath = Join-Path $StageDocsDir $documentName
        [ordered]@{
            file = "docs/$documentName"
            bytes = (Get-Item -LiteralPath $documentPath).Length
            sha256 = (Get-FileHash -LiteralPath $documentPath -Algorithm SHA256).Hash
        }
    }
    $manifestDocuments += [ordered]@{
        file = "AppShell.reuse.md"
        bytes = (Get-Item -LiteralPath $ReuseDocumentPath).Length
        sha256 = (Get-FileHash -LiteralPath $ReuseDocumentPath -Algorithm SHA256).Hash
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        product = "AppShell"
        version = $Version
        channel = "candidate"
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        sdk = (& dotnet --version).Trim()
        targetFrameworks = @("net8.0", "net8.0-windows")
        demoRid = "win-x64"
        selfContained = $false
        artifacts = @($manifestArtifacts)
        documents = @($manifestDocuments)
    }
    $manifestPath = Join-Path $StageRoot "$Version.json"
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($manifestPath, $manifestJson + "`n", [Text.UTF8Encoding]::new($false))

    Write-Host "Prepared AppShell $Version current candidate at $StageRoot"
    $PipelineSucceeded = $true
}
finally {
    & dotnet build-server shutdown | Out-Null
    $env:NUGET_PACKAGES = $PreviousNugetPackages
    $workspaceRestoreOutput = & dotnet restore AppShell.sln --locked-mode --force-evaluate 2>&1
    $workspaceRestoreExitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath $NugetCache) {
        Remove-Item -LiteralPath $NugetCache -Recurse -Force -ErrorAction SilentlyContinue
    }
    Pop-Location
    if ($mutexAcquired) {
        $publishMutex.ReleaseMutex()
    }
    $publishMutex.Dispose()
    if ($workspaceRestoreExitCode -ne 0) {
        $message = "Failed to restore the workspace NuGet asset graph after isolated packaging: " +
            ($workspaceRestoreOutput -join " ")
        if ($PipelineSucceeded) {
            throw $message
        }
        Write-Warning $message
    }
}
