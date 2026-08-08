$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Publish-Transaction.ps1')

function Assert-True {
    param([bool]$Condition, [string]$Name)
    if (-not $Condition) { throw "FAIL: $Name" }
}

function Write-Marker {
    param([string]$Root, [string]$Value)
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    [IO.File]::WriteAllText((Join-Path $Root 'marker.txt'), $Value)
}

function Read-Marker {
    param([string]$Root)
    return [IO.File]::ReadAllText((Join-Path $Root 'marker.txt'))
}

function Invoke-PromotionCase {
    param([string]$Name, [scriptblock]$Body)
    $root = Join-Path ([IO.Path]::GetTempPath()) "Janus-Publish-Transaction-$Name-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $root | Out-Null
    try { & $Body $root }
    finally {
        $fullRoot = [IO.Path]::GetFullPath($root)
        $tempPrefix = ([IO.Path]::GetFullPath([IO.Path]::GetTempPath())).TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $fullRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($fullRoot)).StartsWith(
                'Janus-Publish-Transaction-', [StringComparison]::Ordinal)) {
            throw "Refusing to clean unexpected transaction test path: $fullRoot"
        }
        Remove-Item -LiteralPath $fullRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$validator = {
    param($Root)
    if ((Read-Marker $Root) -ne 'new') { throw 'candidate validation failed' }
}

Invoke-PromotionCase 'success' {
    param($root)
    $candidate = Join-Path $root 'candidate'; $destination = Join-Path $root 'delivery'
    $backup = Join-Path $root 'backup'; $quarantine = Join-Path $root 'quarantine'
    Write-Marker $candidate 'new'; Write-Marker $destination 'old'
    Invoke-DirectoryPromotion $candidate $destination $backup $quarantine $validator
    Assert-True ((Read-Marker $destination) -eq 'new') 'success installs candidate'
    Assert-True ((Read-Marker $backup) -eq 'old') 'success keeps rollback backup'
    Assert-True (-not (Test-Path $quarantine)) 'success creates no quarantine'
}

Invoke-PromotionCase 'after-backup-failure' {
    param($root)
    $candidate = Join-Path $root 'candidate'; $destination = Join-Path $root 'delivery'
    $backup = Join-Path $root 'backup'; $quarantine = Join-Path $root 'quarantine'
    Write-Marker $candidate 'new'; Write-Marker $destination 'old'
    try {
        Invoke-DirectoryPromotion $candidate $destination $backup $quarantine $validator { throw 'injected after-backup failure' }
        throw 'expected promotion failure'
    }
    catch {
        Assert-True ($_.Exception.Message -like '*injected after-backup failure*') 'after-backup error preserved'
    }
    Assert-True ((Read-Marker $destination) -eq 'old') 'after-backup failure restores old delivery'
    Assert-True ((Read-Marker $quarantine) -eq 'new') 'after-backup failure quarantines candidate'
    Assert-True (-not (Test-Path $backup)) 'after-backup failure consumes backup during restore'
}

Invoke-PromotionCase 'candidate-validation-failure' {
    param($root)
    $candidate = Join-Path $root 'candidate'; $destination = Join-Path $root 'delivery'
    $backup = Join-Path $root 'backup'; $quarantine = Join-Path $root 'quarantine'
    Write-Marker $candidate 'invalid'; Write-Marker $destination 'old'
    try {
        Invoke-DirectoryPromotion $candidate $destination $backup $quarantine $validator
        throw 'expected promotion failure'
    }
    catch {
        Assert-True ($_.Exception.Message -like '*candidate validation failed*') 'candidate error preserved'
    }
    Assert-True ((Read-Marker $destination) -eq 'old') 'candidate failure leaves old delivery in place'
    Assert-True ((Read-Marker $quarantine) -eq 'invalid') 'candidate failure quarantines only candidate'
    Assert-True (-not (Test-Path $backup)) 'candidate failure creates no backup'
}

Invoke-PromotionCase 'post-swap-failure' {
    param($root)
    $candidate = Join-Path $root 'candidate'; $destination = Join-Path $root 'delivery'
    $backup = Join-Path $root 'backup'; $quarantine = Join-Path $root 'quarantine'
    Write-Marker $candidate 'new'; Write-Marker $destination 'old'
    $postSwapValidator = {
        param($releaseRoot)
        if ([IO.Path]::GetFullPath($releaseRoot) -eq [IO.Path]::GetFullPath($destination)) {
            throw 'injected post-swap validation failure'
        }
        & $validator $releaseRoot
    }
    try {
        Invoke-DirectoryPromotion $candidate $destination $backup $quarantine $postSwapValidator
        throw 'expected promotion failure'
    }
    catch {
        Assert-True ($_.Exception.Message -like '*post-swap validation failure*') 'post-swap error preserved'
    }
    Assert-True ((Read-Marker $destination) -eq 'old') 'post-swap failure restores old delivery'
    Assert-True ((Read-Marker $quarantine) -eq 'new') 'post-swap failure quarantines failed delivery'
}

Invoke-PromotionCase 'no-old-version' {
    param($root)
    $candidate = Join-Path $root 'candidate'; $destination = Join-Path $root 'delivery'
    $backup = Join-Path $root 'backup'; $quarantine = Join-Path $root 'quarantine'
    Write-Marker $candidate 'new'
    Invoke-DirectoryPromotion $candidate $destination $backup $quarantine $validator
    Assert-True ((Read-Marker $destination) -eq 'new') 'first promotion installs candidate'
    Assert-True (-not (Test-Path $backup)) 'first promotion creates no backup'
}

Invoke-PromotionCase 'release-tree' {
    param($root)
    $releaseRoot = Join-Path $root 'release-root'
    $metadataRoot = Join-Path $releaseRoot 'release'
    New-Item -ItemType Directory -Force -Path $metadataRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'HistoryJanus.exe'), 'exe')
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'docs.md'), 'docs')
    $artifacts = @()
    $checksumLines = @()
    foreach ($relative in @('HistoryJanus.exe', 'docs.md')) {
        $path = Join-Path $releaseRoot $relative
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $artifacts += [ordered]@{ file = $relative; bytes = (Get-Item $path).Length; sha256 = $hash }
        $checksumLines += "$hash  $relative"
    }
    $manifest = [ordered]@{
        schemaVersion = 1; product = 'HistoryJanus'; version = '2.7.5'; channel = 'candidate'; artifacts = $artifacts
    }
    [IO.File]::WriteAllText((Join-Path $metadataRoot '2.7.5.json'), ($manifest | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllLines((Join-Path $metadataRoot '2.7.5.sha256'), $checksumLines)
    Assert-ReleaseTree $releaseRoot '2.7.5' 'candidate'
    try { Assert-ReleaseTree $releaseRoot '2.7.5' 'package'; throw 'expected channel failure' }
    catch { Assert-True ($_.Exception.Message -like '*expected package*') 'wrong channel is rejected' }
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'docs.md'), 'tampered')
    try { Assert-ReleaseTree $releaseRoot '2.7.5'; throw 'expected checksum failure' }
    catch { Assert-True ($_.Exception.Message -like '*does not match manifest*') 'tamper is rejected' }
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'docs.md'), 'docs')
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'extra.txt'), 'extra')
    try { Assert-ReleaseTree $releaseRoot '2.7.5'; throw 'expected extra-file failure' }
    catch { Assert-True ($_.Exception.Message -like '*file set differs*') 'untracked artifact is rejected' }
    Remove-Item -LiteralPath (Join-Path $releaseRoot 'extra.txt')
    [IO.File]::WriteAllText((Join-Path $metadataRoot 'unexpected.txt'), 'extra metadata')
    try { Assert-ReleaseTree $releaseRoot '2.7.5'; throw 'expected metadata failure' }
    catch { Assert-True ($_.Exception.Message -like '*must contain only*') 'extra metadata is rejected' }
}

Write-Host 'PublishTransactionSmoke: PASS'
