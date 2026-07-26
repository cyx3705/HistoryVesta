# v245 structuredContent 客户端行为实验(31 号文档 §3.3 / M3)

> 实验日期:2026-07-26
> 问题:客户端把 `structuredContent` 交给模型吗?`content` 文本块还进上下文吗?

## 方法说明:实弹差分取代探针服务

31 号文档原设计是独立探针 MCP 服务 + 双组 canary。实际执行中发现
**V2.4.5 网关部署后,生产响应本身就构成天然差分实验**:

- `content[0]` = 人类可读 Message(如 ToolKit.Now 的中文时间描述);
- `content[1]` = Data 的 JSON 文本;
- `structuredContent` = `{"data": <同一载荷>}`。

Message 文本与 data 载荷**内容不同**(前者是渲染文本,后者是结构化 JSON),
等价于「canary 只在一处」的差分设计;`help` 等无 Data 工具(只有 content)
则天然充当**正向对照组**(证明 content 通路本身可达模型)。

## Claude Code 实测(2026-07-26)

| 环境 | 值 |
|---|---|
| 客户端 | Claude Code(Windows 桌面 app 会话,模型 claude-opus-5) |
| 接入方式 | `.mcp.json` HTTP server → `http://127.0.0.1:8737/mcp` |
| 网关 | OneHistoryStudio 2.4.5(部署实例) |

模型侧(本会话的模型即观察者)收到的 tool_result 原文:

| 调用 | V2.4.4 网关(部署前) | V2.4.5 网关(部署后) |
|---|---|---|
| `HelloWorld_Say` | `Hello, World!"Hello, World!"`(两个 content 文本块拼接) | `{"data":"Hello, World!"}` |
| `ToolKit_Now` | 两块拼接(Message + JSON) | `{"data":{"local":"2026-07-26T17:39:51.8843571+08:00","unix":1785058791}}` |
| `help` 等无 Data 工具 | content 文本 | content 文本(不变,正向对照 ✅) |

### 判读

1. **`structuredContent` 进入模型上下文**——模型收到的就是它的 JSON 序列化;
2. **有 `structuredContent` 时,`content` 文本块不再进入上下文**——
   `ToolKit_Now` 的 Message 文本(content[0])在模型侧完全不可见,
   证明 Claude Code 的策略是**替换**而非**追加**;
3. 正向对照成立:无 Data 工具的 content 照常到达,通路无恙。

### 对 OHS 的实际影响(Claude Code 侧)

| 项 | V2.4.4 | V2.4.5 |
|---|---|---|
| 双份文本问题 | Message + JSON 两块全进上下文(command_list 单次 ~54KB) | **只进一份** `{"data":...}`(~46KB),Message 不进 |
| 人类可读 Message | 模型可见 | **模型不可见**(data 是其字段超集,信息无损失) |

> **注意**:此结论绑定 2026-07-26 的 Claude Code 版本,客户端行为随版本变化,
> 引用前应复测。31 号文档设想的「移除 content[1] 省上下文」在 Claude Code 侧
> **已无必要**——客户端自己完成了择一。

## Codex 实测(2026-07-26,用户在新建 Codex 任务内执行)

| 环境 | 值 |
|---|---|
| 客户端 | codex-cli **0.145.0** |
| 网关 | OneHistoryStudio 2.4.5(部署实例,8737) |
| 调用 | `ToolKit_Now` |

Codex 报告的所见:

- 中文时间描述(content[0] 的 Message):**没有**
- `unix` 字段:**有**,值 `1785059322`
- `local` 字段:**有**,`2026-07-26T17:48:42.179435+08:00`

### 判读

Codex 只看到结构化 JSON,Message 文本不可见 ——
**与 Claude Code 相同:`structuredContent` 替换 `content` 进入模型上下文。**

## 总结论

两个现役客户端(Claude Code 2026-07-26 版、codex-cli 0.145.0)行为一致:
响应含 `structuredContent` 时,模型只收到它,`content` 文本块不进上下文。

| 推论 | 说明 |
|---|---|
| 双份文本问题已消失 | 两个客户端侧均不再出现 Message+JSON 双份 |
| 「移除 `content[1]`」议题(31 号文档 §4)**收益归零** | 客户端已自行择一;移除只会破坏旧网关兼容与规范 SHOULD,无任何上下文收益。建议永久搁置,除非未来客户端行为改变 |
| Message 文本对模型不可见 | data 是 Message 的字段超集,信息无损失;人类仍经 GUI/日志看 Message |

> 结论绑定当日客户端版本;客户端升级后引用前应复测(方法:免参调 `ToolKit_Now`,
> 问模型是否见到中文描述与 unix 字段,双答案即判读)。

—— 实验完成 ——
