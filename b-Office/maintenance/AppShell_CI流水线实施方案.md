# AppShell CI 流水线实施方案

| 项 | 值 |
|---|---|
| 文档性质 | 内部工程实践方案，不随消费合同发布 |
| 适用基线 | `3.0.3`（冻结契约） |
| 文档日期 | 2026-07-29 |
| 当前状态 | **已实施，本地门禁通过；待首轮远端 CI** |
| 承载分支 | `2026-023-AppShell` |

## 0. 一句话定义

把《冻结执行证据》第 3 节的框架门禁从"人工按需执行"变成"每次推送自动执行"，用机器守住冻结契约不漂移。

## 1. 为什么现在做

冻结项目改动少，看似不需要 CI。恰恰相反：

1. **冻结的承诺是"行为不变"，而防漂移正是机器最擅长、人最容易疏漏的事。** 四份 `PublicAPI.Unshipped.txt` 必须保持基线，这条在冻结审查中靠人工核对了两轮；机器核对成本为零且永不遗忘。
2. **有两个下游 pin 死版本消费。** `2026-022-WBall` 与 `2026-020-OneHistoryStudio` 均以 `PackageReference 3.0.3` 精确消费，任何 3.0.x 补丁的回归由它们承担后果。违约成本是别人付的。
3. **审查会有盲区，测试不会自己退化——前提是有东西持续跑它。** 冻结前审查的 FZR-01 读路径遗漏，根因是验收标准按"命令形态"枚举而非按"敏感值全部出口"推导。已写成测试的断言只有在持续执行时才构成保护。
4. **AppShell 是三个项目中唯一 CI-ready 的。** 已具备 xunit（标准 TRX 输出）、`dotnet format` 门禁、PublicAPI Analyzer、锁定还原。本方案不引入任何新工具，只是把既有命令搬到 runner 上。

## 2. 前提事实

以下事实在 2026-07-29 核实，构成方案的设计依据：

| 事实 | 值 | 对方案的影响 |
|---|---|---|
| 仓库拓扑 | 40 个项目共用 `cyx3705/OneHistory`，**每项目一个分支**，各自 worktree 检出 | workflow 放在本分支只在本分支推送时触发，天然隔离，无需路径过滤 |
| 分支根 | `2026-023-AppShell` 分支的根即项目根，`AppShell.sln` 在根 | workflow 无需 `working-directory` 前缀 |
| 锁文件 | 6 个项目的 `packages.lock.json` 齐全 | `--locked-mode` 可用，还原可复现 |
| `global.json` | **不存在** | 本机用 SDK 9.0.315 构建 net8.0；CI 需显式固定 SDK，见 §5.1 |
| 门禁耗时（本机 Release） | 构建 8.4s + 测试 22s，76/76 PASS | 单轮 CI 预计 4–6 分钟（含 runner 启动与还原） |
| 目标框架 | 全部 `net8.0` / `net8.0-windows`，Shell 与 App 用 WPF | 必须 `windows-latest` runner |
| 现有 CI | 无 | 从零开始 |

## 3. 范围与非目标

### 3.1 阶段一范围（本次实施）

复刻《冻结执行证据》第 3 节的六条门禁命令，外加一条公开面基线断言。

### 3.2 阶段二范围（3.1 开启时实施）

消费方契约冒烟：打包 → 本地 feed → 下游分支还原构建。见 §6。

### 3.3 明确非目标

- **`Publish-AppShell.ps1` 绝不进入 CI。** 它要求干净工作树、写入不可变归档、更新正式 feed 与 `z-Package-AppShell` 快照——这是发布动作而非验证动作，自动触发的后果不可逆。CI 只做只读门禁。
- 不引入新的测试框架、分析器或格式规则；CI 不得成为新的质量标准来源。
- 不做自动发版、自动打 tag、自动推 NuGet.org。
- 不改动任何生产代码或既有门禁命令的语义。

## 4. 触发策略

```yaml
on:
  push:
    branches: [2026-023-AppShell]
  workflow_dispatch:
```

- 仅本分支推送触发，其余 39 个项目分支不受影响。
- 保留手动触发入口，便于在不产生提交的情况下复验。
- **不使用 `pull_request` 触发**：本仓库采用分支即项目的拓扑，分支之间从不合并，无 PR 流程。CI 只能推送后报告，不能阻断合入——这是本方案能力边界，见 §8。

## 5. 阶段一：冻结门禁

### 5.1 先决动作：固定 SDK 版本

本机以 SDK 9.0.315 构建 `net8.0` 目标。`dotnet format` 与分析器诊断在不同 SDK 版本间存在行为差异，若 CI 与本机 SDK 不一致，会出现"本机过、CI 挂"或反之的假信号。

实施前在仓库根新增 `global.json`：

```json
{
  "sdk": {
    "version": "9.0.315",
    "rollForward": "latestPatch"
  }
}
```

固定 SDK 后，CI 与本机使用同一工具链，格式门禁与分析器结果可比。此文件同时使下游维护者复现构建时获得一致行为。

### 5.2 workflow 文件

路径：`.github/workflows/appshell-freeze-gate.yml`

```yaml
name: AppShell Freeze Gate

on:
  push:
    branches: [2026-023-AppShell]
  workflow_dispatch:

jobs:
  gate:
    runs-on: windows-latest
    timeout-minutes: 30

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Restore (locked)
        run: dotnet restore .\AppShell.sln --locked-mode

      - name: Build Debug
        run: dotnet build .\AppShell.sln -c Debug --no-restore

      - name: Test Debug
        run: >
          dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj
          -c Debug --no-build --no-restore
          --logger "trx;LogFileName=debug.trx"
          --results-directory artifacts\test-results

      - name: Build Release
        run: dotnet build .\AppShell.sln -c Release --no-restore

      - name: Test Release
        run: >
          dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj
          -c Release --no-build --no-restore
          --logger "trx;LogFileName=release.trx"
          --results-directory artifacts\test-results

      - name: Format gate
        run: dotnet format .\AppShell.sln --verify-no-changes --no-restore

      - name: Public API baseline gate
        shell: pwsh
        run: .\eng\Assert-PublicApiBaseline.ps1

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: artifacts\test-results
```

设计说明：

- **不设置 `DOTNET_EnableWriteXorExecute=0`。** 该变量是本机 Roslyn 的规避措施，runner 上不需要；若 CI 出现相关失败再单独评估，不预先携带本机特殊配置。
- **Debug 与 Release 双配置全跑。** 冻结执行证据要求两个配置均 0 warning、全部用例 PASS，CI 必须覆盖同样口径。
- **`--logger trx` 并上传制品。** 使 CI 逐条渲染用例结果，失败时不必回溯 stdout。这是自研 harness 无法提供、xunit 免费获得的能力。
- **`if: always()` 上传。** 失败时的结果最需要保留。

### 5.3 公开面基线断言脚本

路径：`eng/Assert-PublicApiBaseline.ps1`

冻结契约要求四个包的 `PublicAPI.Unshipped.txt` 只含 `#nullable enable` 基线行。PublicAPI Analyzer 只保证"新增公开面必须登记"，**不保证"不得新增公开面"**——后者是冻结特有的约束，必须单独断言。

```powershell
#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projects = @(
    'AppShell.Core'
    'AppShell.Services'
    'AppShell.ServiceHost'
    'AppShell.Shell'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$violations = @()

foreach ($project in $projects) {
    $path = Join-Path $repoRoot "b-Code-AppShell\src\$project\PublicAPI.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $path)) {
        $violations += "$project : PublicAPI.Unshipped.txt 缺失"
        continue
    }

    $entries = @(
        Get-Content -LiteralPath $path -Encoding UTF8 |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )

    if ($entries.Count -ne 0) {
        $violations += "$project : 检测到 $($entries.Count) 条未登记公开面 -> $($entries -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host '公开面冻结门禁失败:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host '3.0.x 为冻结契约,不得新增公开 API。若确需演进,应开 3.1 并单独评审公开面。'
    exit 1
}

Write-Host "公开面冻结门禁通过: $($projects.Count) 个包的 Unshipped 均为基线。"
```

脚本可独立本地执行，与 CI 使用同一份实现，不形成第二真值。

## 6. 阶段二：消费方契约冒烟（3.1 开启时）

阶段一守住的是"AppShell 自身没坏"。真正的冻结价值在于"下游脚下没变"，这需要跨分支验证：

1. `dotnet pack` 生成 3.0.x 四包到临时目录；
2. 以该目录为本地 NuGet feed；
3. `actions/checkout` 用 `ref: 2026-022-WBall` 与 `ref: 2026-020-OneHistoryStudio` 分别检出到独立子目录；
4. 覆写下游 `nuget.config` 指向本地 feed，还原并构建；
5. 构建通过即视为消费契约未破。

分支即项目的拓扑对此天然友好——`actions/checkout` 支持同一 job 内多次检出不同 `ref` 到不同 `path`。

**不在阶段一做的理由**：耗时约为阶段一的三倍，且 3.0.3 已冻结、下游已完成消费复验（见 WBall `v3.4_AppShell_3.0.3冻结消费复验`）。该门禁的价值在 3.1 开始改动框架时才兑现。

## 7. 已知风险与处置

### 7.1 WPF 用例在 runner 上的稳定性（**最大技术风险**）

`DockingContractTests` 与 `FreezeBlockerTests` 会创建真实 WPF 对象、构造视觉树并泵送 Dispatcher，其中包含对主文档实际宽度的断言。

有利条件：这两个文件**自建 STA 线程**（`thread.SetApartmentState(ApartmentState.STA)`）并自行泵送消息，不依赖测试运行器的单元状态，可移植性优于依赖 runner 默认 apartment 的写法。

风险：`windows-latest` runner 虽有桌面会话，但无交互式登录，涉及实际布局测量与渲染的用例存在不稳定可能。

处置顺序：

1. **首次运行按现状全跑**，把稳定性当作待测事实而非假设；
2. 若 UI 用例在 runner 上不稳定，给这两个文件加 `[Trait("Category", "UI")]`，CI 用 `--filter "Category!=UI"` 排除，UI 用例保留为本地门禁；
3. **不得因 runner 不稳定而删除或弱化用例**——它们守护的是中央工作区契约。

### 7.2 格式门禁的 SDK 敏感性

已由 §5.1 的 `global.json` 处置。若未固定 SDK 而直接实施，`dotnet format` 极可能在 CI 上报出本机不存在的差异。

### 7.3 首次实施可能暴露既有问题

CI 首次运行有一定概率失败于本机从未触发的条件（干净还原、无本机缓存、无 `DOTNET_EnableWriteXorExecute`）。**这是 CI 的价值而非故障**：它正是在证明"干净机器能否构建"这一本机无法自证的命题。首次失败应作为真实缺陷处理，不得靠放宽门禁绕过。

## 8. 能力边界

必须明确记录，避免高估 CI 的保护力：

- **不能阻断**。本仓库无 PR 流程，CI 只能在推送后报告。防止问题进入分支仍依赖开发纪律。
- **不覆盖发布正确性**。打包、归档、feed 提升由 `Publish-AppShell.ps1` 承担，且刻意排除在 CI 之外。
- **不覆盖下游**（阶段一）。跨分支消费契约在阶段二才闭合。

## 9. 配额与成本

- WPF 强制 `windows-latest`，私有仓库按 **2 倍分钟数**计费。
- GitHub Free 额度 2000 分钟/月 → 实际约 **1000 Windows 分钟**。
- 单轮预计 4–6 分钟 → 约 **170–250 次推送/月**，对单人开发节奏充足。
- `timeout-minutes: 30` 防止挂起用例耗尽额度。
- 若额度趋紧，优先降低触发频率（改为仅 `workflow_dispatch` 与 tag 推送），而非削减门禁项。

## 10. 实施验收清单

- [x] 新增 `global.json` 固定 SDK 至 9.0.315，本机复跑六条门禁确认无行为变化
- [x] 新增 `eng/Assert-PublicApiBaseline.ps1`，本机执行通过
- [x] 故意在任一 `PublicAPI.Unshipped.txt` 追加一行伪条目，确认脚本以非零码失败，随后还原（验证门禁非恒真）
- [x] 新增 `.github/workflows/appshell-freeze-gate.yml`
- [ ] 推送后首轮 CI 结果记录在案：通过 / 失败原因 / 是否触发 §7.1 或 §7.3
- [ ] 确认其余 39 个项目分支未被触发
- [ ] TRX 制品可下载且包含 Debug/Release 逐项结果
- [ ] 本方案状态更新为"已实施"，结果回填《冻结执行证据》

## 11. 与既有文档的关系

- 门禁命令的**权威源仍是**《冻结执行证据》第 3 节。本方案是它的自动化投影，不得成为第二真值；两者若出现分歧，以执行证据为准并同步修正 workflow。
- 本文件属内部维护文档，不进入版本化消费 `docs/`，不随 NuGet 包发布。
