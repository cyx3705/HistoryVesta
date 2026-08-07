[CmdletBinding()]
param(
    [switch]$Instantiation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$componentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $componentRoot '..'))
$errors = [Collections.Generic.List[string]]::new()

function Add-ContractError {
    param([string]$Message)
    $script:errors.Add($Message)
}

function Resolve-ContractPath {
    param(
        [string]$RelativePath,
        [string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        Add-ContractError "$Context must be a non-empty repository-relative path: $RelativePath"
        return $null
    }

    $fullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $RelativePath))
    $rootPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Add-ContractError "$Context points outside the repository: $RelativePath"
        return $null
    }
    return $fullPath
}

function Test-RequiredProperty {
    param(
        [object]$Object,
        [string]$Name,
        [string]$Context
    )

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-ContractError "$Context is missing property '$Name'."
        return $false
    }
    return $true
}

$requiredFiles = @(
    'AGENTS.md'
    'README.md'
    '.ignore'
    'project.manifest.json'
    'b-Code-AppShell/README.md'
    'b-Code-AppShell/eng/Test-ProjectContract.ps1'
    'b-Code-AppShell/eng/Assert-PublicApiBaseline.ps1'
    '.github/workflows/appshell-freeze-gate.yml'
)
foreach ($relativePath in $requiredFiles) {
    $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context 'Required file'
    if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        Add-ContractError "Missing required file: $relativePath"
    }
}

$manifestPath = Join-Path $repoRoot 'project.manifest.json'
$manifest = $null
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    Add-ContractError "project.manifest.json is invalid: $($_.Exception.Message)"
}

$markdownFiles = [Collections.Generic.List[string]]::new()
$markdownFiles.Add((Join-Path $repoRoot 'README.md'))
$markdownFiles.Add((Join-Path $repoRoot 'b-Office\README.md'))
$markdownFiles.Add((Join-Path $repoRoot 'b-Office\文档中心.md'))
$markdownFiles.Add((Join-Path $repoRoot 'b-Office\OneHistoryAppShell\README.md'))

if ($null -ne $manifest) {
    if ((Test-RequiredProperty $manifest 'schemaVersion' 'manifest') -and $manifest.schemaVersion -ne 1) {
        Add-ContractError "Unsupported schemaVersion: $($manifest.schemaVersion)"
    }
    foreach ($name in @('template', 'project', 'paths', 'documents', 'commands', 'contextExclusions')) {
        $null = Test-RequiredProperty $manifest $name 'manifest'
    }

    if ($null -ne $manifest.template) {
        if ((Test-RequiredProperty $manifest.template 'isTemplate' 'template') -and
            $manifest.template.isTemplate -isnot [bool]) {
            Add-ContractError 'template.isTemplate must be a Boolean.'
        }
        elseif ($Instantiation -and $manifest.template.isTemplate) {
            Add-ContractError 'Instantiation validation requires template.isTemplate=false.'
        }
    }

    if ($null -ne $manifest.project) {
        foreach ($name in @('id', 'name', 'title', 'kind', 'status', 'version', 'branch', 'freezeTag')) {
            if (Test-RequiredProperty $manifest.project $name 'project') {
                $value = @($manifest.project.$name)
                if ($value.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$value[0])) {
                    Add-ContractError "project.$name cannot be empty."
                }
            }
        }
        if ($manifest.project.id -ne '2026-023' -or $manifest.project.name -ne 'AppShell') {
            Add-ContractError 'Project identity must be 2026-023/AppShell.'
        }
        # The V3 freeze tag stays v3.0.3. From 3.1 the version line may move forward,
        # but project.version must always match AppShellVersion.props (DEC-009).
        # Keep this file ASCII only: it has no BOM, so Windows PowerShell would
        # decode non-ASCII bytes with the system code page and fail to parse.
        if ($manifest.project.freezeTag -ne 'v3.0.3') {
            Add-ContractError 'V3 freeze tag must remain v3.0.3.'
        }
        $versionPropsPath = Join-Path $repoRoot 'b-Code-AppShell\AppShellVersion.props'
        if (Test-Path $versionPropsPath) {
            $sourceVersion = ([xml](Get-Content $versionPropsPath -Raw)).Project.PropertyGroup.AppShellVersion
            if ([string]$sourceVersion -ne [string]$manifest.project.version) {
                $versionMismatch = "project.version ($($manifest.project.version)) must match AppShellVersion.props ($sourceVersion)."
                Add-ContractError $versionMismatch
            }
        }
        else {
            Add-ContractError 'b-Code-AppShell\AppShellVersion.props is missing.'
        }
    }

    if ($null -ne $manifest.paths) {
        foreach ($name in @('activeRoots', 'sourceRoots', 'generatedRoots', 'archiveRoots',
                'allowedRootDirectoryPrefixes')) {
            $null = Test-RequiredProperty $manifest.paths $name 'paths'
        }
        foreach ($relativePath in @($manifest.paths.activeRoots) + @($manifest.paths.sourceRoots)) {
            $fullPath = Resolve-ContractPath -RelativePath ([string]$relativePath) -Context 'Declared directory'
            if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
                Add-ContractError "Declared directory does not exist: $relativePath"
            }
        }

        $allowedPrefixes = @($manifest.paths.allowedRootDirectoryPrefixes)
        $allowedNames = @($manifest.paths.allowedRootDirectoryNames)
        foreach ($expectedPrefix in @('a-', 'b-', 'z-')) {
            if ($allowedPrefixes -notcontains $expectedPrefix) {
                Add-ContractError "paths.allowedRootDirectoryPrefixes must include '$expectedPrefix'."
            }
        }
        foreach ($directory in Get-ChildItem -LiteralPath $repoRoot -Directory -Force) {
            if ($directory.Name.StartsWith('.')) {
                continue
            }
            $allowed = @($allowedPrefixes | Where-Object {
                $directory.Name.StartsWith([string]$_, [StringComparison]::OrdinalIgnoreCase)
            }).Count -ne 0
            if (-not $allowed) {
                $allowed = @($allowedNames | Where-Object {
                    $directory.Name.Equals([string]$_, [StringComparison]::OrdinalIgnoreCase)
                }).Count -ne 0
            }
            if (-not $allowed) {
                Add-ContractError "Root directory is not allowed by the naming contract: $($directory.Name)"
            }
        }
    }

    if ($null -ne $manifest.documents) {
        foreach ($property in $manifest.documents.PSObject.Properties) {
            $relativePath = [string]$property.Value
            $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context "documents.$($property.Name)"
            if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
                Add-ContractError "Declared document does not exist: $relativePath"
            }
            elseif ($relativePath.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
                $markdownFiles.Add($fullPath)
            }
        }
    }

    if ($null -ne $manifest.commands) {
        foreach ($name in @('setup', 'build', 'test', 'verify', 'run', 'package')) {
            if (Test-RequiredProperty $manifest.commands $name 'commands') {
                $value = $manifest.commands.$name
                if ($null -ne $value -and ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value))) {
                    Add-ContractError "commands.$name must be a non-empty string or null."
                }
            }
        }
    }
}

$linkPattern = [regex]'\[[^\]]+\]\((?<target>[^)]+)\)'
foreach ($filePath in @($markdownFiles | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        continue
    }
    $content = Get-Content -LiteralPath $filePath -Raw -Encoding UTF8
    foreach ($match in $linkPattern.Matches($content)) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if ($target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            continue
        }
        $pathPart = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ([string]::IsNullOrWhiteSpace($pathPart)) {
            continue
        }
        $linkedPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $filePath) $pathPart))
        if (-not (Test-Path -LiteralPath $linkedPath)) {
            $relativeFile = $filePath.Substring($repoRoot.Length + 1)
            Add-ContractError "Broken Markdown link: $relativeFile -> $target"
        }
    }
}

$placeholderPattern = [regex]'\{\{[^{}\r\n]+\}\}'
$strictFiles = @(
    Join-Path $repoRoot 'README.md'
    Join-Path $repoRoot 'project.manifest.json'
    Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Office\current') -Filter '*.md' -File |
        ForEach-Object FullName
)
foreach ($filePath in $strictFiles) {
    $content = Get-Content -LiteralPath $filePath -Raw -Encoding UTF8
    if ($placeholderPattern.IsMatch($content)) {
        Add-ContractError "Instantiation placeholder remains in: $($filePath.Substring($repoRoot.Length + 1))"
    }
}

if ($errors.Count -ne 0) {
    Write-Host "Project contract validation failed with $($errors.Count) error(s):" -ForegroundColor Red
    foreach ($contractError in $errors) {
        Write-Host "  - $contractError" -ForegroundColor Red
    }
    exit 1
}

$mode = if ($Instantiation) { 'instantiation' } else { 'project' }
Write-Host "Project contract validation passed ($mode mode)." -ForegroundColor Green
