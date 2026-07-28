# MCP 接入与安全

> 适用版本：OneHistoryStudio V2.6.0
> 框架基线：AppShell 0.6.1

## 当前架构

V2.5.0 起，MCP 由常驻的 `OneHistoryStudio.Service.exe` 承载，不再依赖 WPF 前端存活。
AppShell `ServiceHost` 装配网关、提示词治理、命令目录与模块宿主；前端通过 8738 命令 API 接入同一总线。

| 责任 | AppShell 框架 | OneHistoryStudio |
|---|---|---|
| 网关和 JSON-RPC | 创建、启动/停止、鉴权、超时、结果编码 | 不重复实现 |
| 命令来源 | 内置指令、模块指令、MCP/命令/治理指令 | 注册 `proj.*`、`git.*`、`tool.*` 等业务指令 |
| 只读策略 | 解释硬排除规则与模块清单档位 | 每条命令在注册处用 `Readonly = true` 自描述 |
| 数据与审计 | 要求 `DataService`，可使用框架默认审计器 | 提供 SQLite 数据服务并复用 `HistoryRecorder` |
| 身份 | 从 `ApplicationIdentity` 生成握手信息 | 提供 OneHistoryStudio 的名称和唯一版本 |
| 管理窗口 | 接管 `mcp`、`commanddetail`、`modules` 的内容 | 只声明窗口在本产品中的默认停靠位置和共享选中状态 |

`ShellConfig.EnableMcp` 默认是 `true`。若派生应用未提供 `DataService`，框架会告警并跳过网关与提示词治理，避免形成无法审计的半装配状态。

## 启动与状态

网关在全部应用指令和模块装载完成后自动监听。启动 OneHistoryStudio 后先在本地控制台确认：

```text
mcp.status
```

显式配置 `mcp.autostart=false`、手动停止或启动失败后，可用以下指令恢复：

```text
mcp.start
mcp.stop
```

默认地址：

```text
http://127.0.0.1:8737/mcp
```

服务只绑定 `127.0.0.1`，只接受 `POST /mcp`，不会监听局域网网卡。端口允许范围是 1024 至 65535。

未配置 `mcp.autostart` 时按 `true` 处理；只有明确设置为 `false` 才关闭启动时监听。
自动启动失败不会阻止主程序打开，失败原因会写入控制台，修正端口或配置后可执行 `mcp.start`。

## Codex 接入

先启动 OneHistoryStudio 和 MCP 网关，再在终端注册：

```powershell
codex.cmd mcp add onehistory --url http://127.0.0.1:8737/mcp
codex.cmd mcp get onehistory
codex.cmd mcp list
```

Codex 新增或修改 MCP 注册后需要重启客户端。OneHistoryStudio 内部的 `tools/list` 每次按当前注册表、策略和模块状态重新计算，但 Codex 通常按任务缓存工具列表；模块热重载后应新建任务再验证新工具。

## 配置项

配置通过本地主机的 `app.get` / `app.set` 管理：

| 键 | 默认/允许值 | 作用 |
|---|---|---|
| `mcp.port` | `8737` | 下次 `mcp.start` 使用的端口 |
| `mcp.policy` | `readonly` / `standard` | 控制普通指令的暴露范围 |
| `mcp.token` | 空 | 非空时要求完全匹配的 Bearer Token |
| `mcp.timeout` | 120 秒，夹取 5~3600 | 单次工具响应等待时间 |
| `mcp.confirm` | `deny` / `host` | 危险指令拒绝或宿主人工确认 |
| `mcp.confirmtimeout` | 60 秒，夹取 10~600 | 宿主确认等待时间 |
| `mcp.autostart` | `true`，可设 `false` | 控制宿主启动时是否自动监听 |

示例：

```text
app.get key=mcp.policy
app.set key=mcp.policy value=readonly
app.set key=mcp.policy value=standard
app.set key=mcp.token value=<共享令牌>
app.set key=mcp.autostart value=true
app.set key=mcp.confirm value=host
app.set key=mcp.confirmtimeout value=60
```

令牌为空仅适合本机回环联调。不要用端口转发、代理或防火墙映射把无令牌服务暴露到局域网或公网。设置令牌后，客户端必须发送 `Authorization: Bearer <token>`。

## 暴露策略

`readonly` 只暴露**自描述为只读**的命令（`CommandDescriptor.Readonly = true`），以及清单声明为 `readonly` 的模块命令。`standard` 额外暴露不需要本地确认的普通动作命令。

以下命令始终硬排除：

- `app.exit`：远程客户端不能退出宿主；
- `debug.*`：调试与承压命令不远程开放；
- `mcp.*`：防止客户端递归管理或关闭自身网关；
- 清单声明 `mcpExposure=hidden` 的模块命令。

### GitHub 账号命令边界（V2.7.2）

`github.status`、`github.accounts` 和 `github.test` 是服务器端只读事实，可按当前 MCP 策略返回已经脱敏的
提交身份、凭据账号名、origin 和连接诊断。`github.login`、`github.logout`、`github.identity` 和
`github.remote` 只允许服务器本机 Shell；即使远程调用方持有 Web/MCP token，也不能启动 GCM、修改
Git 配置或改写远端。

OHS 不接受 PAT/密码参数，不读取 SSH 私钥正文。外部工具输出先经过 token、Bearer、URL userinfo、
password/secret 赋值和私钥块脱敏，再进入命令结果、日志或 HTTP/MCP 响应。

### 只读性由命令自描述（V2.4.4）

只读性是**命令自身的事实**，与 `ConfirmPrompt`（危险）、`SupportsUndo`、`RequiresUiThread` 同级，
在注册处一并声明：

```csharp
registry.Register(new CommandDescriptor
{
    Name = "proj.list",
    Summary = "列出全部项目工作树",
    Readonly = true,          // ← 只读性在此声明，MCP 档位随即生效
    Handler = ...,
});
```

**新增只读指令只需改注册点一处**，不必再去任何名单登记。当前全仓共 32 条核心只读命令
（框架 18 + Studio 14），全部以此方式声明。

判定优先级（`McpExposurePolicy.State`）：

```text
硬排除 → hidden
ConfirmPrompt != null → dangerous
Readonly == true → readonly
其余 → standard
```

> **历史说明**：0.4.4~V2.4.3 期间曾用按名字匹配的白名单（框架基线 + `RegisterReadonly` 登记）。
> V2.4.3 完成 32 条自描述迁移后，白名单已 100% 冗余，V2.4.4 予以清空并删除
> `AppMcpPolicy`。`RegisterReadonly` 方法保留为兜底通道，仅用于**无法修改注册点**的场景
> （如第三方程序集提供的描述符）；正常开发不要使用它。
> 模块命令的 `mcpExposure` 走另一条通道（`ModuleExposure` 委托），不受本次收口影响。

## 危险指令确认

带 `ConfirmPrompt` 的命令被视为危险命令：

- `mcp.confirm=deny`：默认模式，危险命令不进入 `tools/list`，直接调用也会拒绝。
- `mcp.confirm=host` 且 `mcp.policy=standard`：危险命令进入工具列表，调用时 OneHistoryStudio 弹出宿主确认框。
- 人工点“允许执行”后，框架用预批准作用域执行一次；点“拒绝”或超时均不执行。
- 同一时刻只显示一个远程确认，多个请求按序等待。
- `--yes` 只影响本地 UI、脚本和自动化，永远不能绕过 MCP 宿主确认。

确认框会显示客户端、业务确认文案和等价命令。即使某条命令因输入条件不需要弹框，处理器仍会执行自身的保护分支、路径和状态校验。

## 协议与结果

网关实现 JSON-RPC 2.0 的 Streamable HTTP 无状态子集，支持 `initialize`、`ping`、`tools/list` 和 `tools/call`。请求体必须是 UTF-8 JSON，最大 1 MiB。

协议清单仅含 `2025-06-18` 与 `2025-03-26`。`initialize` 请求清单外版本时回落到
`2025-06-18`；后续请求的无效 `MCP-Protocol-Version` 头返回 HTTP 400，缺失头按
`2025-03-26` 兼容。客户端身份存入 `Mcp-Session-Id` 会话；不回送会话头的旧客户端按
底层连接降级，审计名称不会再在并发客户端之间串用。

## Web API 默认安全

- Web 档空 token 或错 token均返回 401；本机 Shell 使用独立可信客户端档。
- `web.bind` 缺省 `127.0.0.1`；非回环绑定必须先设置非空 `web.token`。
- CORS 缺省关闭，`web.cors` 只接受显式来源白名单。
- `web.confirm` 缺省 `local`，Web 危险命令直接拒绝；设为 `web` 后才通过事件流请求并由
  `/api/confirm` 应答，60 秒无应答按拒绝。
- 每会话有分钟级限流，默认 120 次，可用 `web.ratelimit` 调整。

每个工具调用最终都被还原为命令文本并经同一个 `CommandBus` 执行。返回结果包含：

1. `CommandResult.Message` 文本（`content[0]`）；
2. 成功且存在 `CommandResult.Data` 时追加的 JSON 结构化文本（`content[1]`）；
3. V2.4.5 起同时提供规范字段 `structuredContent`，形如 `{"data": <载荷>}`——
   MCP 规范要求该字段为 JSON 对象，而 `Data` 有数组/对象/字符串三态，故统一 `data` 信封。
   `structuredContent.data` 与 `content[1]` 由同一份序列化文本派生，内容必然一致。

`content[1]` 的 JSON 文本块**按规范保留**（MCP 2025-06-18：返回结构化内容的工具
SHOULD 同时在 TextContent 返回序列化 JSON，向后兼容），消费者应优先读
`structuredContent.data`，读不到再回退扫 `content` 文本块。

响应等待超时只切断 MCP 响应，不会强行撕裂正在执行的宿主命令；命令会继续运行并留痕，之后应使用只读命令查询结果。

## Help、命令目录与提示词治理

```text
help
help command=proj.list
command.list mcp=visible
command.show name=git.rule.list
mcp.schema name=proj.list
```

Help、命令集、指令详情、MCP Schema 和自动生成命令手册都读取同一份 `CommandRegistry` 元数据。模块装卸后，本地主机页面与后续 `tools/list` 会看到新状态。

AI 可以提交描述治理材料，但不能自行批准、应用或回滚：

```text
prompt.propose
correction.propose
incident.record
```

以下动作只允许本地主机界面或控制台完成：

```text
mcp.pending
mcp.approve
mcp.reject
mcp.apply
mcp.revert
```

提示词修订、调用、拒绝、宿主批准、超时和结果都写入本地审计数据。OneHistoryStudio 把现有 `HistoryRecorder` 接给框架，因此不会建立第二套平行审计链。

## 联调与排错

建议按顺序执行：

```text
mcp.start
mcp.status
help command=proj.list
proj.list
module.list
command.list mcp=visible
```

常见问题：

- **客户端无工具**：确认 OneHistoryStudio 仍运行、`mcp.status` 显示监听、URL 是 `/mcp`，然后重启或新建 Codex 任务。
- **401**：宿主设置了 `mcp.token`，客户端没有发送匹配的 Bearer Token。
- **工具数量不符**：检查 `mcp.policy`、`mcp.confirm`、模块 `mcpExposure` 和当前模块装载状态。
- **危险工具不可见**：只有 `standard + host` 才暴露；`deny` 或 `readonly` 下隐藏是预期安全行为。
- **监听失败**：端口可能被另一实例占用；先检查现有进程与 `mcp.status`，不要同时启动两个正式实例。
- **模块已刷新但 Codex 未刷新**：宿主工具表是动态的，客户端缓存不是；新建任务再测。
