function Get-ReleaseRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release artifact escaped its root: $fullPath"
    }

    return $fullPath.Substring($fullRoot.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
}

function Resolve-ReleaseArtifactPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Release manifest contains an invalid artifact path: $RelativePath"
    }

    $platformPath = $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $platformPath))
    $normalized = Get-ReleaseRelativePath $fullPath $Root
    if ($normalized.StartsWith('release/', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release manifest may not claim reserved metadata path: $RelativePath"
    }

    return [ordered]@{ FullPath = $fullPath; RelativePath = $normalized }
}

function Read-ReleaseChecksumMap {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release checksum file is missing: $Path"
    }

    $map = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            throw "Release checksum contains an invalid line: $line"
        }
        $relative = $Matches[2].Replace('\', '/')
        if ($map.ContainsKey($relative)) {
            throw "Release checksum contains a duplicate artifact: $relative"
        }
        $map.Add($relative, $Matches[1].ToUpperInvariant())
    }
    if ($map.Count -eq 0) {
        throw "Release checksum contains no artifacts: $Path"
    }
    return $map
}

function Assert-ReleaseTree {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [string]$ExpectedChannel
    )

    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Release root is missing: $fullRoot"
    }

    $metadataRoot = Join-Path $fullRoot 'release'
    $manifestPath = Join-Path $metadataRoot "$ExpectedVersion.json"
    $checksumPath = Join-Path $metadataRoot "$ExpectedVersion.sha256"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Release manifest is missing: $manifestPath"
    }
    $metadataFiles = @(Get-ChildItem -LiteralPath $metadataRoot -Recurse -File)
    $expectedMetadata = @(
        [IO.Path]::GetFullPath($manifestPath),
        [IO.Path]::GetFullPath($checksumPath)
    )
    if ($metadataFiles.Count -ne 2 -or $metadataFiles.Where({
                -not $expectedMetadata.Contains([IO.Path]::GetFullPath($_.FullName))
            }).Count -ne 0) {
        throw "Release metadata directory must contain only the version manifest and checksum"
    }

    try {
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    }
    catch {
        throw "Release manifest is invalid JSON: $($_.Exception.Message)"
    }
    if ([string]$manifest.schemaVersion -ne '1' -or
        [string]$manifest.product -ne 'OneHistoryStudio' -or
        [string]$manifest.version -ne $ExpectedVersion) {
        throw "Release manifest identity does not match OneHistoryStudio $ExpectedVersion"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedChannel) -and
        [string]$manifest.channel -ne $ExpectedChannel) {
        throw "Release manifest channel is $($manifest.channel), expected $ExpectedChannel"
    }

    $checksums = Read-ReleaseChecksumMap $checksumPath
    $manifestMap = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($artifact in @($manifest.artifacts)) {
        $resolved = Resolve-ReleaseArtifactPath $fullRoot ([string]$artifact.file)
        $relative = [string]$resolved.RelativePath
        if ($manifestMap.ContainsKey($relative)) {
            throw "Release manifest contains a duplicate artifact: $relative"
        }
        $manifestMap.Add($relative, $artifact)
        if (-not (Test-Path -LiteralPath $resolved.FullPath -PathType Leaf)) {
            throw "Release artifact is missing: $relative"
        }

        $file = Get-Item -LiteralPath $resolved.FullPath
        $hash = (Get-FileHash -LiteralPath $resolved.FullPath -Algorithm SHA256).Hash
        if ([long]$artifact.bytes -ne $file.Length -or [string]$artifact.sha256 -ne $hash) {
            throw "Release artifact does not match manifest: $relative"
        }
        if (-not $checksums.ContainsKey($relative) -or $checksums[$relative] -ne $hash) {
            throw "Release artifact does not match checksum file: $relative"
        }
    }
    if ($manifestMap.Count -eq 0) {
        throw "Release manifest contains no artifacts"
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -File | Where-Object {
        -not $_.FullName.StartsWith(
            [IO.Path]::GetFullPath($metadataRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
                [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($actualFiles.Count -ne $manifestMap.Count -or $checksums.Count -ne $manifestMap.Count) {
        throw "Release file set differs from manifest/checksum (actual=$($actualFiles.Count), manifest=$($manifestMap.Count), checksum=$($checksums.Count))"
    }
    foreach ($file in $actualFiles) {
        $relative = Get-ReleaseRelativePath $file.FullName $fullRoot
        if (-not $manifestMap.ContainsKey($relative)) {
            throw "Release tree contains an untracked artifact: $relative"
        }
    }
}

function Invoke-DirectoryPromotion {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$Quarantine,
        [Parameter(Mandatory = $true)][scriptblock]$ValidateRelease,
        [scriptblock]$AfterBackup
    )

    foreach ($unusedPath in @($Backup, $Quarantine)) {
        if (Test-Path -LiteralPath $unusedPath) {
            throw "Promotion transaction path already exists: $unusedPath"
        }
    }
    if (-not (Test-Path -LiteralPath $Candidate -PathType Container)) {
        throw "Promotion candidate is missing: $Candidate"
    }

    $candidatePromoted = $false
    try {
        & $ValidateRelease $Candidate
        if (Test-Path -LiteralPath $Destination) {
            Move-Item -LiteralPath $Destination -Destination $Backup
        }
        if ($null -ne $AfterBackup) {
            & $AfterBackup
        }
        Move-Item -LiteralPath $Candidate -Destination $Destination
        $candidatePromoted = $true
        & $ValidateRelease $Destination
    }
    catch {
        $promotionError = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()

        try {
            if ($candidatePromoted -and (Test-Path -LiteralPath $Destination)) {
                Move-Item -LiteralPath $Destination -Destination $Quarantine
            }
            elseif (Test-Path -LiteralPath $Candidate) {
                Move-Item -LiteralPath $Candidate -Destination $Quarantine
            }
        }
        catch {
            $rollbackErrors.Add("quarantine failed: $($_.Exception.Message)")
        }

        try {
            if (Test-Path -LiteralPath $Backup) {
                if (Test-Path -LiteralPath $Destination) {
                    throw "destination still exists"
                }
                Move-Item -LiteralPath $Backup -Destination $Destination
            }
        }
        catch {
            $rollbackErrors.Add("restore failed: $($_.Exception.Message)")
        }

        if ($rollbackErrors.Count -gt 0) {
            throw "Promotion failed: $($promotionError.Exception.Message); rollback incomplete: $($rollbackErrors -join '; ')"
        }
        throw $promotionError
    }
}

function Assert-ModulePackage {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [string]$ExpectedChannel
    )

    $fullRoot = [IO.Path]::GetFullPath($Root)
    $packageDocs = @(Get-ChildItem -LiteralPath (Join-Path $fullRoot 'package') -Filter '*.md' -File -ErrorAction SilentlyContinue)
    if ($packageDocs.Count -ne 1) {
        throw "Module package must contain exactly one API Markdown document"
    }
    $apiRelative = 'package/' + $packageDocs[0].Name
    $expectedFiles = @(
        'OneHistoryStudio.dll',
        'OneHistoryStudio.xml',
        'module.manifest.json',
        $apiRelative,
        'SHA256SUMS'
    )
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Module package root is missing: $fullRoot"
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $fullRoot -Recurse -File | ForEach-Object {
        Get-ReleaseRelativePath $_.FullName $fullRoot
    } | Sort-Object)
    $expectedSorted = @($expectedFiles | Sort-Object)
    if (($actualFiles -join "`n") -ne ($expectedSorted -join "`n")) {
        throw "Module package file set is invalid. Actual: $($actualFiles -join ', ')"
    }

    $manifestPath = Join-Path $fullRoot 'module.manifest.json'
    try {
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    }
    catch {
        throw "Module manifest is invalid JSON: $($_.Exception.Message)"
    }
    if ([string]$manifest.name -ne 'OneHistoryStudio' -or
        [string]$manifest.version -ne $ExpectedVersion -or
        [string]$manifest.artifact -ne 'OneHistoryStudio.dll' -or
        [string]$manifest.docs -ne 'OneHistoryStudio.xml' -or
        [string]$manifest.mcpExposure -ne 'readonly' -or
        $manifest.ui -ne $true) {
        throw "Module manifest identity does not match OneHistoryStudio $ExpectedVersion"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedChannel) -and
        [string]$manifest.channel -ne $ExpectedChannel) {
        throw "Module manifest channel is $($manifest.channel), expected $ExpectedChannel"
    }

    $checksumPath = Join-Path $fullRoot 'SHA256SUMS'
    $checksums = Read-ReleaseChecksumMap $checksumPath
    $hashedFiles = @($expectedFiles | Where-Object { $_ -ne 'SHA256SUMS' } | Sort-Object)
    if ($checksums.Count -ne $hashedFiles.Count) {
        throw "Module checksum must cover exactly $($hashedFiles.Count) files"
    }
    foreach ($relative in $hashedFiles) {
        if (-not $checksums.ContainsKey($relative)) {
            throw "Module checksum is missing $relative"
        }
        $resolved = Resolve-ReleaseArtifactPath $fullRoot $relative
        $hash = (Get-FileHash -LiteralPath $resolved.FullPath -Algorithm SHA256).Hash
        if ($checksums[$relative] -ne $hash) {
            throw "Module checksum mismatch: $relative"
        }
    }

    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $fullRoot 'OneHistoryStudio.dll'))
    if ($assembly.Version.ToString() -ne "$ExpectedVersion.0") {
        throw "Module assembly version is $($assembly.Version), expected $ExpectedVersion.0"
    }
}
