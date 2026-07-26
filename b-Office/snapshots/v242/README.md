# v242 快照（V2.4.2 文档整理与 MCP 自动启动收口后）

> 采集日期：2026-07-25
> 对账基准：`v233`（中间的 v240/v241 未留快照，不能事后伪造）
> 当前状态：采集完成

## 采集对象

- 可执行文件：仓库 `b-Publish\OneHistoryStudio.exe`
- ProductName：`OneHistoryStudio`
- ProductVersion：`2.4.2`
- FileVersion：`2.4.2.0`
- EXE SHA-256：`8DA44DCA9089705CEEE76851EE4B578D701DEE2ED18F5C5B6532E8C6F8A52CBD`
- AppShell.Shell.dll SHA-256：`01B7C47ED196F7D0031A6ABAD87ACB792F4BBA2C7EFD2E41868071AD4AEABEF4`
- AppShell.Services.dll SHA-256：`4AD450423D762DFF6A39AE51ED0CA9989FC7B483F587DF4786F7DCC38D801C63`
- 工作目录：020 项目根
- MCP 策略：`standard`
- 模块：HelloWorld 1 条 + ToolKit 3 条

## 命令手册

`command-manual.md` 已由 2.4.2 运行时执行
`command.manual file=b-Office/snapshots/v242/command-manual.md apply=true` 生成：

| 口径 | v233 | v242 | 说明 |
|---|---:|---:|---|
| 核心指令 | 102 | 102 | 名称集合完全一致 |
| 模块指令 | 10 | 4 | 采集环境的模块槽不同 |
| 运行时总数 | 112 | 106 | 等于核心指令加模块指令 |

SHA-256：

```text
D7CCEDB5BA4B9BABBEFB6F41D2D477CDA0B1BDF2C277832591B2F5DCAFCBD10E
```

逐条解析非 `module:` 来源的 102 个命令块后，缺失 0、新增 0。唯一正文差异是
`command.manual` 的示例路径；`git log -S` 证明该差异由 V2.4.0 提交
`b26e7b81` 引入，不是 V2.4.2 文档整理造成。V2.4.2 另按合同在生成器文首增加
“自动生成、禁止手改、再生成命令”声明，并更新产品版本行。

仓库现行 `meta\命令手册.md` 与本快照手册 SHA-256 完全一致。

## 控制台基线

正式实例经用户明确授权正常关闭后，使用 2.4.2 发布版沿用 v232/v233 的只读序列：

```text
debug.sleep seconds=6
help
win.list
proj.config
mcp.schema
command.list
command.manual file=b-Office/snapshots/v242/command-manual.md apply=true
debug.sleep seconds=2
app.exit
```

九条指令均在日志中出现且顺序正确；最后一条 `app.exit` 正常保存布局并退出。文件证据：

```text
文件：console-baseline.log
行数：682
字节：60380
SHA-256：C78BC105DC9B5FE307FDD2BF744BB3F44BFFEF7994D2AF0B3186E0932A79B679
```

关键运行时结果：

- OneHistoryStudio 2.4.2 启动完成；
- 2 个模块、4 条模块指令；
- 在没有执行 `mcp.start` 的情况下，日志出现“服务已启动”和“自启动”两条证据；
- `mcp.schema`：注册表 106 条，MCP 工具形态 92 个，硬排除 14 条；
- `command.manual`：106 条，SHA-256 与本目录命令手册一致；
- 采集过程只执行 Help、窗口/配置/Schema/命令目录和手册生成，没有项目 Git 写操作。

采集后已把正式部署从 2.4.1 整目录替换为 2.4.2，并且不传 `mcp.start` 启动。
进程响应正常、8737 自动监听，Codex 原生 `HelloWorld.Say` 与 `proj.list` 调用成功。

修复前的 2.4.2 基线保留为 `console-baseline-pre-autostart.log`：

```text
行数：678
字节：60115
SHA-256：848BE4B0964724A72713CC43BFF0BBABAC96D307D6103494136EDF2DDD004AA8
```

两份日志的命令序列、版本、模块和注册表计数一致；最终基线新增的四行来自 MCP 服务启动、
自动启动摘要和退出时停止服务，直接证明本次小修复的行为边界。

## v233 到 v242 的差异判定

两次快照不在同一版本、同一模块环境，原始日志不能逐字节相等：

| 差异 | v233 | v242 | 判定 |
|---|---:|---:|---|
| 产品版本 | 2.3.3 | 2.4.2 | 版本身份 |
| 模块 | 5 个 / 10 条 | 2 个 / 4 条 | 当前模块槽状态 |
| 注册表总数 | 112 | 106 | 核心均为 102，差额来自模块 |
| MCP 工具形态 | 97 | 92 | 模块数量及 ToolBeta hidden 状态变化 |
| 自动启动日志 | 有 | 有 | V2.4.2 小修复恢复既有行为 |

可稳定对账的核心命令块已按命令名和来源解析：102→102，缺失 0、新增 0；除 V2.4.0
已引入的 `command.manual` 示例路径外，其余核心命令块一致。V2.4.2 的有意变化包括产品版本行、
命令手册文首生成声明，以及宿主启动后自动监听 MCP。

## 自动启动缺口与收口

V2.3.3 基线记录过 `mcp.autostart=true` 自动启动，而最初采集的 2.4.2 基线没有启动行。
源码核对确认 V2.4.0 上抛后只保留了配置显示，没有调用启动入口。本次在同一 V2.4.2 收口：

- 缺省 `mcp.autostart=true`，程序启动即监听；
- 显式设为 `false` 时不监听，仍可用 `mcp.start` 手动恢复；
- 自动启动安排在应用指令与模块全部装载后，首次 `tools/list` 即为完整目录；
- 自动启动失败只告警，不阻止主程序打开；
- PromptGovernanceSmoke 真实监听随机本地端口，覆盖缺省启用、显式关闭和重新启用。
