# HistoryVulcan 3.2.0

本仓库是 OneHistory HistoryVulcan（原 AppShell，3.2.0 起改名）的独立源码、合同与发布资产真值。
`3.0.3` 是 V3 冻结基线，冻结标签为 `v3.0.3`；版本线不再与 HistoryJanus 对齐，`0.7.x` 仅保留用于回滚。

当前正式版本为 `3.2.0`：在 3.1.10 共享命令目录会话基础上执行产品改名，项目、命名空间、程序集、
宿主 EXE 与消费文档统一为 HistoryVulcan；不新增功能或改变命令语义。
`3.1.8` 仅是内部过渡版本，不作为稳定支持版本；`3.1.9` 是旧名 AppShell 的最后快照。
3.1.10 对“轻松指令”和中央命令集做了内部高内聚重构：
两种交互共享由 `CommandBus` 驱动的目录快照、详情缓存、检索和选择状态，不新增公开 API 或改变命令语义。
控制台聚焦时输入框上方显示命令、参数名和允许值候选，
`Shift+W`/`Shift+S` 上下选择、`Tab` 写入当前候选而不执行；普通布局由控制台输入直接检索中央命令集，
命令集不再保留独立搜索框；3.1.7 建立的 UI 风格合同继续统一嵌入页面的色板、字体、字号、
圆角、间距、控件尺寸、顶栏归属和响应式验收规则。HistoryVulcan 采用单 EXE 双进程运行模型，后台服务承载
命令、模块、日志和全局快捷键，WPF 前端只负责窗口与 UI 模块。双 `/` 唤出并聚焦控制台；前端关闭默认隐藏而不停止后台。
删除 HistoryVulcan
内置资源/Workspace 与演示电机页，并保留 `ModulesView` 作为唯一模块管理页面。资源浏览未来由独立模块提供；
全局 `HistoryVesta\\*\\z-*` 扫描不在本版本范围。HistoryVulcan 独立可执行宿主显式启用模块生命周期与模块管理页；
消费方仍按最小能力原则自行决定是否启用，Janus 不再维护第二套模块宿主或管理页面。V3.1/V3.2 方案与实施记录已归档到
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
`z-HistoryVulcan/HistoryVulcan.reuse.md`。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `b-Code-HistoryVulcan/` | Core、Services、Shell、ServiceHost、演示宿主与工程脚本 |
| `b-Code-Tests/` | HistoryVulcan 回归、布局、命令、安全与包合同测试 |
| `b-Code-Samples/` | 模块开发示例 |
| `b-Office/package/` | 消费文档与嵌入页面 UI 风格合同编辑源 |
| `b-Office/current/` | 现行项目、验证、升级与发布合同 |
| `b-Code-HistoryVulcan/eng/release/` | 发布清单和生成模板等机器输入 |
| `b-Office/` | 冻结契约、内部设计与执行证据 |
| `b-Publish/current/` | 唯一一份可覆盖的当前候选和完整发布测试结果 |
| `b-Publish/history/<版本>/` | 与当时 Z 快照同构的最小正式历史副本 |
| `z-HistoryVulcan/` | 当前发布快照的展开内容，不保存历史版本目录 |

根级 `HistoryVulcan.sln` 是仓库验收入口，只包含六个冻结项目；组件目录内的
`b-Code-HistoryVulcan/HistoryVulcan.sln` 是发布脚本使用的等价入口。

## 构建与测试

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Debug --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\HistoryVulcan.sln -c Release --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Release --no-build --no-restore
dotnet format .\HistoryVulcan.sln --verify-no-changes --no-restore
```

## 宿主候选与正式部署

```powershell
# 生成 b-Publish/current 下的宿主 + 文档完整候选
.\b-Code-HistoryVulcan\eng\Publish-HistoryVulcanHost.ps1 -Version 3.2.0

# 候选审核通过后，将同一完整快照一次性部署到 z-HistoryVulcan
.\b-Code-HistoryVulcan\eng\Publish-HistoryVulcanHost.ps1 -Version 3.2.0 -DeployToZ
```

当前交付物是可直接运行的 HistoryVulcan 宿主，不是 NuGet 包。历史四包发布脚本只保留用于库消费兼容、
历史验证和回滚，不是 3.2.0 宿主部署入口：

```powershell
# 使用 b-Publish/history 中的历史包验证发布生成链，只更新 b-Publish/virtual
.\b-Code-HistoryVulcan\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish

# 测试/回滚时将已验证快照的内部内容整体替换到 z 根目录
.\b-Code-HistoryVulcan\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish -DeployToZ
```

宿主候选位于 `b-Publish/current`。当前正式快照为 3.2.0：运行入口 `z-HistoryVulcan/host/HistoryVulcan.exe`，
UI 风格合同 `z-HistoryVulcan/docs/HistoryVulcan_UI风格与嵌入页面规范.md`。
旧名 `z-Package-AppShell/` 的 3.1.9 快照已随 3.2.0 发布退役删除（同构副本入库于 `b-Publish/history/3.1.9/`）。
旧候选整体归档到 `b-Publish/history/<版本>/`。宿主部署脚本不会执行 Git commit、tag、push，
也不会生成或推送 NuGet 包。

桌面消费者通常引用 `OneHistory.HistoryVulcan.Shell`；服务化宿主额外引用
`OneHistory.HistoryVulcan.ServiceHost`。当前已验证消费方为 HistoryJanus（020）和 WBall（022）。

维护入口见 [b-Office/文档中心.md](b-Office/文档中心.md)。其他项目和 AI 先读取
`z-HistoryVulcan/HistoryVulcan.reuse.md`，再按需索引同一当前快照中的 `z-HistoryVulcan/docs/`；历史版本文档与发布证据从 `b-Publish` 查阅。
