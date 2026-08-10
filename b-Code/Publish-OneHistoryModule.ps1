[CmdletBinding()]
param(
    [ValidateSet('HistoryMinerva', 'HistoryVulcan')]
    [string]$Module = 'HistoryMinerva',
    [switch]$Publish,
    [switch]$AllowDirtySource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dianaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectsRoot = [IO.Path]::GetFullPath((Join-Path $dianaRoot '..'))
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$workRoot = Join-Path $dianaRoot "b-Publish\work\module-release-$transactionId"

# Kind 决定快照形状与验证步骤，其余字段两类共用。
#   module  平铺快照 + module.manifest.json(name/version) + Smoke/UiSmoke 控制台
#   host    host/ 与 docs/ 多层快照 + manifest.json(product/version) + dotnet test + 门禁
# 两类的候选构建调用形状是同一个：-Configuration Release -OutputRoot <候选目录>。
$definitions = @{
    HistoryMinerva = [ordered]@{
        Kind = 'module'
        ProjectDirectory = '2026-024-HistoryMinerva'
        VersionProps = 'b-Code-HistoryMinerva\build\HistoryMinerva.Version.props'
        VersionProperty = 'HistoryMinervaVersion'
        SourceManifest = 'b-Code-HistoryMinerva\module.manifest.json'
        SnapshotManifest = 'module.manifest.json'
        IdentityProperty = 'name'
        CandidateDirectory = 'b-Publish\current\HistoryMinerva'
        FormalDirectory = 'z-HistoryMinerva'
        BuildScript = 'b-Code-HistoryMinerva\eng\Build-HistoryMinervaPackage.ps1'
        PackageDocuments = 'b-Office\package'
        ContractScript = 'b-Code\Test-ProjectContract.ps1'
        SmokeProject = 'b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.Smoke\HistoryMinerva.Smoke.csproj'
        UiSmokeProject = 'b-Code-HistoryMinerva-Tests\tests\HistoryMinerva.UiSmoke\HistoryMinerva.UiSmoke.csproj'
        TestProject = ''
        GateScripts = @()
    }
    HistoryVulcan = [ordered]@{
        Kind = 'host'
        ProjectDirectory = '2026-023-HistoryVulcan'
        VersionProps = 'b-Code-HistoryVulcan\VulcanVersion.props'
        VersionProperty = 'VulcanVersion'
        # 宿主没有"源 manifest"：身份由版本源加快照 manifest 表达，没有第三处可漂移。
        SourceManifest = ''
        SnapshotManifest = 'manifest.json'
        IdentityProperty = 'product'
        CandidateDirectory = 'b-Publish\current'
        FormalDirectory = 'z-HistoryVulcan'
        BuildScript = 'b-Code-HistoryVulcan\eng\Publish-HistoryVulcanHost.ps1'
        PackageDocuments = 'b-Office\package'
        ContractScript = ''
        SmokeProject = ''
        UiSmokeProject = ''
        TestProject = 'b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj'
        GateScripts = @(
            'b-Code-HistoryVulcan\eng\Test-QualityGate.ps1',
            'b-Code-HistoryVulcan\eng\Assert-PublicApiBaseline.ps1'
        )
    }
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its allowed root: $fullPath"
    }
    return $fullPath
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "[$Module] $Description"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Read-ModuleVersion {
    param([string]$PropsPath, [string]$PropertyName)

    [xml]$props = [IO.File]::ReadAllText($PropsPath, [Text.UTF8Encoding]::new($false))
    $values = @($props.Project.PropertyGroup | ForEach-Object { $_.$PropertyName } | Where-Object { $_ })
    if ($values.Count -ne 1 -or [string]$values[0] -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version source must declare exactly one semantic version property ${PropertyName}: $PropsPath"
    }
    return [string]$values[0]
}

function Assert-ModuleSnapshot {
    param(
        [string]$Root,
        [string]$ExpectedName,
        [string]$ExpectedVersion,
        [string]$ManifestName = 'module.manifest.json',
        [string]$IdentityProperty = 'name',
        [string]$Kind = 'module'
    )

    $manifestPath = Join-Path $Root $ManifestName
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "Module snapshot is incomplete: $Root"
    }

    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($manifest.$IdentityProperty -ne $ExpectedName -or $manifest.version -ne $ExpectedVersion) {
        throw "Module snapshot identity mismatch: expected $ExpectedName $ExpectedVersion"
    }

    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($sumsPath, [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64}) [ *](?<file>.+)$') {
            throw "Invalid SHA256SUMS line in ${Root}: $line"
        }
        if ($hashes.ContainsKey($Matches.file)) {
            throw "Duplicate checksum entry in ${Root}: $($Matches.file)"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }

    # 宿主快照有 host/ 与 docs/ 子目录，SHA256SUMS 用带 / 的相对路径；模块快照是平铺文件名。
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = if ($Kind -eq 'host') {
        @(Get-ChildItem -LiteralPath $Root -File -Recurse | Where-Object Name -ne 'SHA256SUMS')
    } else {
        @(Get-ChildItem -LiteralPath $Root -File | Where-Object Name -ne 'SHA256SUMS')
    }
    if ($files.Count -ne $hashes.Count) {
        throw "SHA256SUMS does not cover the complete snapshot ($($files.Count) files vs $($hashes.Count) entries): $Root"
    }
    foreach ($file in $files) {
        $key = if ($Kind -eq 'host') {
            $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        } else {
            $file.Name
        }
        $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($key) -or $hashes[$key] -ne $actual) {
            throw "Checksum mismatch in ${Root}: $key"
        }
    }
}

function New-ConsumerDocumentMirror {
    param(
        [string]$SourceRoot,
        [string]$Destination,
        [string]$ModuleName,
        [string]$Version,
        [string]$ProjectDirectory,
        [string]$SourceCommit,
        [bool]$SourceDirty
    )

    $documents = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.md' -File | Sort-Object Name)
    if ($documents.Count -eq 0) {
        throw "No consumer Markdown documents found: $SourceRoot"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $entries = foreach ($document in $documents) {
        $target = Join-Path $Destination $document.Name
        Copy-Item -LiteralPath $document.FullName -Destination $target
        [ordered]@{
            file = $document.Name
            bytes = (Get-Item -LiteralPath $target).Length
            sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToUpperInvariant()
        }
    }

    $mirrorManifest = [ordered]@{
        schemaVersion = 1
        module = $ModuleName
        version = $Version
        sourceProject = $ProjectDirectory
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        sourceDocumentRoot = 'b-Office/package'
        artifactSnapshot = "../../../../$ProjectDirectory/z-$ModuleName"
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        documents = @($entries)
    }
    [IO.File]::WriteAllText(
        (Join-Path $Destination 'manifest.json'),
        ($mirrorManifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Assert-ConsumerDocumentMirror {
    param([string]$Root, [string]$ExpectedModule, [string]$ExpectedVersion)

    $manifestPath = Join-Path $Root 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Consumer document mirror manifest is missing: $Root"
    }
    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($manifest.module -ne $ExpectedModule -or $manifest.version -ne $ExpectedVersion) {
        throw "Consumer document mirror identity mismatch: $Root"
    }
    foreach ($document in @($manifest.documents)) {
        $path = Join-Path $Root ([string]$document.file)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Mirrored consumer document is missing: $path"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne [string]$document.sha256) {
            throw "Mirrored consumer document checksum mismatch: $path"
        }
    }
}

$definition = $definitions[$Module]
$projectRoot = Assert-ChildPath (Join-Path $projectsRoot $definition.ProjectDirectory) $projectsRoot 'Module project'
$versionPropsPath = Join-Path $projectRoot $definition.VersionProps
$sourceManifestPath = Join-Path $projectRoot $definition.SourceManifest
$candidateRoot = Assert-ChildPath (Join-Path $projectRoot $definition.CandidateDirectory) $projectRoot 'Candidate directory'
$formalRoot = Assert-ChildPath (Join-Path $projectRoot $definition.FormalDirectory) $projectRoot 'Formal directory'
$documentSourceRoot = Join-Path $projectRoot $definition.PackageDocuments
$consumerDocumentsDirectoryName = -join @(0x6D88, 0x8D39, 0x6587, 0x6863 | ForEach-Object { [char]$_ })
$documentMirrorRoot = Assert-ChildPath `
    (Join-Path $dianaRoot "b-Office-OneHistory\$consumerDocumentsDirectoryName\$Module") `
    $dianaRoot `
    'Document mirror'
$moduleVersion = Read-ModuleVersion $versionPropsPath $definition.VersionProperty

# 模块的身份写在三处(版本源、源 manifest、快照 manifest)，这里对齐前两处。
# 宿主只有两处：版本源与快照 manifest，没有源 manifest 可对，也就少一处可漂移。
if ($definition.Kind -eq 'module') {
    if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
        throw "Source module manifest is missing: $sourceManifestPath"
    }
    $sourceManifest = [IO.File]::ReadAllText($sourceManifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($sourceManifest.name -ne $Module -or $sourceManifest.version -ne $moduleVersion) {
        throw "Source manifest must match $Module $moduleVersion before release"
    }
}

$sourceStatus = @(& git -C $projectRoot status --porcelain -- ':!b-Publish/**' ":!$($definition.FormalDirectory)/**")
if ($LASTEXITCODE -ne 0) { throw "Unable to read Git status: $projectRoot" }
$sourceDirty = $sourceStatus.Count -gt 0
if ($sourceDirty -and -not $AllowDirtySource) {
    throw "Source worktree is dirty. Commit it or explicitly pass -AllowDirtySource:`n$($sourceStatus -join [Environment]::NewLine)"
}
if ($sourceDirty) {
    Write-Warning "Publishing dirty $Module source because -AllowDirtySource was explicitly supplied."
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Unable to resolve source commit: $projectRoot"
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    Invoke-Checked 'powershell.exe' @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $definition.BuildScript),
        '-Configuration', 'Release', '-OutputRoot', $candidateRoot
    ) $projectRoot 'Build candidate package'
    Assert-ModuleSnapshot $candidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    if ($definition.Kind -eq 'module') {
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $definition.ContractScript),
            '-Instantiation'
        ) $projectRoot 'Run project contract checks'
        Invoke-Checked 'dotnet.exe' @(
            'run', '--project', (Join-Path $projectRoot $definition.SmokeProject), '-c', 'Release',
            '-p:NuGetAudit=false'
        ) $projectRoot 'Run module Smoke'
        Invoke-Checked 'dotnet.exe' @(
            'run', '--project', (Join-Path $projectRoot $definition.UiSmokeProject), '-c', 'Release',
            '-p:NuGetAudit=false', '--', '--width', '320', '--height', '680', '--dark',
            '--capture', (Join-Path $workRoot 'ui-smoke-320x680-dark.png')
        ) $projectRoot 'Run module UI Smoke'
    }
    else {
        # 宿主的等价验证不是 Smoke 控制台，而是单元测试加两道门禁。
        Invoke-Checked 'dotnet.exe' @(
            'test', (Join-Path $projectRoot $definition.TestProject), '-c', 'Release', '--nologo'
        ) $projectRoot 'Run host unit tests'
        foreach ($gate in @($definition.GateScripts)) {
            Invoke-Checked 'powershell.exe' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $gate)
            ) $projectRoot "Run host gate $(Split-Path -Leaf $gate)"
        }
    }

    $documentStage = Join-Path $workRoot 'consumer-docs'
    New-ConsumerDocumentMirror $documentSourceRoot $documentStage $Module $moduleVersion `
        $definition.ProjectDirectory $sourceCommit $sourceDirty
    Assert-ConsumerDocumentMirror $documentStage $Module $moduleVersion

    if (-not $Publish) {
        Write-Host "Verified candidate $Module ${moduleVersion}: $candidateRoot"
        Write-Host 'Pass -Publish to promote the candidate and consumer document mirror.'
        return
    }

    $formalStage = Join-Path $workRoot 'formal'
    Copy-Item -LiteralPath $candidateRoot -Destination $formalStage -Recurse
    Assert-ModuleSnapshot $formalStage $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    $oldVersion = 'none'
    $formalManifestPath = Join-Path $formalRoot $definition.SnapshotManifest
    if (Test-Path -LiteralPath $formalManifestPath -PathType Leaf) {
        $oldVersion = [string](([IO.File]::ReadAllText($formalManifestPath) | ConvertFrom-Json).version)
    }
    $formalBackup = Join-Path $projectRoot "b-Publish\history\$Module\$oldVersion-$stamp-$($transactionId.Substring(0, 8))"
    $documentBackup = Join-Path $dianaRoot "b-Publish\history\consumer-docs\$Module\$oldVersion-$stamp-$($transactionId.Substring(0, 8))"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $formalBackup), (Split-Path -Parent $documentBackup), (Split-Path -Parent $documentMirrorRoot) | Out-Null

    $formalBackedUp = $false
    $formalPromoted = $false
    $documentsBackedUp = $false
    $documentsPromoted = $false
    try {
        if (Test-Path -LiteralPath $formalRoot) {
            Move-Item -LiteralPath $formalRoot -Destination $formalBackup
            $formalBackedUp = $true
        }
        Move-Item -LiteralPath $formalStage -Destination $formalRoot
        $formalPromoted = $true
        Assert-ModuleSnapshot $formalRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

        if (Test-Path -LiteralPath $documentMirrorRoot) {
            Move-Item -LiteralPath $documentMirrorRoot -Destination $documentBackup
            $documentsBackedUp = $true
        }
        Move-Item -LiteralPath $documentStage -Destination $documentMirrorRoot
        $documentsPromoted = $true
        Assert-ConsumerDocumentMirror $documentMirrorRoot $Module $moduleVersion
    }
    catch {
        if ($documentsPromoted -and (Test-Path -LiteralPath $documentMirrorRoot)) {
            Move-Item -LiteralPath $documentMirrorRoot -Destination (Join-Path $workRoot 'failed-consumer-docs')
        }
        if ($documentsBackedUp -and (Test-Path -LiteralPath $documentBackup)) {
            Move-Item -LiteralPath $documentBackup -Destination $documentMirrorRoot
        }
        if ($formalPromoted -and (Test-Path -LiteralPath $formalRoot)) {
            Move-Item -LiteralPath $formalRoot -Destination (Join-Path $workRoot 'failed-formal')
        }
        if ($formalBackedUp -and (Test-Path -LiteralPath $formalBackup)) {
            Move-Item -LiteralPath $formalBackup -Destination $formalRoot
        }
        throw
    }

    Write-Host "Published $Module ${moduleVersion}: $formalRoot"
    Write-Host "Consumer documents: $documentMirrorRoot"
    if ($formalBackedUp) { Write-Host "Previous formal snapshot: $formalBackup" }
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
