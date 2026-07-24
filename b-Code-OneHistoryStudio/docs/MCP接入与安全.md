# MCP 接入与安全

> 适用版本：OneHistoryStudio V2.1.7

## Codex 接入

先启动部署区 OneHistoryStudio，确认控制台执行 `mcp.status` 显示监听地址。默认地址为：

```text
http://127.0.0.1:8737/mcp
```

注册到 Codex：

```powershell
codex.cmd mcp add onehistory --url http://127.0.0.1:8737/mcp
codex.cmd mcp get onehistory
```

Codex 客户端新增或修改 MCP 注册后需要重启客户端。OneHistoryStudio 内部模块可热重载，但客户端工具目录是否即时刷新由客户端决定。

## 暴露策略

```text
app.get key=mcp.policy
app.set key=mcp.policy value=readonly
app.set key=mcp.policy value=standard
```

- `readonly`：只暴露查询、Help、命令目录和治理历史等白名单指令；
- `standard`：额外暴露无本地确认要求的动作指令；
- `mcp.*`、`debug.*` 和 `app.exit` 恒不对远程开放；
- 模块可在 `module.manifest.json` 声明 `mcpExposure`（standard/readonly/hidden）自定其档位。

令牌通过 `mcp.token` 配置。未设置令牌只适用于本机回环联调，不应把端口映射到局域网或公网。

## 危险指令与宿主确认中继（mcp.confirm，V2.2）

具有 `ConfirmPrompt` 的危险指令（proj.delete / commitall / pushall / tool.remove / db 危险写等）
的远程处置由 `mcp.confirm` 决定：

```text
app.set key=mcp.confirm value=deny          # 默认：一律拒绝，最保守
app.set key=mcp.confirm value=host          # 远程请求 → 宿主弹框由人裁决
app.set key=mcp.confirmtimeout value=60     # host 档确认超时秒数（10~600，超时按拒绝）
```

- `deny`（默认）：危险指令不进 `tools/list`，`tools/call` 直接拒绝。行为等同 V2.1。
- `host`：危险指令进入 `tools/list`（描述带⚠标注），客户端调用时宿主屏幕弹出确认框，
  标注发起客户端名与倒计时；**人点「允许执行」才执行，点「拒绝」或超时则拒绝**。
- `--yes` 自动确认**永不放行** MCP 来源的危险调用——即使宿主以 `--yes` 启动，远程危险
  请求仍弹框由人裁决（`--yes` 只作用于 UI/手动/脚本）。
- 每次中继均留痕 `mcp_history`（result = 远程确认通过·成功|失败 / 远程拒绝 / 确认超时）。
- 同一时刻只弹一个确认框，多个远程请求按序处理。

## Help 与命令目录

MCP 中的 `help` 与本地控制台读取同一个 `CommandRegistry`：

```text
help
help command=proj.list
command.list mcp=visible
command.show name=git.rule.list
mcp.schema name=proj.list
```

命令集页面提供搜索、域/来源/MCP 过滤、参数表、Help、Schema 和复制示例。模块加载或卸载后页面自动刷新。

## 提示词治理

AI 可以调用 `prompt.propose`、`correction.propose` 和 `incident.record` 提交建议与证据，但不能直接批准、应用或回滚。以下动作只允许本地界面/控制台完成：

```text
mcp.pending
mcp.approve
mcp.reject
mcp.apply
mcp.revert
```

建议先审核文本完整性和命令对象，再批准并应用。乱码提案会在命令边界、存储边界和应用边界被拒绝。

## 联调基线

```text
help command=proj.list
proj.list
module.list
```

若 DemoModule 已安装，再执行其加法指令。失败时先检查 OneHistoryStudio 控制台日志、`mcp.status`、客户端是否重启以及端口是否被其他实例占用。
