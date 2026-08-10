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

# --- 4. 消费合同投影：API 版本、窗口和命令必须与当前源码事实一致 --------------------------
$apiPath = Join-Path $root 'b-Office\package\模块API.md'
$apiText = [IO.File]::ReadAllText($apiPath)
if ($apiText -notmatch "(?m)^# HistoryJanus $([regex]::Escape($sourceVersion)) 模块 API$") {
    $violations.Add("模块API.md 标题版本未对齐 $sourceVersion")
}
if ($apiText -notmatch "(?m)^- 版本：``$([regex]::Escape($sourceVersion))``。$") {
    $violations.Add("模块API.md 正式消费版本未对齐 $sourceVersion")
}
if ($apiText -notmatch "(?m)^- 宿主基线：HistoryVulcan ``$([regex]::Escape($requiredVulcan))`` ") {
    $violations.Add("模块API.md 宿主基线未对齐 HistoryVulcan $requiredVulcan")
}

$uiSource = [IO.File]::ReadAllText((Join-Path $componentRoot 'Module\HistoryJanusUiModule.cs'))
$sourceWindows = @(
    [regex]::Matches($uiSource, '(?m)^\s*Id\s*=\s*"(?<id>[a-z][a-z0-9]*)"') |
        ForEach-Object { $_.Groups['id'].Value }
)
if ($sourceWindows.Count -ne 2) {
    $violations.Add("HistoryJanusUiModule.cs must declare exactly 2 windows; found $($sourceWindows.Count)")
}

$windowSection = [regex]::Match($apiText, '(?ms)^## UI 窗口\s*(?<body>.*?)(?=^##\s|\z)')
$apiWindows = @(
    [regex]::Matches($windowSection.Groups['body'].Value, '(?m)^\|\s*`(?<id>[a-z][a-z0-9]*)`\s*\|') |
        ForEach-Object { $_.Groups['id'].Value }
)
if (($sourceWindows -join ',') -cne ($apiWindows -join ',')) {
    $violations.Add("模块API.md 窗口清单 [$($apiWindows -join ', ')] != 源码 [$($sourceWindows -join ', ')]")
}

$businessCommandNames = @(
    Get-ChildItem -LiteralPath $componentRoot -Recurse -Filter '*.cs' -File |
        Where-Object { $_.FullName -notmatch $excluded } |
        ForEach-Object {
            $sourceText = [IO.File]::ReadAllText($_.FullName)
            [regex]::Matches($sourceText, '(?m)^\s*Name\s*=\s*"(?<name>janus\.[a-z0-9]+\.[a-z0-9]+)"') |
                ForEach-Object { $_.Groups['name'].Value }
        }
)
$businessCommandNames = @($businessCommandNames | Sort-Object -Unique)
$expectedRuntimeCommandNames = @($businessCommandNames + 'janus.status' | Sort-Object -Unique)
$apiCommandNames = @(
    [regex]::Matches($apiText, '(?m)^\|\s*`(?<name>janus(?:\.[a-z0-9]+){1,2})`\s*\|') |
        ForEach-Object { $_.Groups['name'].Value } |
        Sort-Object -Unique
)
if ($businessCommandNames.Count -ne 31 -or $expectedRuntimeCommandNames.Count -ne 32) {
    $violations.Add("运行时命令总数应为 32（31 条业务命令 + janus.status）；源码为 $($businessCommandNames.Count) + 1")
}
if (($expectedRuntimeCommandNames -join ',') -cne ($apiCommandNames -join ',')) {
    $violations.Add("模块API.md 命令清单与源码不一致：API $($apiCommandNames.Count)，运行时 $($expectedRuntimeCommandNames.Count)")
}

# --- 5. 正式树边界（QA-004 日常化）：z 级快照只允许五类条目 -----------------------------
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
    $formalManifestPath = Join-Path $packageRoot 'module.manifest.json'
    $formalApiPath = Join-Path $packageDoc '模块API.md'
    if (-not (Test-Path -LiteralPath $formalManifestPath -PathType Leaf)) {
        $violations.Add('z-HistoryJanus/module.manifest.json is missing')
    }
    elseif (-not (Test-Path -LiteralPath $formalApiPath -PathType Leaf)) {
        $violations.Add('z-HistoryJanus/docs/模块API.md is missing')
    }
    else {
        $formalManifest = [IO.File]::ReadAllText($formalManifestPath) | ConvertFrom-Json
        $formalVersion = [string]$formalManifest.version
        $formalApiText = [IO.File]::ReadAllText($formalApiPath)
        if ($formalApiText -notmatch "(?m)^- 版本：``$([regex]::Escape($formalVersion))``。$") {
            $violations.Add("z-HistoryJanus 模块 API 版本未对齐其 manifest $formalVersion")
        }
        if ($formalVersion -eq $sourceVersion -and $formalApiText -cne $apiText) {
            $violations.Add('同版本 z-HistoryJanus/docs/模块API.md != b-Office/package/模块API.md')
        }
    }
}

# --- 6. 宿主合同预检：发布脚本同源检查日常化 --------------------------------------------
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
Write-Host ("Quality gate passed: suppressions 0; hotspots {0}; version {1}; windows {2}; commands {3}; host HistoryVulcan {4}." -f $hotspots.Count, $sourceVersion, $sourceWindows.Count, $expectedRuntimeCommandNames.Count, $requiredVulcan)
