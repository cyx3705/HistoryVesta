# v243-before 基线快照

> 采集日期：2026-07-25
> 采集版本：OneHistoryStudio 2.4.2
> 用途：V2.4.3 代码卫生等价改写前基线

## 采集方式

使用仓库 `b-Publish\OneHistoryStudio.exe`，从 020 项目根依次执行：

```text
debug.sleep seconds=6
help
win.list
proj.config
mcp.schema
command.list
command.manual file=b-Office/snapshots/v243-before/command-manual.md apply=true
debug.sleep seconds=2
app.exit
```

采集时 `mcp.policy=standard`，模块槽包含 HelloWorld 1 条和 ToolKit 3 条指令。
程序自动启动 MCP；全程没有执行项目提交、推送或历史改写。

## 文件证据

| 文件 | 字节 | 行数 | SHA-256 |
|---|---:|---:|---|
| `command-manual.md` | 47,923 | - | `D7CCEDB5BA4B9BABBEFB6F41D2D477CDA0B1BDF2C277832591B2F5DCAFCBD10E` |
| `console-baseline.log` | 60,394 | 682 | `A8095ED364886D5F1988612555FF1B2A759CE86443E14D2CD9F435BDCC27EEF5` |

运行时总计 106 条命令，其中核心 102 条、模块 4 条、readonly 32 条。

