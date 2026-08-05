# V4 验证合同

## 结构门禁

- 根目录存在 `AGENTS.md`、`project.manifest.json`、`README.md`、`.ignore`。
- 活动代码为 `b-Code-MyAPI/src`、`b-Code-MyAPI/tests` 和根级 `b-Code-TestModule`；不存在 `b-Code-MyAPI-Core`、`b-Code-MyAPI-Lite` 或内置 `samples`。
- `b-Code-MyAPI` 不提交 `.vs`、`bin`、`obj` 或用户状态文件。
- `b-Code-TestModule` 不属于生产内核或发布内容，并且测试能力程序集不引用 MyAPI。
- manifest 中的路径、命令和文档均可解析。

## 自动验证

```powershell
dotnet restore .\b-Code-MyAPI\MyAPI.sln --locked-mode
dotnet build .\b-Code-MyAPI\MyAPI.sln -c Release --no-restore
dotnet run --project .\b-Code-MyAPI\tests\MyAPI.ContractTests\MyAPI.ContractTests.csproj -c Release --no-build
git diff --check
```

当前零依赖契约测试覆盖：能力脱离 MyAPI 直接调用、显式命令注册、参数拒绝、请求防御性快照、失败替换和失败热重载保持旧快照、超时、宿主默认关闭，共 8 项。

## 模块冒烟

将 `MyAPI.TestModule.dll` 和 `MyAPI.TestCapability.dll` 放入临时 Modules 目录，以 `EnableHttp=true`、`EnableModules=true`、`EnableMcp=true` 启动宿主；已验证 4 个命令可见，HTTP 与 MCP 调用 `test.calculator.add` 均返回 15。外置后的冒烟已重新执行通过；无开关启动仍应立即空闲退出。

权限、审计、签名和跨进程身份测试尚未执行，不能标记为通过。
