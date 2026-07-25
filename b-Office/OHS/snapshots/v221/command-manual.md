# OneHistoryStudio 命令手册

> 版本：2.2.1
> 来源：运行时 `CommandRegistry` 与 MCP 投影自动生成；请勿手工维护指令条目。
> 当前 MCP 策略：`standard`
> 指令总数：106

<!-- command-count: 106 -->

## DemoModule (4)

### `DemoModule.Add`

两个整数相加,返回和

- 来源：`module:DemoModule`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `DemoModule_Add`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `a` | `int` | 是 | - | - | 第一个加数 |
| `b` | `int` | 是 | - | - | 第二个加数 |

```text
DemoModule.Add a=1 b=1
```

### `DemoModule.Now`

返回当前时间和机器名(演示静态方法与对象返回值的 JSON 回显)

- 来源：`module:DemoModule`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `DemoModule_Now`

参数：无。

```text
DemoModule.Now
```

### `DemoModule.Reverse`

把字符串反转

- 来源：`module:DemoModule`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `DemoModule_Reverse`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `text` | `string` | 是 | - | - | 要反转的字符串 |

```text
DemoModule.Reverse text=文本
```

### `DemoModule.SayHello`

向指定的人问好(演示异步方法与默认参数)

- 来源：`module:DemoModule`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `DemoModule_SayHello`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | World | - | 要问候的名字,缺省为 World |

```text
DemoModule.SayHello name=文本
```

## HelloWorld (1)

### `HelloWorld.Say`

返回固定问候文本，不产生任何外部副作用

- 来源：`module:HelloWorld`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `HelloWorld_Say`

参数：无。

```text
HelloWorld.Say
```

## ToolAlpha (1)

### `ToolAlpha.Ver`

返回本模块槽内 SharedDep 依赖的版本标识

- 来源：`module:ToolAlpha`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `ToolAlpha_Ver`

参数：无。

```text
ToolAlpha.Ver
```

## ToolBeta (1)

### `ToolBeta.Ver`

返回本模块槽内 SharedDep 依赖的版本标识

- 来源：`module:ToolBeta`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：模块清单声明 mcpExposure=hidden,不对 MCP 暴露(Q211-2)

参数：无。

```text
ToolBeta.Ver
```

## ToolKit (3)

### `ToolKit.NewGuid`

生成一个新 GUID

- 来源：`module:ToolKit`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `ToolKit_NewGuid`

参数：无。

```text
ToolKit.NewGuid
```

### `ToolKit.Now`

当前本地时间与 Unix 秒

- 来源：`module:ToolKit`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `ToolKit_Now`

参数：无。

```text
ToolKit.Now
```

### `ToolKit.Sha256`

计算文本的 SHA-256(十六进制大写)

- 来源：`module:ToolKit`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `ToolKit_Sha256`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `text` | `string` | 是 | - | - | 要哈希的文本 |

```text
ToolKit.Sha256 text=文本
```

## app (5)

### `app.about`

显示关于对话框

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `app_about`

参数：无。

### `app.exit`

退出程序

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：远程客户端不得退出宿主

参数：无。

### `app.get`

读应用配置项;不带参数列出全部

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `app_get`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `key` | `string` | 否 | - | - | 配置键;省略列出全部 |

```text
app.get key=console.history
```

### `app.opendata`

在系统资源管理器中打开应用数据目录

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `app_opendata`

参数：无。

### `app.set`

写应用配置项

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `app_set`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `key` | `string` | 是 | - | - | 配置键 |
| `value` | `string` | 是 | - | - | 配置值 |

```text
app.set key=console.history value=1000
```

## core (4)

### `cls`

清空控制台显示(不清日志文件)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `cls`

参数：无。

### `help`

列出全部指令 / 显示某指令详情与示例

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `help`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `command` | `string` | 否 | - | - | 指令名;省略时列出全部指令 |

```text
help win.dock
```

### `history`

查看指令历史

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `history`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `count` | `int` | 否 | 20 | - | 显示条数 |

```text
history count=10
```

### `run`

逐行执行指令脚本文件(# 注释与空行忽略)

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `run`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `file` | `string` | 是 | - | - | 脚本路径;相对路径基于工作区目录 |
| `continue` | `bool` | 否 | false | - | 出错时跳过继续(默认中断并报告行号) |

```text
run file=每日巡检.txt continue=true
```

## command (4)

### `command.domains`

列出全部指令域及注册数量

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `command_domains`

参数：无。

```text
command.domains
```

### `command.list`

结构化列出全部注册指令及其来源、风险和 MCP 投影

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `command_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `domain` | `string` | 否 | - | - | 可选指令域，如 proj / attr / command |
| `mcp` | `string` | 否 | all | all / visible / hidden | 按当前策略过滤 MCP 可见性 |
| `filter` | `string` | 否 | - | - | 按名称、说明或来源搜索 |

```text
command.list domain=proj mcp=visible filter=scan
```

### `command.manual`

从运行时注册表和 MCP 投影预览或生成 Markdown 命令手册

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `command_manual`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `file` | `string` | 是 | - | - | 相对当前工作目录的 Markdown 输出路径 |
| `apply` | `bool` | 否 | false | - | false 仅预览；true 经本地确认后原子写入 |

```text
command.manual file=b-Code-OneHistoryStudio/docs/命令手册.md apply=false
```

### `command.show`

查看单条指令的 Help 参数、来源、风险和 MCP 映射

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `command_show`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 完整指令名 |

```text
command.show name=mcp.apply
```

## correction (2)

### `correction.list`

列出 MCP 工具描述勘误记录

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `correction_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 可选指令名 |
| `limit` | `int` | 否 | 50 | - | 最多返回条数 |

```text
correction.list name=proj.list limit=20
```

### `correction.propose`

提交工具描述勘误，不直接修改生效描述

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `correction_propose`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |
| `claim` | `string` | 是 | - | - | 需要纠正的原说法 |
| `correction` | `string` | 是 | - | - | 正确解释 |
| `evidence` | `string` | 否 | - | - | 证据或复现记录 |
| `proposal` | `string` | 否 | - | - | 可选关联的提示词提案 ID |

```text
correction.propose name=proj.list claim="扫描全部磁盘" correction="只列已登记工作树"
```

## db (9)

### `db.delete`

删除行(无 where 将清空整表,需二次确认)

- 来源：`framework`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `db_delete`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `where` | `string` | 否 | - | - | SQL 条件;省略 = 清空整表(危险) |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.delete table=users where="id=1032"
```

### `db.export`

导出查询结果为 CSV 文件

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `db_export`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `file` | `string` | 是 | - | - | 目标文件;相对路径落到工作区目录 |
| `where` | `string` | 否 | - | - | SQL 条件片段 |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.export table=users file=用户.csv where="vip=1"
```

### `db.insert`

插入一行

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `db_insert`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `set` | `string` | 是 | - | - | 列=值 列表,逗号分隔,字符串用单引号 |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.insert table=users set="name='张三', age=30"
```

### `db.list`

列出全部命名数据库连接

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `db_list`

参数：无。

### `db.query`

查询表数据(表窗口同步显示结果)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`readonly`，当前策略可见，工具名 `db_query`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `where` | `string` | 否 | - | - | SQL 条件片段(原样拼接) |
| `order` | `string` | 否 | - | - | SQL 排序片段,如 "age desc" |
| `limit` | `int` | 否 | 500 | - | 每页行数 |
| `page` | `int` | 否 | 1 | - | 页码(1 起) |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.query table=users where="age>30 and city='北京'" limit=100
```

### `db.schema`

查看表结构(字段名 / 类型 / 主键 / 非空)

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `db_schema`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.schema table=users
```

### `db.sql`

SQL 直通(高级用户;执行前有风险确认)

- 来源：`framework`
- 安全：本地二次确认
- UI 线程：是
- MCP：`dangerous`，当前策略隐藏，工具名 `db_sql`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `sql` | `string` | 是 | - | - | 完整 SQL 语句 |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.sql "SELECT city, COUNT(*) FROM users GROUP BY city"
```

### `db.tables`

列出连接内全部表

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `db_tables`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `conn` | `string` | 否 | - | - | 连接名,缺省 main |

```text
db.tables
```

### `db.update`

更新行(无 where 将更新整表,需二次确认)

- 来源：`framework`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `db_update`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `table` | `string` | 是 | - | - | 表名(db.tables 可查) |
| `set` | `string` | 是 | - | - | 列=值 列表,逗号分隔 |
| `where` | `string` | 否 | - | - | SQL 条件;省略 = 整表更新(危险) |
| `conn` | `string` | 否 | - | - | 连接名,缺省 main(db.list 可查) |

```text
db.update table=users set="vip=1" where="id=1032"
```

## debug (2)

### `debug.logflood`

日志承压测试:按指定速率注入日志

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：调试与承压指令不对远程暴露

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `rate` | `int` | 否 | 1000 | - | 每秒注入条数 |
| `seconds` | `int` | 否 | 30 | - | 持续秒数 |

```text
debug.logflood rate=1000 seconds=30
```

### `debug.sleep`

等待指定秒数(自动化脚本用;异步等待,不阻塞 UI)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：调试与承压指令不对远程暴露

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `seconds` | `int` | 否 | 3 | - | 等待秒数(1~120) |

```text
debug.sleep seconds=5
```

## git (7)

### `git.rule.gaps`

只列未纳管的格式与目录候选,按影响文件数降序(缺口清单)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `git_rule_gaps`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 项目名;省略则查全库 |

```text
git.rule.gaps
```

### `git.rule.list`

列出项目根文件格式的纳入 Git、LFS、LF 规则和实际索引状态

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `git_rule_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 已登记 Project 名/分支名 |

```text
git.rule.list name=0000-000-Template
```

### `git.rule.remove`

预览或确认后移除托管文件格式规则；不删除本地文件

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `git_rule_remove`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 已登记 Project 名/分支名 |
| `pattern` | `string` | 是 | - | - | 要移除的简单文件格式 |
| `apply` | `bool` | 否 | false | - | false 仅预览；true 经本地确认后移除 |

```text
git.rule.remove name=demo pattern=*.xlsx apply=false
```

### `git.rule.scan`

扫描项目库全部文件格式,输出台账与覆盖率(省略 name 扫全库)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `git_rule_scan`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 项目名;省略则扫描全库 |
| `deep` | `bool` | 否 | false | - | true 时追加 git lfs 指针核验(较慢) |
| `refresh` | `bool` | 否 | false | - | true 时忽略缓存全量重扫 |

```text
git.rule.scan depth=normal
```

### `git.rule.set`

预览或确认后设置文件格式的纳入 Git、LFS、LF 状态并同步索引

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `git_rule_set`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 已登记 Project 名/分支名 |
| `pattern` | `string` | 是 | - | - | 简单文件格式，如 *.xlsx |
| `track` | `bool` | 否 | true | - | 是否纳入 Git |
| `lfs` | `bool` | 否 | false | - | 是否使用 LFS 指针 |
| `lf` | `bool` | 否 | false | - | 是否作为文本并统一 LF |
| `apply` | `bool` | 否 | false | - | false 仅预览；true 经本地确认后写入并同步索引 |

```text
git.rule.set name=demo pattern=*.xlsx track=true lfs=true lf=false apply=false
```

### `git.rule.suggest`

对未决格式给出处置建议(派生件忽略/文本 LF/大二进制 LFS;未知格式留白)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `git_rule_suggest`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 项目名;省略则针对全库 |

```text
git.rule.suggest
```

### `git.rule.sync`

把模板项目的规则基线刷入各项目的 baseline 块(不动项目自身 managed 块与手写内容)

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `git_rule_sync`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 目标项目;省略则同步全部项目(模板自身除外) |
| `apply` | `bool` | 否 | false | - | false 仅预览;true 经确认后写入 |

```text
git.rule.sync apply=false
```

## incident (2)

### `incident.list`

列出 MCP 工具调用或描述事故记录

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `incident_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 可选指令名 |
| `limit` | `int` | 否 | 50 | - | 最多返回条数 |

```text
incident.list name=proj.list limit=20
```

### `incident.record`

记录工具调用或描述事故；保留预期、实际和证据

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `incident_record`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |
| `symptom` | `string` | 是 | - | - | 问题现象 |
| `expected` | `string` | 是 | - | - | 预期行为 |
| `actual` | `string` | 是 | - | - | 实际行为 |
| `evidence` | `string` | 否 | - | - | 日志、复现步骤或引用 |
| `correction` | `string` | 否 | - | - | 可选关联的勘误 ID |

```text
incident.record name=proj.list symptom=误解扫描范围 expected=只列登记项目 actual=尝试扫描磁盘
```

## layout (4)

### `layout.list`

列出全部命名布局方案

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`readonly`，当前策略可见，工具名 `layout_list`

参数：无。

### `layout.load`

加载命名布局方案

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `layout_load`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 方案名(layout.list 可查) |

```text
layout.load name=调试布局
```

### `layout.reset`

重置为默认布局

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `layout_reset`

参数：无。

### `layout.save`

把当前布局保存为命名方案

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `layout_save`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 方案名 |

```text
layout.save name=调试布局
```

## log (1)

### `log.level`

调整控制台日志显示级别(文件始终全量)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `log_level`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `level` | `string` | 否 | - | trace / debug / info / warn / error / fatal | 显示级别;省略时查看当前值 |

```text
log.level warn
```

## mcp (11)

### `mcp.apply`

本地应用已批准提案；基线变化时拒绝覆盖

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `id` | `string` | 是 | - | - | 提案 ID |
| `reviewer` | `string` | 否 | - | - | 应用人；省略使用当前 Windows 用户 |

```text
mcp.apply id=proposal_xxx reviewer=Administrator
```

### `mcp.approve`

本地批准提示词提案；批准后仍需 apply 才生效

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `id` | `string` | 是 | - | - | 提案 ID |
| `reviewer` | `string` | 否 | - | - | 审核人；省略使用当前 Windows 用户 |

```text
mcp.approve id=proposal_xxx reviewer=Administrator
```

### `mcp.desc`

本地查看/直接修订 MCP 工具描述；远程 AI 请使用 prompt.propose

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |
| `text` | `string` | 否 | - | - | 新描述；省略只查看 |
| `reason` | `string` | 否 | 本地直接修订 | - | 修订理由 |
| `reviewer` | `string` | 否 | - | - | 执行人；省略使用当前 Windows 用户 |
| `reset` | `bool` | 否 | false | - | 恢复指令自带描述 |

```text
mcp.desc name=proj.list text="列出全部已登记项目" reason=人工修订
```

### `mcp.parse`

调试:模拟 tools/call 反向解析——JSON arguments 组装为指令文本,exec=true 随即经总线执行

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `command` | `string` | 是 | - | - | 目标指令名 |
| `args` | `string` | 否 | - | - | JSON 对象文本(工具调用的 arguments) |
| `argsfile` | `string` | 否 | - | - | 从文件读 JSON(替代 args,规避命令行转义;自动化验收用) |
| `exec` | `bool` | 否 | false | - | true 时组装后立即经总线执行(来源 MCP:parse) |

```text
mcp.parse command=proj.list args="{\"filter\":\"2026\"}" exec=true
```

### `mcp.pending`

本地列出待审核或已批准未应用的提示词提案

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 可选指令名 |
| `limit` | `int` | 否 | 100 | - | 最多返回条数 |

```text
mcp.pending name=proj.list
```

### `mcp.reject`

本地拒绝提示词提案并保留理由

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `id` | `string` | 是 | - | - | 提案 ID |
| `reason` | `string` | 是 | - | - | 拒绝理由 |
| `reviewer` | `string` | 否 | - | - | 审核人；省略使用当前 Windows 用户 |

```text
mcp.reject id=proposal_xxx reason=边界描述不准确
```

### `mcp.revert`

本地把指定历史修订内容生成为新的当前修订

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `revision` | `string` | 是 | - | - | 目标历史修订 ID |
| `reason` | `string` | 是 | - | - | 回滚理由 |
| `reviewer` | `string` | 否 | - | - | 执行人；省略使用当前 Windows 用户 |

```text
mcp.revert revision=rev_xxx reason=回退错误描述
```

### `mcp.schema`

查看指令的 MCP 工具形态(不带参列全部;带 name 输出单条完整 JSON Schema)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 指令名或工具名(如 proj.create / proj_create);省略列出全部 |

```text
mcp.schema name=proj.create
```

### `mcp.start`

启动 MCP 服务(仅 127.0.0.1;策略/令牌经 app.set mcp.policy / mcp.token 配置)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `port` | `int` | 否 | - | - | 监听端口(缺省读 mcp.port 配置,默认 8737;显式指定时持久化) |

```text
mcp.start port=8737
```

### `mcp.status`

查看 MCP 服务状态(运行/端口/策略/暴露工具数/累计调用/最近一次调用)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

参数：无。

```text
mcp.status
```

### `mcp.stop`

停止 MCP 服务并释放端口

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`hidden`，当前策略隐藏
- MCP 排除原因：防止远程递归管理或关闭 MCP 服务

参数：无。

```text
mcp.stop
```

## module (4)

### `module.dir`

查看/切换模块目录(切换后立即重载并持久化)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `module_dir`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 否 | - | - | 新模块目录(绝对路径);省略则只显示当前目录 |

```text
module.dir path=D:\MyModules
```

### `module.list`

列出已加载模块(名称/版本/描述/指令数)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `module_list`

参数：无。

```text
module.list
```

### `module.open`

在系统资源管理器中打开模块目录(UI-12 面板按钮落点)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `module_open`

参数：无。

```text
module.open
```

### `module.reload`

手动整体重载全部模块(文件变化会自动热重载,通常无需手动)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `module_reload`

参数：无。

```text
module.reload
```

## panel (4)

### `panel.list`

列出全部控制面板及其窗口状态

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `panel_list`

参数：无。

### `panel.reload`

重读面板 JSON 配置并原地重建(新增面板需重启)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `panel_reload`

参数：无。

### `panel.set`

程序向面板控件回写值(P-07)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `panel_set`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `panel` | `string` | 是 | - | - | 面板 id |
| `control` | `string` | 是 | - | - | 控件 id |
| `value` | `string` | 是 | - | - | 新值 |

```text
panel.set panel=motor control=speed value=800
```

### `panel.show`

显示控制面板(等价 win.show)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `panel_show`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `id` | `string` | 是 | - | - | 面板 id(panel.list 可查) |

```text
panel.show id=motor
```

## proj (15)

### `proj.commit`

提交单个项目到本地裸仓库(大小检查→LFS 处理→add→commit)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_commit`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 分支名(= 工作树文件夹名) |
| `msg` | `string` | 是 | - | - | 提交描述(Commit Message) |

```text
proj.commit name=2026-018-MyAPI msg="更新说明"
```

### `proj.commitall`

一键提交全部工作树到本地裸仓库(逐项大小检查,汇总四类结果)

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `proj_commitall`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `msg` | `string` | 是 | - | - | 统一提交描述 |

```text
proj.commitall msg="每日推送"
```

### `proj.config`

显示 proj.* 当前生效配置(经 app.set 修改)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `proj_config`

参数：无。

```text
proj.config
```

### `proj.create`

创建新项目:新建分支 + 同名工作树(分支名 = 文件夹名)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_create`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 项目名称(合法文件夹字符) |
| `base` | `string` | 否 | - | - | 基础分支(缺省取 proj.basebranch 配置) |

```text
proj.create name=2026-020-新项目
```

### `proj.delete`

删除项目:移除工作树 + 强制删除分支(不可撤销;受保护分支拒绝)

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `proj_delete`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 要删除的分支名(= 工作树文件夹名) |

```text
proj.delete name=9999-901-测试
```

### `proj.list`

列出全部项目工作树(编号/分支/路径/状态)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `proj_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `filter` | `string` | 否 | - | - | 分支名关键字过滤(包含匹配,忽略大小写) |

```text
proj.list filter=2026
```

### `proj.metalist`

列出全部项目根下以 z/Z 开头的一级元文件夹

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `proj_metalist`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `filter` | `string` | 否 | - | - | 项目名/元文件夹名/路径关键字过滤(包含匹配,忽略大小写) |

```text
proj.metalist filter=AD
```

### `proj.metaopen`

在系统资源管理器中打开指定元文件夹(path= 或 name=+meta=)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_metaopen`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 否 | - | - | 元文件夹完整路径 |
| `name` | `string` | 否 | - | - | 所属项目分支名(= 工作树文件夹名) |
| `meta` | `string` | 否 | - | - | 元文件夹名(须以 z/Z 开头) |

```text
proj.metaopen name=2026-016-AD学习 meta=z-AD库文件汇总
```

### `proj.note`

写入/更新分支的项目描述(继承树与 proj.tree 优先显示此描述)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_note`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 分支名 |
| `text` | `string` | 是 | - | - | 项目描述文本 |

```text
proj.note name=2026-018-MyAPI text="基础设施整合项目"
```

### `proj.open`

在系统资源管理器中打开项目工作树(不带 name 打开工作树根目录)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_open`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 分支名(= 工作树文件夹名);省略打开根目录 |

```text
proj.open name=2026-018-MyAPI
```

### `proj.push`

推送单个分支到 GitHub(git push origin 分支名)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `proj_push`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 分支名(= 工作树文件夹名) |

```text
proj.push name=2026-018-MyAPI
```

### `proj.pushall`

推送全部分支到 GitHub(git push --all origin)

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `proj_pushall`

参数：无。

```text
proj.pushall
```

### `proj.repair`

worktree 断链批量修复:删除全部工作树目录→prune→按分支清单重建

- 来源：`app`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `proj_repair`

参数：无。

```text
proj.repair
```

### `proj.scan`

扫描项目大文件并输出分级报告(不提交)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `proj_scan`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 分支名(= 工作树文件夹名) |

```text
proj.scan name=2026-018-MyAPI
```

### `proj.tree`

输出分支继承树(默认读文件缓存秒开;refresh=true 重新扫描并更新缓存)

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `proj_tree`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `refresh` | `bool` | 否 | false | - | true 时忽略缓存重新扫描裸仓库 |
| `cached` | `bool` | 否 | false | - | true 时仅读缓存,无缓存不触发扫描(视图自动加载用) |

```text
proj.tree refresh=true
```

## prompt (4)

### `prompt.diff`

查看提示词提案的原文、新文和文本差异

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `prompt_diff`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `id` | `string` | 是 | - | - | 提案 ID |

```text
prompt.diff id=proposal_xxx
```

### `prompt.get`

查看 MCP 工具的默认描述、生效描述、当前修订和待审核提案数

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `prompt_get`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |

```text
prompt.get name=proj.list
```

### `prompt.history`

查看某个 MCP 工具的描述修订历史

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `prompt_history`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |
| `limit` | `int` | 否 | 20 | - | 最多返回条数 |

```text
prompt.history name=proj.list limit=20
```

### `prompt.propose`

提交 MCP 工具描述修改提案；不会直接改变生效描述

- 来源：`app`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `prompt_propose`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 指令名或工具名 |
| `text` | `string` | 是 | - | - | 建议的新描述 |
| `reason` | `string` | 是 | - | - | 修改理由 |
| `evidence` | `string` | 否 | - | - | 证据、错误现场或引用 |

```text
prompt.propose name=proj.list text="列出全部已登记项目" reason=澄清扫描边界
```

## res (7)

### `res.delete`

删除到回收站(需二次确认,不做永久删除)

- 来源：`framework`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `res_delete`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 是 | - | - | 相对工作区根目录的路径 |

```text
res.delete path=旧配方
```

### `res.list`

列出目录内容

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_list`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 否 | - | - | 目录(相对根);省略为根目录 |

```text
res.list path=配方
```

### `res.mkdir`

新建文件夹(限工作区内)

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_mkdir`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 是 | - | - | 相对工作区根目录的路径 |

```text
res.mkdir path=新配方
```

### `res.open`

用系统默认程序打开文件

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_open`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 是 | - | - | 相对工作区根目录的路径 |

```text
res.open path=说明.txt
```

### `res.rename`

重命名文件或文件夹

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_rename`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 是 | - | - | 相对工作区根目录的路径 |
| `to` | `string` | 是 | - | - | 新名字(不含路径) |

```text
res.rename path="配方/旧.json" to="新.json"
```

### `res.reveal`

在系统资源管理器中显示

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_reveal`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 是 | - | - | 相对工作区根目录的路径 |

```text
res.reveal path=配方
```

### `res.root`

查看 / 切换工作区根目录

- 来源：`framework`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `res_root`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `path` | `string` | 否 | - | - | 新根目录(绝对路径);省略时查看当前值 |

```text
res.root path=D:\我的工程
```

## tool (4)

### `tool.list`

列出已同步工具的溯源(来源分支/版本/哈希/时间/槽状态)

- 来源：`tool`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `tool_list`

参数：无。

```text
tool.list
```

### `tool.remove`

移除已同步工具:删除模块槽 + 注销溯源(指令随热重载注销)

- 来源：`tool`
- 安全：本地二次确认
- UI 线程：否
- MCP：`dangerous`，当前策略隐藏，工具名 `tool_remove`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 工具名(tool.list 所列) |

```text
tool.remove name=ToolDemo
```

### `tool.scan`

扫描项目库全部工具清单(z 级元文件夹的 module.manifest.json),标注部署状态

- 来源：`tool`
- 安全：普通
- UI 线程：否
- MCP：`readonly`，当前策略可见，工具名 `tool_scan`

参数：无。

```text
tool.scan
```

### `tool.sync`

把清单声明的工具产物同步入模块槽(复制+SHA-256 溯源,热重载自动接手)

- 来源：`tool`
- 安全：普通
- UI 线程：否
- MCP：`standard`，当前策略可见，工具名 `tool_sync`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 否 | - | - | 工具名(tool.scan 所列);与 all 二选一 |
| `all` | `bool` | 否 | false | - | true 时同步全部「未同步/已过期」项 |

```text
tool.sync name=ToolDemo
```

## win (7)

### `win.dock`

停靠窗口到指定方位(pos=tab 时并入 target 所在标签组)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_dock`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |
| `pos` | `string` | 是 | - | left / right / top / bottom / tab | 停靠方位 |
| `target` | `string` | 否 | - | - | pos=tab 时,并入哪个窗口所在的标签组 |
| `ratio` | `double` | 否 | - | - | 占主窗体比例 0~1 |

```text
win.dock name=console pos=bottom ratio=0.3
```

### `win.float`

把窗口浮动为独立顶层窗口

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_float`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |

```text
win.float name=console
```

### `win.hide`

隐藏窗口(状态保留,可再唤出)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_hide`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |

```text
win.hide name=console
```

### `win.list`

列出全部窗口及状态

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`readonly`，当前策略可见，工具名 `win_list`

参数：无。

### `win.ratio`

调整窗口占主窗体的比例

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_ratio`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |
| `value` | `double` | 是 | - | - | 比例 0~1 |

```text
win.ratio name=table value=0.3
```

### `win.reset`

把窗口复位到注册时的默认位置

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_reset`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |

```text
win.reset name=console
```

### `win.show`

显示窗口(隐藏则唤出,已显示则激活)

- 来源：`framework`
- 安全：普通
- UI 线程：是
- MCP：`standard`，当前策略可见，工具名 `win_show`

| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |
|---|---|---|---|---|---|
| `name` | `string` | 是 | - | - | 窗口名(win.list 可查) |

```text
win.show name=console
```
