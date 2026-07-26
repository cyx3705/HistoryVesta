# 2026-019 Studio 工具箱

本项目存放可独立构建并通过 OneHistoryStudio 自动发现、同步和注册的工具模块。
模块源码使用 `b-Code-*` 目录；`z-*` 目录只保存 OHS 工具清单等注册元数据，不承载业务源码。

## 模块

| 源码目录 | 注册目录 | 模块 | MCP 档位 | 功能 |
|---|---|---|---|---|
| `b-Code-ToolKit` | `z-Module` | `ToolKit` | `standard` | SHA-256、Base64、GUID 和时间戳 |
| `b-Code-ProjectPulse` | `z-ProjectPulse` | `ProjectPulse` | `readonly` | 工作树概况、最近修改和大文件热点 |
| `b-Code-ToolRelay` | `z-ToolRelay` | `ToolRelay` | `readonly` | 当前 Codex 任务动态发现并转发调用新 MCP 工具 |

每个注册目录包含一份固定名称的 `module.manifest.json`。OHS 的 `tool.scan` 沿项目库中的一级
`z/Z` 元文件夹发现这些清单；`tool.sync` 将声明的 Release 产物复制到独立模块槽并记录来源、
版本和 SHA-256，AppShell 模块宿主随后自动热重载。

## 构建与同步

```powershell
dotnet build .\b-Code-ToolKit\ToolKit.csproj -c Release -p:NuGetAudit=false
dotnet build .\b-Code-ProjectPulse\ProjectPulse.csproj -c Release -p:NuGetAudit=false
dotnet build .\b-Code-ToolRelay\ToolRelay.csproj -c Release -p:NuGetAudit=false
```

```text
tool.scan
tool.sync name=ToolKit
tool.sync name=ProjectPulse
tool.sync name=ToolRelay
module.list
```

ProjectPulse 的 MCP 冒烟脚本位于 `z-ProjectPulse/mcp-smoke.txt`。新模块同步后，程序内指令立即
可用；Codex 需要新建任务刷新 MCP 工具目录。
