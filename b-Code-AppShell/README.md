# AppShell

AppShell 是 OHS 伞形项目中的通用桌面应用框架组件，也是框架源码的唯一真值。

## 结构

- `src/AppShell.Core`：指令、数据、停靠、日志和存储契约。
- `src/AppShell.Services`：日志、SQLite、设置、工作区、MCP 与模块托管实现。
- `src/AppShell.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `src/AppShell.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `docs`：框架需求、演进纪律和 `AppShell版本记录.md`。

## 构建

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
```

V2.4.0 起，OHS 直接通过 `ProjectReference` 使用本目录源码，不再维护框架副本或执行哈希回灌。

## 0.7.2 本地包

0.5.0 是首个固定版本包基线；0.7.2 将动态模块界面统一为普通工具窗口，并保留拖动、浮动、停靠和最大化。桌面消费者引用 Shell，
服务化消费者额外引用 ServiceHost：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="0.7.2" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="0.7.2" />
```

包发布到仓库内 `z-Package-AppShell/feed`，OHS 自身仍使用 `ProjectReference`。打包与验收入口：

```powershell
# 可覆盖 staging：构建、审计、隔离消费和演示发布
.\eng\Publish-AppShell.ps1 -Version 0.7.2

# 不可覆盖的正式本地 feed；要求 b-Code-AppShell 已提交且路径干净
.\eng\Publish-AppShell.ps1 -Version 0.7.2 -Publish
```

脚本不会执行 Git commit/tag/push，也不会推送 NuGet.org。包结构、许可边界和消费说明见
`PACKAGE.md`；0.6 系列契约见 `docs/AppShell-0.6.0-服务宿主与会话层.md`，发布基线见
`docs/AppShell-0.5.0-发布与质量整备.md`，0.7 系列的模块界面契约见
`docs/AppShell-0.7.2-统一工具窗口模型.md`。0.7.0 和 0.7.1 文档保留为历史记录，不再作为新模块接入依据。
