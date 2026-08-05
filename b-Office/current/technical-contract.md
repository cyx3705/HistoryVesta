# MyAPI V4 技术合同

## 当前分层

- `MyAPI.Abstractions`：模块、命令描述、参数、上下文、结果和调用接口；能力程序集可以独立引用，不依赖宿主。
- `MyAPI.Runtime`：显式命令注册、原子快照替换、重复 ID 拦截、取消、超时和统一错误结果。
- `MyAPI.Host`：可选 HTTP/MCP 适配和 DLL 模块加载；宿主默认空闲，HTTP 默认只绑定回环地址。
- `b-Code-TestModule/MyAPI.TestCapability`：完全独立的测试能力程序集，可脱离 MyAPI 直接消费。
- `b-Code-TestModule/MyAPI.TestModule`：把测试能力显式注册到 MyAPI 的外置薄适配层；只用于契约和冒烟验证，不属于生产内核。

## 命令边界

命令 ID、模块 ID、参数描述和结果状态属于跨前端合同。AppShell、Web、Console、HTTP 和 MCP 都只能调用同一个 `ICommandDispatcher`，不能分别实现一套业务逻辑。

注册必须显式调用 `ICommandRegistry.Register`。公共方法不会因为“是 public”就自动暴露，避免意外扩大攻击面和 API 面。

## 默认安全行为

- `EnableHttp=false`、`EnableMcp=false`、`EnableModules=false`、`EnableHotReload=false`。
- 未显式开启 HTTP 时，`MyAPI.Host` 直接退出，不监听端口。
- HTTP 默认绑定 `127.0.0.1`；绑定非回环地址必须同时设置 `AllowRemote=true`。
- MCP 只能在 HTTP 开启后显式开启。
- 模块目录仅在 `EnableModules=true` 时扫描；热重载还需要 `EnableHotReload=true`。入口程序集使用 `*Module.dll` 命名，依赖 DLL 不作为模块入口扫描。

## 原型限制

当前仍缺少正式权限提供器、审计后端、模块签名校验和跨进程身份传播。这些是承载大量上层前必须完成的下一阶段，不在本切片中伪装为已解决。
