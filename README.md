# MyAPI

MyAPI 是 OneHistory 的后端模块运行时探索项目。当前从 `b-Code-MyAPI/` 中的 Lite 原型开始，目标是形成模块注册器、命令管线以及本地/HTTP/MCP 调用适配；页面由独立前端模块提供，AppShell 和 Web 只承担页面宿主层。用于验收模块接入的测试能力和注册适配已外置到根级 `b-Code-TestModule/`。

本项目处于 V4 exploration 状态，不影响已冻结的 `2026-023-AppShell` 3.0.3，也不提供稳定 API 或正式发布包。

## 入口

- [AI 工作合同](AGENTS.md)
- [项目清单](project.manifest.json)
- [文档中心](b-Office/document-center.md)
- [项目概览](b-Office/current/overview.md)
- [技术合同](b-Office/current/technical-contract.md)
- [V4 探索模型](b-Office/current/v4-exploration.md)
- [复用说明](b-Office/package/reuse.md)
- [模块开放说明](Module/模块开放说明.md)

## 当前构建

```powershell
dotnet restore .\b-Code-MyAPI\MyAPI.sln --locked-mode
dotnet build .\b-Code-MyAPI\MyAPI.sln -c Release --no-restore
dotnet run --project .\b-Code-MyAPI\tests\MyAPI.ContractTests\MyAPI.ContractTests.csproj -c Release --no-build
```

旧 Lite 工程已被拆分为合同、运行时、宿主和契约测试项目；外置测试模块用于验证真实的跨目录模块消费，不属于生产内核或发布内容。V4 API 仍处于探索态，待边界稳定后再评审正式 NuGet 命名。

OHS、AppShell 等上层模块的实验性源码和工程暂存于 `Module/`。该目录用于逐步建构和验证模块，不是正式发布快照。
