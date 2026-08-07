# AppShell

AppShell 是独立维护的通用桌面应用框架，也是框架源码的唯一真值。

## 结构

- `src/AppShell.Core`：指令、停靠、日志、MCP 和存储契约。
- `src/AppShell.Services`：日志、设置、文件状态、MCP 与模块托管实现。
- `src/AppShell.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `src/AppShell.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `eng`：发布、公开 API 冻结、TRX 失败摘要和 AI-ready 项目合同检查。
- `../b-Office/package`：消费文档编辑源；`../b-Office` 根目录保留冻结合同和内部设计记录。
- `../b-Publish/current`：唯一一份当前候选和完整发布测试结果。
- `../b-Publish/history`：按版本保存的 Z 级最小正式历史副本。
- `../z-Package-AppShell`：当前正式四包、精简复用说明和同版本消费合同。

## 构建

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
```

消费方通过固定版本的 `PackageReference` 使用 AppShell，不直接引用本目录源码。

## 3.1.x 包

3.0.3 是冻结基线；当前源码与候选版本为 3.1.3。桌面消费者引用 Shell，服务化消费者额外引用
ServiceHost。正式消费仍以当前 Z 级快照声明的版本为准；3.1.3 候选验证使用：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="3.1.3" />
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="3.1.3" />
```

审核候选覆盖写入仓库内 `b-Publish/current`；审核通过后先整体更新
`z-Package-AppShell`，再把同一最小快照归档到 `b-Publish/history/<版本>`。打包与验收入口：

```powershell
# 可覆盖 current：构建、审计、隔离消费和演示发布
.\eng\Publish-AppShell.ps1 -Version 3.1.3

# 替换 Z 当前快照，并生成不可覆盖的最小历史副本
.\eng\Publish-AppShell.ps1 -Version 3.1.3 -Publish
```

脚本不会执行 Git commit/tag/push，也不会推送 NuGet.org。包结构与许可边界见 `PACKAGE.md`。
完整消费文档由 `../b-Office/package` 生成，候选位于 `../b-Publish/current/docs/`，正式历史位于
`../b-Publish/history/<版本>/docs/`，并随当前正式快照写入 `../z-Package-AppShell/docs/`；
其他项目和 AI 先读
`../z-Package-AppShell/AppShell.reuse.md`，再按需跟随其中的合同链接。

## 维护门禁

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Assert-PublicApiBaseline.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-ProjectContract.ps1 -Instantiation
```

现行项目规则从 `../project.manifest.json` 和 `../b-Office/current/` 读取；历史施工与冻结证据默认不进入维护上下文。
