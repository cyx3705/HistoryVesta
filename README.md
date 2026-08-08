# HistoryJanus

HistoryJanus V3 是运行在 HistoryVulcan 中的项目与 Git 治理模块。HistoryVulcan 独立负责 Shell、命令总线、模块生命周期、ServiceHost 和 MCP/Web 基础设施；Janus 只注册业务命令和页面。

## 结构

| 目录 | 职责 |
| --- | --- |
| `b-Code-Studio` | Janus 业务源码、模块入口与发布脚本 |
| `b-Code-Verify` | Contracts、功能 Smoke 与 ModuleSmoke |
| `b-Office/current` | 四份现行元文档 |
| `b-Office/package` | 唯一跨项目模块 API 文档 |
| `b-Office/history` | 只读版本记录，不是现行开发输入 |
| `b-Publish` | 单槽候选、历史正式包、事务工作区，不入 Git |
| `z-HistoryJanus` | 最新正式模块消费快照 |

文档入口：[文档中心](./b-Office/文档中心.md)；跨模块入口：[模块 API](./b-Office/package/模块API.md)。

## 构建与验证

```powershell
dotnet restore .\HistoryJanus.sln --locked-mode -p:NuGetAudit=false
dotnet build .\HistoryJanus.sln -c Debug --no-restore -p:NuGetAudit=false
dotnet test .\b-Code-Verify\Contracts\Contracts.csproj -c Debug --no-build --no-restore -p:NuGetAudit=false
.\b-Code-Studio\eng\Test-QualityGate.ps1
dotnet run --project .\b-Code-Verify\ModuleSmoke\ModuleSmoke.csproj -c Debug -- .\b-Code-Studio\Module\bin\Debug\net8.0-windows
```

日常开发执行质量门禁、相关 Contracts、Debug 构建、定向功能 Smoke 与 ModuleSmoke；`Test-QualityGate.ps1`
把抑制标记、千行文件、版本链一致性、正式树边界和宿主合同五项漂移检查日常化（代码管道化条件 4：
漂移由检查自动阻断，不积累到发布）。推送到 `2026-020-HistoryJanus` 分支时，GitHub Actions 门禁
（`.github/workflows/historyjanus-gate.yml`）并行复验锁定还原、双配置构建、Contracts、格式与同一门禁脚本。
正式发布才执行 Debug/Release 全量门禁、正式包、双槽部署和回滚验证。

```powershell
.\b-Code-Studio\eng\Publish-Janus.ps1
.\b-Code-Studio\eng\Publish-Janus.ps1 -Publish
```

正式包只含 `HistoryJanus.dll`、XML、module manifest、checksum 和 `package/模块API.md`，不含 Janus EXE 或 HistoryVulcan 运行库。脚本不测试开机自启动，也不启动 HistoryVulcan。

## AI 工作边界

- 当前事实以源码、测试、`b-Office/current`、`b-Office/package` 和最新 z 级正式快照为准。
- 默认不列举、搜索或读取 `b-Office/history`；只有用户明确追溯版本时才读取指定文件。
- HistoryVulcan 合同只从平级 `2026-023-HistoryVulcan/z-HistoryVulcan` 消费，不复制其源码或文档。
- 不提交 `bin`、`obj`、`.vs` 或 `b-Publish`；z 级正式快照进入 Git。
- 不新建独立 `AGENTS.md`；本节与文档中心共同承担仓库 AI 边界。
