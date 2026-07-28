# AppShell

AppShell 是 OHS 伞形项目中的通用桌面应用框架组件，也是框架源码的唯一真值。

## 结构

- `src/AppShell.Core`：指令、数据、停靠、日志和存储契约。
- `src/AppShell.Services`：日志、SQLite、设置、工作区、MCP 与模块托管实现。
- `src/AppShell.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `src/AppShell.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `../b-Office/appshell`：框架需求、演进纪律和版本记录。
- `../b-Office/versions`：V 版本留存记录，默认不进入 AI 检索与开发基线。

## 构建

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
```

V2.4.0 起，OHS 直接通过 `ProjectReference` 使用本目录源码，不再维护框架副本或执行哈希回灌。

## 2.7.5 本地包

0.5.0 是首个固定版本包基线；从 2.7.2 起 AppShell 与 OHS 使用同一版本列车。2.7.5 收口版本投影、命令管道和停靠布局自愈，继续保留统一工具窗口、拖动、浮动、停靠和最大化。桌面消费者引用 Shell，
服务化消费者额外引用 ServiceHost：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="2.7.5" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="2.7.5" />
```

包发布到仓库内 `z-Package-AppShell/feed`，OHS 自身仍使用 `ProjectReference`。打包与验收入口：

```powershell
# 可覆盖 staging：构建、审计、隔离消费和演示发布
.\eng\Publish-AppShell.ps1 -Version 2.7.5

# 不可覆盖的正式本地 feed；要求 b-Code-AppShell 已提交且路径干净
.\eng\Publish-AppShell.ps1 -Version 2.7.5 -Publish
```

脚本不会执行 Git commit/tag/push，也不会推送 NuGet.org。包结构、许可边界和消费说明见
`PACKAGE.md`；当前服务、模块 UI 和发布合同分别见
`../b-Office/meta/MCP接入与安全.md`、`../b-Office/meta/模块开发手册.md` 和
`../b-Office/meta/发布与升级.md`。旧 0.x 施工文档只可从 Git 历史调查，不得用于新模块接入。
