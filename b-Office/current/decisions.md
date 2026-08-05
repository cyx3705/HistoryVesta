# V4 有效决定

| 编号 | 决定 | 状态 |
| --- | --- | --- |
| V4-001 | V4 在 `2026-018-MyAPI` 独立探索，与 AppShell 3.0.3 隔离 | 生效 |
| V4-002 | `b-Code-MyAPI-Lite` 上抛并改名为 `b-Code-MyAPI`；内部工程已拆分为 MyAPI.Abstractions、MyAPI.Runtime 和 MyAPI.Host | 生效 |
| V4-003 | 旧 `b-Code-MyAPI-Core` 删除，不作为 V4 兼容层 | 生效 |
| V4-004 | 一个页面对应一个前端模块；AppShell/Web 作为页面宿主层 | 生效 |
| V4-005 | MCP、HTTP 监听和模块扫描默认关闭，由消费者显式启用 | 已实现 |
| V4-006 | 能力程序集与 MyAPI 注册适配分离；能力可脱离注册器直接消费 | 已实现 |
| V4-007 | 所有协议适配统一调用 `ICommandDispatcher`，不再从 public 方法自动生成命令 | 已实现 |
| V4-008 | 测试能力和注册适配外置为根级 `b-Code-TestModule`，生产内核不内置示例模块 | 已实现 |
| V4-009 | OHS、AppShell 等上层模块的实验性源码统一暂存于根级 `Module/`；不进入 V4 发布包 | 已实现 |
| V4-010 | AppShell V4 迁移为单项目桌面 Shell 前端，删除命令总线、MyAPI 后端引用和前后端适配层 | 已实现 |

## 未决事项

权限模型、审计、跨进程传输、模块签名/依赖策略、版本兼容和正式包格式仍需单独评审。
