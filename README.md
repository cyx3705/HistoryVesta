# AppShell 3.1.7

本仓库是 OneHistory AppShell 的独立源码、合同与发布资产真值。`3.0.3` 是 V3 冻结基线，
冻结标签为 `v3.0.3`；版本线不再与 OneHistoryStudio 对齐，`0.7.x` 仅保留用于回滚。

当前源码为 `3.1.7`：新增随宿主部署的 UI 风格合同，统一嵌入页面的浅色/深色色板、字体、字号、
圆角、间距、控件尺寸、顶栏归属和响应式验收规则；AppShell 采用单 EXE 双进程运行模型，后台服务承载
命令、模块、日志和全局快捷键，WPF 前端只负责窗口与 UI 模块。双 `/` 唤出并聚焦控制台；前端关闭默认隐藏而不停止后台。
删除 AppShell
内置资源/Workspace 与演示电机页，并保留 `ModulesView` 作为唯一模块管理页面。资源浏览未来由独立模块提供；
全局 `OneHistory-Projects\\*\\z-*` 扫描不在本版本范围。AppShell 独立可执行宿主显式启用模块生命周期与模块管理页；
消费方仍按最小能力原则自行决定是否启用，OHS 不再维护第二套模块宿主或管理页面。V3.1/V3.2 方案与实施记录已归档到
`b-Office/history/`；当前规则见 [断头指令审计表](b-Office/current/断头指令审计表.md)。

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
| `b-Code-AppShell/` | Core、Services、Shell、ServiceHost、演示宿主与工程脚本 |
| `b-Code-Tests/` | AppShell 回归、布局、命令、安全与包合同测试 |
| `b-Code-Samples/` | 模块开发示例 |
| `b-Office/package/` | 消费文档与嵌入页面 UI 风格合同编辑源 |
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
dotnet test .\b-Code-Tests\AppShell.Tests\AppShell.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
dotnet test .\b-Code-Tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --no-restore
dotnet format .\AppShell.sln --verify-no-changes --no-restore
```

## 宿主候选与正式部署

```powershell
# 生成 b-Publish/current 下的宿主 + 文档完整候选
.\b-Code-AppShell\eng\Publish-AppShellHost.ps1 -Version 3.1.7

# 候选审核通过后，将同一完整快照一次性部署到 z-Package-AppShell
.\b-Code-AppShell\eng\Publish-AppShellHost.ps1 -Version 3.1.7 -DeployToZ
```

当前交付物是可直接运行的 AppShell 宿主，不是 NuGet 包。历史四包发布脚本只保留用于库消费兼容、
历史验证和回滚，不是 3.1.7 宿主部署入口：

```powershell
# 使用 b-Publish/history 中的历史包验证发布生成链，只更新 b-Publish/virtual
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish

# 测试/回滚时将已验证快照的内部内容整体替换到 z 根目录
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish -DeployToZ
```

宿主候选位于 `b-Publish/current`，正式运行入口位于 `z-Package-AppShell/host/AppShell.exe`，
UI 风格合同位于 `z-Package-AppShell/docs/AppShell_UI风格与嵌入页面规范.md`。
旧候选整体归档到 `b-Publish/history/<版本>/`。宿主部署脚本不会执行 Git commit、tag、push，
也不会生成或推送 NuGet 包。

桌面消费者通常引用 `OneHistory.AppShell.Shell`；服务化宿主额外引用
`OneHistory.AppShell.ServiceHost`。当前已验证消费方为 OneHistoryStudio（020）和 WBall（022）。

维护入口见 [b-Office/文档中心.md](b-Office/文档中心.md)。其他项目和 AI 先读取
`z-Package-AppShell/AppShell.reuse.md`，再按需索引同一当前快照中的 `z-Package-AppShell/docs/`；历史版本文档与发布证据从 `b-Publish` 查阅。
