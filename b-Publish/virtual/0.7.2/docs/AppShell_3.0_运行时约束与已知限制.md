> **Virtual publish validation only; this is not a formal compatibility contract.**
> Package version: 0.7.2. The document body comes from the AppShell 3.0.0 consumer-contract source and validates only the release-generation pipeline. Compatibility with 0.7.2 is not asserted.
# AppShell 3.0 运行时约束与已知限制

适用版本：`3.0.x`。本文件记录消费应用必须遵守的运行时约束、默认值和已知限制。

## 运行时边界

| 边界 | 结论 |
|---|---|
| 无界集合 | 运行时日志缓冲、文件写入队列、控制台显示与入站队列、命令历史均有上限；Web 限流窗口、前端目录缓存和 MCP 会话缓存已增加可配置上限与清理点。命令注册表、设置及治理记录属于显式配置/持久资产，边界见下文。 |
| 未释放资源 | `McpGateway`、`WebGateway` 的监听器和 `CancellationTokenSource` 均在 Stop/Dispose 释放；WebSocket 会话、文件监视器、去抖定时器、日志流及模块加载上下文均有退出路径。 |
| 硬编码 | MCP/Web 默认端口按稳定应用名派生并可显式覆盖；端口重试、限流、缓存容量、命令历史、控制台容量、MCP 执行/确认超时均可配置。协议报文 1 MiB 上限和 Web 前端中继 15 秒超时是 3.0 安全契约，不作为业务调优项。 |
| 跨应用共享资源 | `%AppData%/<应用名>/`、ServiceHost 本地互斥体、MCP/Web 默认端口均按稳定应用名隔离；端口冲突自动顺延。相同 `ServiceName` 的 ServiceHost 仍为有意的单实例服务。 |
| null 降级路径 | `Workspace=null` 时不注册 `res.*` 且资源窗口不接管；`CommandSelection=null` 时 Shell 创建本地状态；MCP/提示词治理不依赖数据库；前端离线时前端目录仍可查阅，执行返回明确失败。 |
| 线程亲和 | 窗口、布局、面板、对话框和控制台命令均声明 `RequiresUiThread`；CommandBus 统一编组到 `UiContext`。ModuleHost 只通过注入的 `SynchronizationContext` 创建/销毁 UI，无窗服务保持 null。 |

## 默认值

| 设置键 | 默认值 | 说明 |
|---|---:|---|
| `mcp.port` | 未设置时 `8737 + stableHash(appName) % 200` | 显式值优先 |
| `mcp.portretries` | `20` | 初始端口冲突后的顺延次数，范围 `0..100` |
| `mcp.sessionlimit` | `1024` | MCP 会话缓存上限，范围 `16..65536` |
| `web.port` | 未设置时 `8938 + stableHash(appName) % 200` | 显式值优先 |
| `web.portretries` | `20` | 初始端口冲突后的顺延次数，范围 `0..100` |
| `web.ratelimit` | `120` | 单会话每分钟请求数，范围 `10..10000` |
| `web.ratewindowlimit` | `4096` | 限流窗口缓存上限，范围 `128..65536` |
| `web.frontendcataloglimit` | `32` | 离线前端目录缓存上限，范围 `1..256` |
| `console.buffer` | `50000` | 控制台行和入站事件上限，最小 `1000` |
| `console.history` | `500` | 手输命令历史上限，最小 `10` |

## 已知限制

1. `SettingsService` 接受应用自定义键，`PromptGovernanceStore` 保存修订、提案、纠错和事故的完整审计链。这两类持久资产不做静默淘汰；长期自动化宿主应由运维流程归档旧设置和治理文件。框架只限制查询返回量，不擅自删除审计证据。
2. 同一应用名的多个桌面实例共享 `%AppData%/<应用名>/`。布局和设置采用原子覆盖但不提供跨进程事务；日志文件被另一实例占用时，该实例可能只保留内存/控制台日志。需要并行运行隔离实例时，应使用不同的稳定应用名。
3. 前端目录按 `FrontendName` 持久缓存。超过 `web.frontendcataloglimit` 时会确定性淘汰其它离线应用目录；在线会话目录不受影响，重新连接会再次发布。
4. `ShellServiceClient` 在建立 WebSocket 时发布目录。运行期间动态注册的新命令需重连后进入服务端权威目录；AppShell 的内置命令、启动期应用命令和启动期模块命令均在连接前完成注册。

遇到上述限制时，应记录根因和复现；禁止在消费方复制命令清单、删除布局文件或改写框架状态来掩盖问题。
