# AppShell 3.0

本仓库是 OneHistory AppShell 的独立源码、合同与发布资产真值。`3.0.3` 是 V3 最终冻结基线，
冻结标签为 `v3.0.3`；版本线不再与 OneHistoryStudio 对齐，`0.7.x` 仅保留用于回滚。

## AI 与维护入口

| 入口 | 用途 |
| --- | --- |
| [AI 工作合同](AGENTS.md) | 读取顺序、真值、冻结与修改边界 |
| [项目清单](project.manifest.json) | 项目身份、活动路径、命令、归档和上下文排除项 |
| [项目概览](b-Office/current/项目概览.md) | 目标、范围、冻结状态与最近验证 |
| [技术合同](b-Office/current/技术合同.md) | 现行需求、架构和不变量 |
| [验证合同](b-Office/current/验证合同.md) | 本地、CI、包和消费方门禁 |
| [文档中心](b-Office/文档中心.md) | current、package、history 与发布资产边界 |

常规维护不要扫描 `b-Office/history/`、`b-Publish/` 或生成目录；跨项目消费直接读取
`z-Package-AppShell/AppShell.reuse.md`。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `b-Code-AppShell/` | Core、Services、Shell、ServiceHost、演示宿主与测试 |
| `b-Code-Samples/` | 模块开发示例 |
| `b-Office/package/` | 消费文档编辑源 |
| `b-Office/current/` | 现行项目、验证、升级与发布合同 |
| `b-Code-AppShell/eng/release/` | 发布清单和生成模板等机器输入 |
| `b-Office/` | 冻结契约、内部设计与执行证据 |
| `b-Publish/current/` | 唯一一份可覆盖的当前候选和完整发布测试结果 |
| `b-Publish/history/<版本>/` | 与当时 Z 快照同构的最小正式历史副本 |
| `z-Package-AppShell/` | 当前发布快照的展开内容，不保存历史版本目录 |

根级 `AppShell.sln` 是仓库验收入口，只包含六个冻结项目；组件目录内的
`b-Code-AppShell/AppShell.sln` 是发布脚本使用的等价入口。

## 构建与测试

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --no-restore
dotnet format .\AppShell.sln --verify-no-changes --no-restore
```

## 审核候选与正式发布

```powershell
# 重建 b-Publish/current 下的可覆盖审核候选
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.3

# 仅在审核通过、代码和消费文档均已提交且干净后执行
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.3 -Publish

# 使用 b-Publish/history 中的历史包验证发布生成链，只更新 b-Publish/virtual
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish

# 测试/回滚时将已验证快照的内部内容整体替换到 z 根目录
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish -DeployToZ
```

审核阶段的消费方必须临时指向
`b-Publish/current/packages`；正式提升后改用 `z-Package-AppShell/feed`。`z-Package-AppShell`
始终只保留一个当前发布快照；正式发布后，同一最小快照按版本写入 `b-Publish/history/`。
发布脚本不会执行 Git commit、tag、push，也不会推送 NuGet.org。

桌面消费者通常引用 `OneHistory.AppShell.Shell`；服务化宿主额外引用
`OneHistory.AppShell.ServiceHost`。当前已验证消费方为 OneHistoryStudio（020）和 WBall（022）。

维护入口见 [b-Office/文档中心.md](b-Office/文档中心.md)。其他项目和 AI 先读取
`z-Package-AppShell/AppShell.reuse.md`，再按需索引同一当前快照中的 `z-Package-AppShell/docs/`；历史版本文档与发布证据从 `b-Publish` 查阅。
