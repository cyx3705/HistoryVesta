# MyAPI V4 复用说明

## 当前可消费范围

当前提供 V4 实验性源码和外置测试模块，不提供稳定 NuGet 兼容承诺。`b-Code-TestModule/MyAPI.TestCapability` 证明能力程序集可以脱离 MyAPI 直接消费；需要模块化运行时的消费者再引用 `MyAPI.Abstractions`、`MyAPI.Runtime` 和自己的薄注册适配层。测试模块本身不作为生产包发布。

## 接入原则

- 不复制 AppShell 或 OHS 源码作为底座。
- 前端页面通过命令合同调用后端模块；不要直接引用 `MyAPI.Host` 内部类型。
- HTTP、MCP、模块扫描和监听默认关闭；消费者必须在启动配置中显式启用。
- `MyAPI.Host` 是协议和 DLL 加载适配器，不是业务能力容器；上层也可以完全绕过它，直接使用 `MyAPI.Runtime`。
- 正式消费前必须完成权限、审计、版本和错误合同评审。
- `Module/` 仅供 OHS、AppShell 等上层模块实验性建构；不能把其中代码当作稳定包或正式消费合同。

## 与 3.0 的隔离

本说明不改变 `2026-023-AppShell` 3.0.3 的包、API 或冻结状态。任何替换关系都需要新的版本决策和消费者验证。
