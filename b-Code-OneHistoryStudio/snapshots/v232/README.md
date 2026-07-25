# v232 基线快照（V2.3.3 整备前）

> 采集时间：2026-07-24 14:26
> 用途：V2.3.3「对外行为全不变」铁律的比对基准（AQ233-13）

## 采集方式

用 **2.3.2 正式部署版 exe** 采集，工作目录为仓库根：

```
C:\OneHistory\OneHistory-Push\OneHistoryStudio\OneHistoryStudio.exe  (ProductVersion 2.3.2)
工作目录：b-Code-OneHistoryStudio
参数：--yes --exec "debug.sleep seconds=6" --exec "help" --exec "win.list"
      --exec "proj.config" --exec "mcp.schema" --exec "command.list"
      --exec "command.manual file=docs/snapshots/v232/command-manual.md apply=true"
      --exec "debug.sleep seconds=2" --exec "app.exit"
```

> 注意：PowerShell `Start-Process -ArgumentList` 不会自动为含空格的参数加引号，
> 每个 `--exec` 的指令文本必须显式写成 `'"debug.sleep seconds=6"'`，否则参数会被截断
> （首次采集即因此让 `command.manual` 丢掉 `file=` 而失败）。

## 文件

| 文件 | 内容 |
|---|---|
| `console-baseline.log` | 714 行；`help` / `win.list` / `proj.config` / `mcp.schema` / `command.list` 全量回显 |
| `command-manual.md` | 运行时生成，112 条，SHA-256 `C5A8E7A8…B2E5129` |

## 采集环境（比对时必须一致）

| 项 | 值 |
|---|---|
| 模块目录 | `%AppData%\OneHistoryStudio\Modules` |
| 已加载模块 | 5 个 / 10 条指令：DemoModule(4)、HelloWorld(1)、ToolAlpha(1)、ToolBeta(1)、ToolKit(3) |
| MCP 策略 | `standard` |
| 数据目录 | `%AppData%\OneHistoryStudio` |

## 指令总数的正确读法（重要）

**「106」不是不变量。** 实测拆解：

| 口径 | 数值 | 说明 |
|---|---|---|
| **核心指令（框架 + App 注册）** | **102** | 真正的不变量，与模块目录无关 |
| 模块指令 | 随模块目录漂移 | 本次为 10 条 |
| 运行时总数 | 112 | = 102 + 10 |

仓库里的 `docs\命令手册.md` 标称 106，是在模块目录只有 HelloWorld(1) + ToolKit(3) 时生成的
（106 = 102 + 4）。两份手册**剔除 module 来源章节后逐字符完全一致**，102 条核心指令一条不差。

因此 V2.3.3 的验收判据以 **核心 102 条**为准，不以运行时总数为准；
比对时用同一台机器、同一模块目录前后各采一次即可，绝对值无需跨环境相等。
