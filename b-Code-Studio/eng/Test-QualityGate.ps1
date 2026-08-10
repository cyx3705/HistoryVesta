[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# HistoryJanus 日常质量门禁（VERIFY-FAST 组成部分）。
# 定位：代码管道化条件 4 —— 漂移由日常检查自动阻断，而不是积累到正式发布才暴露。
# 权威源上游：JanusVersion.props（版本）、module.manifest.json（模块身份）、
# z-HistoryJanus（正式树边界）、2026-023-HistoryVulcan z 级快照（宿主合同）。
#
# 注意：所有收集结果必须经 @(...) 包装；单个违规项在 Windows PowerShell 5.1 下是标量，
# 直接读 .Count 会得到 $null 并静默绕过失败分支（2026-08 在 HistoryVulcan 同类脚本中实证）。

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$componentRoot = Join-Path $root 'b-Code-Studio'
$activeRoots = @('b-Code-Studio', 'b-Code-Verify')
$excluded = '\\(bin|obj|Unused|b-Publish|z-HistoryJanus)\\'

$violations = [System.Collections.Generic.List[string]]::new()

# --- 1. 抑制标记零容忍：NoWarn/SuppressMessage/#pragma disable 一律不得入库。
#        唯一豁免：工程文件中仅抑制 XML 文档警告 CS1573/CS1591 的 NoWarn 行 -------------
$suppressionPattern = 'NoWarn|SuppressMessage|#pragma\s+warning\s+disable'
$docWarningWhitelist = @('CS1573', 'CS1591')
foreach ($relativeRoot in $activeRoots) {
    $path = Join-Path $root $relativeRoot
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $files = Get-ChildItem -LiteralPath $path -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.targets' -and $_.FullName -notmatch $excluded }
    foreach ($file in $files) {
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $lineNumber++
            if ($line -notmatch $suppressionPattern) { continue }
            $codes = @([regex]::Matches($line, '[A-Z]{2}\d{4}') | ForEach-Object Value)
            $effective = @($codes | Where-Object { $_ -notin $docWarningWhitelist })
            if ($line -match 'NoWarn' -and $effective.Count -eq 0) { continue }
            $violations.Add("Suppression token: $($file.FullName):$lineNumber")
        }
    }
}

# --- 2. 生产代码行数上限（与平台 1000 行一致；超出即按职责拆分） ------------------------
$hotspots = @(
    Get-ChildItem -LiteralPath $componentRoot -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.xaml' -and $_.FullName -notmatch $excluded } |
        ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Lines = ([IO.File]::ReadAllLines($_.FullName)).Count } } |
        Where-Object Lines -gt 1000 |
        Sort-Object Lines -Descending
)
foreach ($hotspot in $hotspots) {
    $violations.Add("Hotspot over 1000 lines: $($hotspot.Path) ($($hotspot.Lines) lines); split by responsibility.")
}

# --- 3. 版本身份链一致：props -> manifest -> 技术合同现行声明 ----------------------------
$versionPropsPath = Join-Path $componentRoot 'JanusVersion.props'
[xml]$versionProps = [IO.File]::ReadAllText($versionPropsPath)
$sourceVersion = [string]$versionProps.Project.PropertyGroup.HistoryJanusVersion
if ([string]::IsNullOrWhiteSpace($sourceVersion)) {
    $violations.Add("JanusVersion.props does not declare HistoryJanusVersion")
}
# 宿主契约版本的唯一真源同样是 JanusVersion.props，脚本不再各自硬编码字面量。
$requiredVulcan = [string]$versionProps.Project.PropertyGroup.RequiredHistoryVulcanVersion
if ([string]::IsNullOrWhiteSpace($requiredVulcan)) {
    $violations.Add("JanusVersion.props does not declare RequiredHistoryVulcanVersion")
}

$manifestPath = Join-Path $componentRoot 'Module\module.manifest.json'
$manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
if ([string]$manifest.version -ne $sourceVersion) {
    $violations.Add("module.manifest.json version $($manifest.version) != JanusVersion.props $sourceVersion")
}

$contractPath = Join-Path $root 'b-Office\current\技术合同.md'
$contractText = [IO.File]::ReadAllText($contractPath)
if ($contractText -notmatch "当前开发版本为 ``?$([regex]::Escape($sourceVersion))``?") {
    $violations.Add("技术合同.md 未声明当前开发版本 $sourceVersion")
}

# 运行时 Status 必须是程序集投影，不允许版本字面量回流进源码
$statusSource = [IO.File]::ReadAllText((Join-Path $componentRoot 'Module\HistoryJanusCommands.cs'))
if ($statusSource -match '"[^"]*\d+\.\d+\.\d+[^"]*"') {
    $violations.Add('HistoryJanusCommands.cs contains a hardcoded version literal; project from the assembly instead')
}

# --- 4. 正式树边界（QA-004 日常化）：z 级快照只允许五类条目 -----------------------------
$packageRoot = Join-Path $root 'z-HistoryJanus'
if (Test-Path -LiteralPath $packageRoot) {
    $allowed = @('HistoryJanus.dll', 'HistoryJanus.xml', 'module.manifest.json', 'SHA256SUMS', 'docs')
    $unexpected = @(
        Get-ChildItem -LiteralPath $packageRoot |
            Where-Object { $_.Name -notin $allowed }
    )
    foreach ($item in $unexpected) {
        $violations.Add("Unexpected entry in z-HistoryJanus: $($item.Name)")
    }
    $packageDoc = Join-Path $packageRoot 'docs'
    if ((Test-Path -LiteralPath $packageDoc) -and
        @(Get-ChildItem -LiteralPath $packageDoc -File).Count -ne 1) {
        $violations.Add('z-HistoryJanus/docs must contain exactly one API document')
    }
}

# --- 5. 宿主合同预检：发布脚本同源检查日常化 --------------------------------------------
$vulcanRoot = [IO.Path]::GetFullPath((Join-Path $root '..\2026-023-HistoryVulcan\z-HistoryVulcan'))
$vulcanManifestPath = Join-Path $vulcanRoot 'manifest.json'
$vulcanCorePath = Join-Path $vulcanRoot 'host\HistoryVulcan.Core.dll'
if (-not (Test-Path -LiteralPath $vulcanManifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $vulcanCorePath -PathType Leaf)) {
    $violations.Add("HistoryVulcan formal snapshot is incomplete: $vulcanRoot")
}
else {
    $vulcanManifest = [IO.File]::ReadAllText($vulcanManifestPath) | ConvertFrom-Json
    if ([string]$vulcanManifest.product -ne 'HistoryVulcan' -or [string]$vulcanManifest.version -ne $requiredVulcan) {
        $violations.Add("Janus requires the HistoryVulcan $requiredVulcan formal host; found $($vulcanManifest.version)")
    }
    $vulcanCore = [Reflection.AssemblyName]::GetAssemblyName($vulcanCorePath)
    if ($vulcanCore.Version.ToString() -ne "$requiredVulcan.0") {
        $violations.Add("HistoryVulcan.Core identity is $($vulcanCore.Version), expected $requiredVulcan.0")
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host ("Quality gate passed: suppressions 0; hotspots {0}; version {1}; host HistoryVulcan {2}." -f $hotspots.Count, $sourceVersion, $requiredVulcan)
