# v245-before 快照(V2.4.5 施工前基线)

> 采集时间:2026-07-26 17:00 +08:00
> 用途:M2「`content` 逐条零差异」与 M4 验收的对账基准(31 号文档 §5)

## 采集方式

**线上格式基线**(本版核心判据):直接从**运行中的部署实例**
(`C:\OneHistory\OneHistory-Push\OneHistoryStudio\OneHistoryStudio.exe`,V2.4.4 网关,
`127.0.0.1:8737`)以原始字节抓取 HTTP 响应,不经字符串解码往返。
采集脚本为一次性 PowerShell(WebRequest 直读字节流),23 条免参只读工具逐条
`tools/call` + `tools/list` + 变体调用。

**命令手册基线**:沿用 v244 方式,020 伞形根为工作目录,源码 Release 产物
`--yes --exec "command.manual file=... apply=true"` 生成,退出码 0。

## 文件

| 文件 | 内容 |
|---|---|
| `responses/<tool>.json` | 23 条工具的原始 HTTP 响应字节(UTF-8 JSON) |
| `responses/ToolRelay_List.modulesOnly-false.json` | ToolRelay.List 全量变体 |
| `responses/app_get.mcp-policy.json` | mcp.policy 取值留档 |
| `index.tsv` | 逐条:块数 / isError / 各块字符数 / 有无 structuredContent |
| `mcpstate.tsv` | 113 条命令:命令名 / McpState / 来源 / MCP 工具名 |
| `tools-list.json` | tools/list 原始应答(83 个工具) |
| `command-manual.md` | 源码 Release 产物生成,1359 行(dev 环境模块构成) |
| `summary.txt` | 汇总口径 |

## 基线口径(与 31 号文档 §5 M0 出口逐项对账)

| 判据 | 文档要求 | 实测 | 结果 |
|---|---|---|---|
| 双块工具 | 12 / 23 | 12 / 23 | ✅ |
| 核心 readonly | 32 | 32 | ✅ |
| 命令总数 | 113 | 113 = 核心 102 + 模块 11 | ✅ |
| tools/list 暴露 | 83 | 83 | ✅ |
| ToolRelay 默认调用 | 成功 | 成功(2 块,5528+3287 字符) | ✅ |
| ToolRelay 注册版本 | 1.0.0 留档 | 1.0.0,Sha256 `6D6190FE2AA738A65EEADB85C47D9F5F7BD2DB2C8E482D2EE418111643E51D30` | ✅ |
| 全部响应无 structuredContent | —(V2.4.4 无此字段) | 23/23 无 | ✅ |

核心 McpState 构成:readonly 32 / standard 40 / dangerous 16 / hidden 14。
`mcp.policy` 当前取值见 `responses/app_get.mcp-policy.json`。

## 对账注意(M2 复采时)

1. **确定性工具**(`command_list` `command_domains` `module_list` `proj_metalist`
   `tool_list`* `correction_list` `incident_list` `HelloWorld_Say` 等):
   要求块数与各块字符数**逐条相等**,`command_list` 要求文本**字节级相等**。
2. **非确定性工具**,只比块数与结构,不比内容字节:
   - `history`(每次调用自身会追加记录,长度漂移)
   - `ToolKit_Now`(时间)/ `ToolKit_NewGuid`(随机)
   - `proj_list`(工作树时间戳可能变化)
   - `win_list` / `layout_list` / `app_get`(窗口与设置状态)
   - `tool_list` 在 M1 升级 ToolRelay 后版本号与哈希**应当**变化(1.0.0→1.0.1),
     这是 M1 的预期效果,不是回归
3. 四条免参报错工具(`prompt_get` `prompt_history` `git_rule_list` `ProjectPulse_Summary`
   均为缺参数报错,isError=true 单块)是基线的一部分,M2 后行为应不变。
4. 复采必须用**同一部署实例、同一 mcp.policy**;暴露数不是 83 时先查策略再谈回归。
