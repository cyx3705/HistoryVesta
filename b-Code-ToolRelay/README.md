# ToolRelay 模块设计

> 状态：1.0.1 已实现（清单读取双路径加固，配合 OHS V2.4.5）  
> 当前版本：1.0.1  
> 目标宿主：OneHistoryStudio V2.4.4+ / AppShell 0.4.4+  
> 目标客户端：Codex 当前任务

## 0. 版本记录

| 版本 | 变更 |
|---|---|
| 1.0.0 | 首版，通过 MCP 闭环验收 |
| 1.0.1 | `ToolRelay.List` 的模块清单读取改为双路径：优先 `structuredContent.data`（OHS V2.4.5 规范字段），回退扫 `content` 文本块（兼容 V2.4.4 及更早网关，永久保留）。三条公开方法的 Schema 不变，无需刷新 Codex 任务 |

## 1. 问题

OHS 的模块宿主可以在 `tool.sync` 后自动热重载，新命令会立即进入运行时注册表，MCP 服务端的
下一次 `tools/list` 也能看到它们。但是 Codex 在任务开始时取得工具目录快照，任务运行期间不会因
OHS 新增模块而自动获得新的原生工具入口。

当前自管理循环因此在最后一跳中断：

```text
Codex 写模块
  -> Release 构建
  -> tool.scan / tool.sync
  -> OHS 自动热重载
  -> 新命令进入 MCP tools/list
  -X 当前 Codex 任务仍持旧工具快照
  -> 用户新建任务并重新说明上下文
```

ProjectPulse 已验证这一现象：同一任务内 OHS 已注册 `ProjectPulse.*`，但 Codex 的原生工具目录
没有出现对应工具，只能借助预先存在的 `run` 工具执行脚本。

## 2. 已确认事实

1. `McpGateway.VisibleTools()` 每次调用都从当前 `CommandRegistry` 生成结果，OHS 服务端没有旧快照。
2. OHS 当前实现是 Streamable HTTP 无状态子集，`initialize` 返回的 tools capability 为空对象，
   不声明 `listChanged`，也没有 SSE 或服务端通知通道。
3. OHS V2.2.0 的 CX-04 明确裁决“不做 SSE/listChanged”，以“新任务刷新”作为标准操作。
4. Codex 当前公开手册列出 STDIO、Streamable HTTP、认证和 server instructions 等 MCP 能力，
   没有承诺当前任务会处理工具目录变更通知；新增或修改 MCP 配置仍要求重启客户端。
5. 模块无法获得 Codex 当前任务 ID，也没有被授权控制 Codex 桌面进程、创建任务或自动发送提示词。

因此，1.0 不尝试“重启 Codex”，而是让当前任务不再需要刷新工具目录。

## 3. 方案

新增一个长期稳定、只安装一次的 `ToolRelay` 模块。Codex 任务开始时只需要认识三条固定工具：

```text
ToolRelay.List
ToolRelay.Describe
ToolRelay.Call
```

以后新增模块仍按现有流程进入 OHS。当前 Codex 任务通过 ToolRelay 实时查询 OHS 的最新
`tools/list`，再由 ToolRelay 把调用转发回本机 OHS MCP 网关。目标工具仍经过网关当前的可见性、
readonly、危险确认、硬排除、超时和审计规则。

```text
当前 Codex 任务
  -> ToolRelay.Call（稳定工具，任务开始时已存在）
  -> http://127.0.0.1:<mcp.port>/mcp
  -> 最新 tools/list / tools/call
  -> 新模块命令
  -> 原有 MCP 策略与命令总线
```

首次安装 ToolRelay 后仍需新建一次 Codex 任务。此后只要 ToolRelay 的三条公开方法不变，新增和
升级其他模块都不再要求重启 Codex 或新建任务。若将来修改 ToolRelay 自身的工具 Schema，仍需
刷新任务。

## 4. 模块命令

### `ToolRelay.List`

实时读取 OHS 当前 `tools/list`。

参数：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `filter` | string | 空 | 按工具名或描述包含匹配 |
| `modulesOnly` | bool | true | 只返回模块工具；false 返回全部当前可见工具 |

返回工具名、描述和参数 Schema。结果必须来自一次新的 MCP 请求，不使用模块内缓存。

`modulesOnly=true` 时通过 `command_list` 判定模块来源。V1.0.1 起清单读取
**优先取 `structuredContent.data`**（OHS V2.4.5 网关的规范字段），
该字段不存在时**回退扫 `content` 文本块**——回退路径永久保留以兼容
V2.4.4 及更早网关，两条路径共用同一段解析逻辑。

### `ToolRelay.Describe`

查看一个当前可见工具的完整描述和 JSON Schema。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 是 | MCP 工具名，例如 `ProjectPulse_Summary` |

目标不存在或在当前策略下不可见时失败，不从 OHS 内部程序集或注册表旁路读取。

### `ToolRelay.Call`

通过最新 MCP 网关调用一个当前可见工具。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `name` | string | 是 | MCP 工具名，不接受本地命令文本 |
| `argumentsJson` | string | 否 | JSON 对象字符串，默认 `{}`，最大 64 KiB |

目标成功时返回原始 MCP `content`、`structuredContent` 和 `isError`。目标返回 `isError=true` 时，
ToolRelay 把首段错误文本传播为外层失败，避免 Codex 或脚本把目标失败误判为成功。

## 5. 实现方式

### 5.1 项目布局

实现阶段采用：

```text
2026-019-Studio工具箱/
├─ b-Code-ToolRelay/
│  ├─ ToolRelay.csproj
│  ├─ ModuleInfoBase.cs
│  ├─ ModuleInfo.cs
│  ├─ RelayCommands.cs
│  ├─ OhsmcpClient.cs
│  └─ README.md
└─ z-ToolRelay/
   ├─ module.manifest.json
   └─ mcp-smoke.txt
```

`b-Code-ToolRelay` 是源码；`z-ToolRelay` 只作为 OHS 工具清单挂点。模块精准暴露
`RelayCommands`，不提供面板，不引入第三方依赖。

### 5.2 网关连接

模块从 `%AppData%\OneHistoryStudio\settings.json` 读取：

- `mcp.port`，缺省 8737；
- `mcp.token`，存在时使用 `Authorization: Bearer ...`；
- `mcp.timeout`，用于限制内部 HTTP 等待时间。

目标地址固定为 `http://127.0.0.1:<port>/mcp`，不得接受调用方传入 URL、主机或令牌。
OHS 当前网关是无状态子集，内部请求直接使用 `tools/list` / `tools/call`，不发送 `initialize`，
避免覆盖网关当前以单字段保存的外部客户端名称。

### 5.3 调用流程

`ToolRelay.Call` 必须按以下顺序执行：

1. 解析并验证 `argumentsJson` 是 JSON 对象；
2. 调用一次 `tools/list`；
3. 确认目标工具在本次列表中；
4. 拒绝 `ToolRelay_*` 自调用；
5. 使用相同工具名和参数调用 `tools/call`；
6. 成功时原样返回目标工具结果；目标 `isError=true` 时传播为外层失败；
7. 不在模块中重试有副作用的 `tools/call`。

步骤 2 使转发器只调用当前策略真正暴露的工具。即使调用方猜到隐藏工具名，也不能越过
`tools/list` 白名单。

## 6. 安全边界

1. **不绕过 MCP 网关**：不得反射 `CommandRegistry`，不得引用 OHS/AppShell 内部程序集，
   不直接执行命令文本。
2. **不绕过 readonly**：readonly 策略下，内部 `tools/list` 只返回 readonly 工具；standard
   工具不可被猜名调用。
3. **不绕过危险确认**：危险工具只有在 `mcp.confirm=host` 且 policy=standard 时进入列表，
   内部 `tools/call` 仍触发宿主人工确认。
4. **不绕过硬排除**：`app.exit`、`mcp.*`、`debug.*` 和 hidden 模块不会进入列表。
5. **禁止递归**：拒绝全部 `ToolRelay_*` 目标，避免自调用造成递归请求。
6. **只连回环地址**：禁止 SSRF，禁止任意端口以外的调用方覆盖；端口只读 OHS 配置。
7. **令牌不出模块**：任何返回值、异常和日志都不得包含 `mcp.token`。
8. **限制输入**：`argumentsJson` 最大 64 KiB，必须是单个 JSON 对象，不接受数组或原始值。
9. **不自动重试写操作**：连接中断或超时后返回不确定状态，由调用方查询审计记录。
10. **双层审计保留**：外层记录 `ToolRelay.Call`，内层记录真实目标工具和参数；不合并、不隐藏。

模块清单计划声明 `mcpExposure=readonly`。这里的含义是：ToolRelay 在 readonly 策略下也可用，
但它只能转发该策略的实时 `tools/list` 中已有的 readonly 工具；切换到 standard 后，允许范围随
网关策略自然扩大。ToolRelay 自身不决定目标权限。

## 7. 不做的事情

- 不终止、重启或控制 Codex 桌面进程；
- 不通过 UI 自动创建 Codex 任务；
- 不自动向新任务发送提示词；
- 不调用 `codex exec` 启动游离于当前任务之外的代理；
- 不自动构建或同步其他模块；
- 不缓存工具列表；
- 不修改 OHS MCP 网关和 AppShell；
- 不承诺让新工具成为当前任务的原生强类型工具，只提供稳定的动态调用入口。

Codex app-server 确实提供 `thread/start`、`thread/resume` 和 `turn/start` 等编排 API，但把它接入
OHS 需要独立的外部编排器、线程身份传递和权限设计，不应塞进普通业务模块。

## 8. 备选方案

### A. `notifications/tools/list_changed`

长期最标准的方案，但需要 OHS 从无状态 HTTP 子集升级为可向客户端推送通知的会话模型，并确认
Codex 当前产品会在任务运行中接收通知、更新工具 Schema 并让模型重新规划。公开手册未给出这一
保证，不能作为 1.0 出口。

### B. 自动重启 Codex并创建新任务

能得到全新的工具快照，但会丢失或复制当前上下文，且模块没有当前任务身份和创建任务授权。
自动代理递归还可能无限创建任务。否决。

### C. 继续使用 `run` 脚本

当前已验证可行，但每次调用都要先写文件，使用命令文本而非 JSON Schema，结果只返回脚本成功数，
Codex 还需读取日志才能获得真实结果。保留为故障回退，不作为正式闭环。

### D. 在 OHS 内置 `mcp.invoke`

比模块回环 HTTP 更干净，可直接复用网关的 exporter 和 policy，但会修改 AppShell/OHS 公共契约，
且必须防止绕过危险确认。先用 ToolRelay 模块验证真实使用价值，再决定是否上抛为框架能力。

## 9. 里程碑

### M0 文档裁决

- 确认“消除重启需求”，而不是“自动重启 Codex”；
- 确认稳定三命令接口；
- 确认模块回环 MCP 的安全模型。

### M1 最小客户端

- 实现配置读取、固定回环地址、Bearer token 和 JSON-RPC；
- 实现 `List` / `Describe`；
- 无 OHS/AppShell 编译引用。

### M2 动态调用

- 实现 `Call`、输入上限、自调用拒绝和原样结果；
- 实现超时不重试；
- XML Help 和 MCP Schema 完整。

### M3 MCP 闭环

- Release 构建 0 警告 0 错误；
- `tool.scan` 报未同步，`tool.sync` 入槽，自动热重载；
- 当前任务通过既有 `run` 入口完成首次验收；
- 新任务原生看到 ToolRelay 三工具。

### M4 无重启飞轮验收

在 Codex 任务已经开始、工具快照已经固定后：

1. 修改或新增一个测试模块工具；
2. Release 构建并 `tool.sync`；
3. 不重启 Codex、不新建任务；
4. `ToolRelay.List` 立即发现新工具；
5. `ToolRelay.Describe` 返回最新 Schema；
6. `ToolRelay.Call` 成功调用；
7. readonly、hidden、危险确认、自调用和坏 JSON 五类负向测试全部通过。

## 10. 验收判据

| 编号 | 判据 |
|---|---|
| TR-01 | 同一 Codex 任务内，新增模块同步后可经 ToolRelay 发现并调用 |
| TR-02 | 目标工具参数 Schema 来自实时 `tools/list`，无模块内缓存 |
| TR-03 | readonly 策略不能经 ToolRelay 调用 standard 工具 |
| TR-04 | hidden、`mcp.*`、`debug.*`、`app.exit` 均不可转发 |
| TR-05 | 危险工具仍经过宿主确认，不因转发器预批准 |
| TR-06 | ToolRelay 自调用立即拒绝，无递归请求 |
| TR-07 | 令牌、URL 和 OHS 内部绝对路径不出现在错误结果中 |
| TR-08 | 目标超时后不自动重试，审计中保留外层和内层记录 |
| TR-09 | OHS 停止、端口错误、token 错误时返回明确且不泄密的诊断 |
| TR-10 | ToolRelay 自身接口不变时，后续模块迭代不再要求新建 Codex 任务 |

## 11. 结论

ToolRelay 不能也不应该替用户重启 Codex。它通过一个预先稳定的动态调用入口，把“客户端工具快照”
与“OHS 运行时模块注册表”解耦，从而让自管理循环在同一任务内继续运行。该方案完全复用 OHS
现有 MCP 策略和审计，是当前架构下改动最小、可验证且不会扩大权限的实现路径。

## 12. V1.0.0 交付记录

> 交付日期：2026-07-26

- Release 构建：0 警告、0 错误；
- `tool.scan`：从“未同步”到同步后的“最新”；
- `tool.sync name=ToolRelay`：成功进入独立模块槽并自动热重载；
- 运行时：精准暴露 3 条 `module/readonly` 指令，OHS 命令总数从 109 增至 112；
- 正向冒烟：`List / Describe / Call` 3/3 成功；
- 旧快照闭环：当前 Codex 任务未取得原生 ToolRelay 工具，仍经既有 `run` 成功转发调用
  `ProjectPulse_Summary`；
- 负向冒烟：自调用、硬排除、危险指令 deny、非对象 JSON、目标工具失败共 5/5 正确拒绝；
- readonly：ProjectPulse 成功、standard 的 ToolKit 被拒，脚本最终恢复 `mcp.policy=standard`；
- 离线诊断：错误端口下明确返回“无法连接 OHS MCP 回环服务”，脚本最终恢复 8737；
- 无重启飞轮 M4：当前 Codex 快照中 `ToolKit_Base64Encode` 为 0；ToolKit 1.0.1 同步并自动
  热重载后，ToolRelay 在同一任务内完成 List、Describe、Call 3/3，Base64 结果与独立计算一致；
- 临时负向脚本已删除，仅保留 `z-ToolRelay/mcp-smoke.txt` 正向回归入口。

未执行 `mcp.confirm=host` 的真实人工弹框点击测试。ToolRelay 不实现确认逻辑，危险目标直接回到
OHS `tools/call`，因此该交互继续由 OHS 既有宿主确认中继验收覆盖。
