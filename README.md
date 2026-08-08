# 2026-019 Studio 工具箱

本项目存放可独立构建并通过 HistoryJanus 自动发现、同步和注册的工具模块。
模块源码使用 `b-Code-*` 目录；`z-*` 目录只保存 OHS 工具清单等注册元数据，不承载业务源码。

## 模块

| 源码目录 | 注册目录 | 物理模块 | MCP 档位 | 承载进程 | 功能 |
|---|---|---|---|---|---|
| `b-Code-StudioTools` | `z-StudioTools` | `StudioTools` | `standard` | 服务 | 聚合 `ProjectPulse`、`ToolKit`、`ToolRelay` 三个逻辑模块 |
| `b-Code-ActiveDock` | `z-ActiveDock` | `ActiveDock` | `standard` | 服务（自持窗口） | 桌面右下角活动项目坞，命令域 `dock` |
| `b-Code-GitHubConnection` | `z-GitHubConnection` | `GitHubConnection` | `readonly` | 桌面（停靠窗口） | 服务器本机 GitHub、GCM、SSH 和 origin 连接治理 |

`StudioTools.dll` 使用 AppShell 支持的多 `ModuleInfoBase` 合同保留三个原逻辑命令域和 10 条命令，
同时只占用一个源码项目、一份发布清单和一个正式模块槽。ActiveDock 与 GitHubConnection 因宿主归属、
安全边界、依赖和发布节奏不同独立维护。四模块聚合期的源码和清单保存在
`Unused/StudioTools-Merge-Legacy-20260802-0015`，不参与发现、构建或发布。

SE2SW 与 SWuse 已先后迁出：`SE2SW`（对外名 Mapping）先迁入 `2026-024-SE2SW`，`SWuse` 随后并入同一项目；
该项目现改名为 **HistoryMinerva**（`2026-024-HistoryMinerva`），两个模块的源码、清单与发布合同以
HistoryMinerva 为唯一来源。

带界面的模块分两类：**停靠型**（`IUiModule + IShellUiAware`，窗口停靠进 OHS 主窗口，由桌面进程承载）与
**自持型**（`IUiModule`，模块自建顶层窗口，由无窗服务宿主承载，关闭主窗口不受影响）。两类模块被同一模块槽
发现、在两个宿主进程都会实例化，因此各自必须对不属于自己的一侧弃权，判据是宿主是否注入 `IShellUiRegistrar`。
详见 OHS `b-Office/package/模块开发手册.md`。

每个注册目录包含一份固定名称的 `module.manifest.json`。OHS 的 `tool.scan` 沿项目库中的一级
`z/Z` 元文件夹发现这些清单；`tool.sync` 将声明的 Release 产物复制到独立模块槽并记录来源、
版本和 SHA-256，AppShell 模块宿主随后自动热重载。

## 构建与同步

```powershell
dotnet build .\b-Code-StudioTools\StudioTools.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-StudioTools\tests\Smoke\Smoke.csproj -c Release -p:NuGetAudit=false
dotnet build .\b-Code-ActiveDock\ActiveDock.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-ActiveDock\tests\Smoke\Smoke.csproj -c Release -p:NuGetAudit=false
dotnet build .\b-Code-GitHubConnection\GitHubConnection.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code-GitHubConnection\tests\Smoke\Smoke.csproj -c Release
```

```text
tool.scan
tool.sync name=StudioTools
tool.sync name=ActiveDock
tool.sync name=GitHubConnection
module.list
```

同步顺序有硬约束：`StudioTools 1.2.0` 必须先于 `ActiveDock 1.0.0` 入槽。旧版 `StudioTools 1.1.0` 仍注册
`dock` 命令域，顺序颠倒会导致两个模块争抢同一命令域而注册失败。

设计与迁移记录：StudioTools 聚合见 `b-Code-StudioTools/docs/01-V1.1.0-四模块聚合迁移.md`，活动坞迁出见
`b-Code-StudioTools/docs/02-V1.2.0-活动坞迁出.md` 与
`b-Code-ActiveDock/docs/01-V1.0.0-活动坞独立模块.md`。新模块同步后程序内指令立即可用；Codex 需要新建任务
刷新 MCP 工具目录。
