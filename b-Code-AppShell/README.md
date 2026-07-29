# AppShell

AppShell 是独立维护的通用桌面应用框架，也是框架源码的唯一真值。

## 结构

- `src/AppShell.Core`：指令、停靠、日志、MCP 和存储契约。
- `src/AppShell.Services`：日志、设置、工作区、MCP 与模块托管实现。
- `src/AppShell.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `src/AppShell.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `../b-Office/package`：消费文档编辑源；`../b-Office` 根目录保留冻结合同和内部设计记录。
- `../b-Publish`：候选构建、完整版本归档和发布证据。
- `../z-Package-AppShell`：当前正式四包、精简复用说明和同版本消费合同。

## 构建

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
```

消费方通过固定版本的 `PackageReference` 使用 AppShell，不直接引用本目录源码。

## 3.0.x 包

3.0.3 是当前正式发布基线，已完成 Docking 主工作区比例收口。桌面消费者引用 Shell，
服务化消费者额外引用 ServiceHost。正式切换前继续固定使用当前 Z 级快照声明的版本：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="3.0.0" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="3.0.0" />
```

审核候选写入仓库内 `b-Publish/staging`；审核通过后完整归档到 `b-Publish`，
并用当前正式四包更新 `z-Package-AppShell/feed`。打包与验收入口：

```powershell
# 可覆盖 staging：构建、审计、隔离消费和演示发布
.\eng\Publish-AppShell.ps1 -Version 3.0.3

# 完整归档不可覆盖；同时替换 z-Package-AppShell 的当前正式快照
.\eng\Publish-AppShell.ps1 -Version 3.0.3 -Publish
```

脚本不会执行 Git commit/tag/push，也不会推送 NuGet.org。包结构与许可边界见 `PACKAGE.md`。
完整消费文档由 `../b-Office/package` 生成，归档到 `../b-Publish/docs/<版本>`，并随当前正式快照写入
`../z-Package-AppShell/docs/`，不再重复装入每个 NuGet 包；其他项目和 AI 先读
`../z-Package-AppShell/AppShell.reuse.md`，再按需跟随其中的合同链接。
