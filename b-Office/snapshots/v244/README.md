# v244 快照（V2.4.4 只读白名单收口后）

> 采集时间：2026-07-26
> 对账基准：`../v244-before/`（同日采集，收口前）

## 采集方式

用 V2.4.4 源码的 Release 构建产物采集，工作目录为 020 伞形根：

```
b-Code-Studio\bin\Release\net8.0-windows\OneHistoryStudio.exe
--yes --exec "debug.sleep seconds=6"
     --exec "command.manual file=b-Office/snapshots/v244/command-manual.md apply=true"
     --exec "debug.sleep seconds=2" --exec "app.exit"
```

> PowerShell `Start-Process -ArgumentList` 不会自动为含空格的参数加引号，
> 每个 `--exec` 的指令文本必须显式写成 `'"debug.sleep seconds=6"'`（沿用 v232 的踩坑记录）。

## 文件

| 文件 | 内容 |
|---|---|
| `command-manual.md` | 运行时生成，1980 行，112 条命令的完整形态 |
| `mcpstate.tsv` | 从手册提取的 `命令 / MCP档位 / 来源` 三列，用于逐条对账 |

`mcpstate.tsv` 是本版的**核心判据载体**——只比总数不足以发现「两条命令互换档位」这类回归。

## 对账结果

### 1. McpState 逐条比对：**112 条完全一致**

```
before: 112 条   after: 112 条
Compare-Object 差异: 0
```

### 2. `command-manual.md` 全文：**1980 行逐行完全一致**

本版未升产品版本号（仍为 2.4.3 产物），因此**连版本号行都相同**——
这是历次快照对账中唯一一次「零差异」，因为本版纯粹删除冗余代码，
对运行时行为没有任何投影。

### 3. 只读档位构成

| 口径 | 条数 | 说明 |
|---|---|---|
| **核心 readonly** | **32** | 稳定不变量，全部来自 `CommandDescriptor.Readonly = true` |
| 模块 readonly | 1 | `ToolAlpha.Ver`，来自模块清单 `mcpExposure=readonly` |
| 合计 readonly | 33 | = 32 + 模块，模块部分随模块目录漂移 |

**核心 32 条**（框架 18 + Studio 14）：

```
app.get  help  history
command.domains  command.list  command.show
correction.list  incident.list
db.list  db.query  db.schema  db.tables
layout.list  module.list  win.list
prompt.diff  prompt.get  prompt.history
proj.config  proj.history  proj.history.diff  proj.history.show
proj.list  proj.metalist  proj.scan  proj.tree
git.rule.gaps  git.rule.list  git.rule.scan  git.rule.suggest
tool.list  tool.scan
```

> 模块那 1 条恰好证明了 V2.4.4 保留 `ModuleExposure` 通道的必要性：
> 模块的暴露档是**模块清单的事实**，与命令描述符自描述是两条独立通路，不可合并。

## 采集环境

| 项 | 值 |
|---|---|
| 已加载模块 | 5 个 / 10 条指令（DemoModule 4、HelloWorld 1、ToolAlpha 1、ToolBeta 1、ToolKit 3） |
| MCP 策略 | `standard` |
| 运行时命令总数 | 112 = 核心 102 + 模块 10 |

核心 **102 条**是环境无关的不变量；运行时总数随模块目录漂移，只作记录不作判据
（口径见 `versions/25-V2.3.3` §1.4）。
