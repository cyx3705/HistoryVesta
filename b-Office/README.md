# OneHistoryStudio 文档中心

> 当前版本：V2.4.3
> 更新日期：2026-07-25
> 面向对象：仓库开发者、维护者与 AI

这里是 OneHistoryStudio 仓库内文档的唯一导航入口。现行用户手册、一次性证据、行为快照和历史工程记录分区保存，彼此不能混用。

## 目录结构

```text
b-Office/
├─ README.md       仓库文档导航
├─ meta/           随程序发布的六份现行 Help 源文件
├─ evidence/       一次性清单与迁移证据
├─ snapshots/      运行时行为基线，只增不改
└─ versions/       版本设计、实施与验收记录
```

| 区域 | 性质 | 维护规则 |
|---|---|---|
| `meta/` | 当前产品手册，也是 `Publish\docs\` 的唯一来源 | 随当前版本更新 |
| `evidence/` | 清理、迁移等一次性事实清单 | 形成后保持原文 |
| `snapshots/` | 命令手册与控制台全量回显组成的行为基线 | 只新增版本快照，不改旧快照 |
| `versions/` | 每个版本的决策、方案、测试与交付证据 | 已交付文档不可改写 |

## 发布映射

`b-Code-Studio/Studio.csproj` 使用固定的 `Link="docs\..."` 生成最终 Help。源文件位置可以整理，发布目录和文件名不能漂移。

| 仓库源文件 | 发布文件 |
|---|---|
| [使用说明](./meta/使用说明.md) | `docs\README.md` |
| [命令手册](./meta/命令手册.md) | `docs\命令手册.md` |
| [MCP 接入与安全](./meta/MCP接入与安全.md) | `docs\MCP接入与安全.md` |
| [项目库与备份](./meta/项目库与备份.md) | `docs\项目库与备份.md` |
| [模块开发手册](./meta/模块开发手册.md) | `docs\模块开发手册.md` |
| [发布与升级](./meta/发布与升级.md) | `docs\发布与升级.md` |

## 旧路径映射

历史文档中的路径记录的是当时事实，不回写旧文档。定位旧引用时按下表换算：

| 历史写法 | 当前位置 |
|---|---|
| `docs\00-...` 至 `docs\28-...` | `b-Office\versions\` 下同名文件 |
| `docs\snapshots\vNNN\` | `b-Office\snapshots\vNNN\` |
| `docs\命令手册.md`、`docs\README.md` 等现行 Help | `b-Office\meta\` 中对应源文件 |
| `b-Office\OHS\...` | 删除中间的 `OHS\`，其余相对结构不变 |
| `b-Code-OneHistoryStudio\...` | V2.4.1 后产品源码位于根级 `b-Code-Studio\` |

## 现行手册

- [使用说明](./meta/使用说明.md)：面向最终用户的 Help 首页。
- [命令手册](./meta/命令手册.md)：运行时注册表自动生成的全部指令、参数和 MCP 投影。
- [MCP 接入与安全](./meta/MCP接入与安全.md)：AppShell 网关、Studio 白名单、策略、鉴权、确认和审计。
- [项目库与备份](./meta/项目库与备份.md)：裸仓库、worktree、Git 规则、LFS、提交与恢复。
- [模块开发手册](./meta/模块开发手册.md)：AppShell 模块契约、装载、热重载、面板和 Studio 工具同步。
- [发布与升级](./meta/发布与升级.md)：版本身份、构建、整目录部署、回滚和历史交付记录。

## 一次性证据

- [V2.3.3 部署物清理清单](./evidence/部署物清理清单-V2.3.3.md)
- [V2.4.0 M0 迁移前现状清单](./evidence/V2.4.0-M0-迁移前现状清单.md)

## 行为快照

快照是“结构重构未改变对外行为”的客观对账依据，不是用户手册。每组至少包含运行时生成的 `command-manual.md` 与 `console-baseline.log`；后者覆盖 `help`、`win.list`、`proj.config`、`mcp.schema` 和 `command.list` 的全量回显。

| 快照 | 命令手册 | 控制台基线 | 采集说明 |
|---|---|---|---|
| v215 | [manual](./snapshots/v215/command-manual.md) | [console](./snapshots/v215/console-baseline.log) | - |
| v216 | [manual](./snapshots/v216/command-manual.md) | [console](./snapshots/v216/console-baseline.log) | - |
| v217 | [manual](./snapshots/v217/command-manual.md) | [console](./snapshots/v217/console-baseline.log) | - |
| v220 | [manual](./snapshots/v220/command-manual.md) | [console](./snapshots/v220/console-baseline.log) | - |
| v221 | [manual](./snapshots/v221/command-manual.md) | [console](./snapshots/v221/console-baseline.log) | - |
| v232 | [manual](./snapshots/v232/command-manual.md) | [console](./snapshots/v232/console-baseline.log) | [README](./snapshots/v232/README.md) |
| v233 | [manual](./snapshots/v233/command-manual.md) | [console](./snapshots/v233/console-baseline.log) | [README](./snapshots/v233/README.md) |
| v242 | [manual](./snapshots/v242/command-manual.md) | [console](./snapshots/v242/console-baseline.log) / [修复前](./snapshots/v242/console-baseline-pre-autostart.log) | [README](./snapshots/v242/README.md) |
| v243-before | [manual](./snapshots/v243-before/command-manual.md) | [console](./snapshots/v243-before/console-baseline.log) | [README](./snapshots/v243-before/README.md) |
| v243 | [manual](./snapshots/v243/command-manual.md) | [console](./snapshots/v243/console-baseline.log) | [README](./snapshots/v243/README.md) |

V2.2.2、V2.3.0、V2.3.1、V2.4.0 和 V2.4.1 没有留下对应快照，不能事后伪造。V2.4.2 只从当前可复现运行时补采 `v242`，并在其 README 中明确与 `v233` 的差异归因。
V2.4.3 同时保留改造前 `v243-before` 与改造后 `v243`，用于逐条证明代码卫生改写没有改变外部行为。

## 版本工程文档

以下链接是完整证据链。除正在实施的最新文档外，已交付版本只读：

- [00 开发提示](./versions/00-开发提示.md)
- [01 需求文档](./versions/01-需求文档.md)
- [02 项目验收单](./versions/02-项目验收单.md)
- [03 V2.0.1 Meta 文件窗口](./versions/03-V2.0.1-Meta文件窗口.md)
- [04 V2.1.0 工程实施](./versions/04-V2.1.0-工程实施文档.md)
- [05 源码治理与项目边界规划](./versions/05-源码治理与项目边界规划.md)
- [06 V2.1.0-M1.5 工程修复快照](./versions/06-V2.1.0-M1.5-工程修复快照.md)
- [07 V2.1.0-M3 Codex 联调测试](./versions/07-V2.1.0-M3-Codex联调测试.md)
- [08 V2.1.1 管理页更新](./versions/08-V2.1.1-管理页更新.md)
- [09 V2.1.2 MCP 提示词治理](./versions/09-V2.1.2-MCP提示词治理.md)
- [10 V2.1.3 Git 属性调试与命令集管理](./versions/10-V2.1.3-Git属性调试与命令集管理.md)
- [11 V2.1.4 文档、Help 与程序交付](./versions/11-V2.1.4-文档Help与程序交付.md)
- [12 V2.1.2 至 V2.1.4 四步实施总表](./versions/12-V2.1.2至V2.1.4-四步实施总表.md)
- [13 V2.1.3-M2 Git 管理简约化](./versions/13-V2.1.3-M2-Git管理简约化设计.md)
- [14 V2.1.5 项目操作语义与指令详情拆窗](./versions/14-V2.1.5-项目操作语义与指令详情拆窗.md)
- [15 V2.1.6 代码质量整备](./versions/15-V2.1.6-代码质量整备.md)
- [16 V2.1.7 提交推送归位与项目选择同步](./versions/16-V2.1.7-提交推送归位与项目选择同步.md)
- [17 V2.2.0 工程实施](./versions/17-V2.2.0-工程实施文档.md)
- [18 V2.2.0-M3 Codex 飞轮操作手册](./versions/18-V2.2.0-M3-Codex飞轮操作手册.md)
- [19 V2.2.0-M4 确认中继交互验收](./versions/19-V2.2.0-M4-确认中继交互验收.md)
- [20 V2.2.1 文件格式全覆盖扫描](./versions/20-V2.2.1-文件格式全覆盖扫描.md)
- [21 V2.2.2 Git 文件规则全格式入表](./versions/21-V2.2.2-Git文件规则全格式入表.md)
- [22 V2.3.0 分支历史与安全回滚](./versions/22-V2.3.0-分支历史与安全回滚.md)
- [23 V2.3.1 子模块提交与推送](./versions/23-V2.3.1-子模块提交与推送.md)
- [24 V2.3.2 提交操作归位与四级范围](./versions/24-V2.3.2-提交操作归位与四级范围.md)
- [25 V2.3.3 体积治理与代码质量整备](./versions/25-V2.3.3-体积治理与代码质量整备.md)
- [26 V2.4.0 伞形结构迁移](./versions/26-V2.4.0-伞形结构迁移.md)
- [27 V2.4.1 骨干削薄与源码上抛](./versions/27-V2.4.1-骨干削薄与源码上抛.md)
- [28 V2.4.2 文档整理](./versions/28-V2.4.2-文档整理.md)
- [29 V2.4.3 代码卫生与自管理](./versions/29-V2.4.3-代码卫生与自管理.md)

## 架构参考

- [AppShell 二次开发演进手册](../b-Code-AppShell/docs/二次开发演进手册.md)
- [AppShell 框架需求规划](../b-Code-AppShell/docs/通用窗口框架模板_需求规划_README_3.md)
- [AppShell 版本记录](../b-Code-AppShell/docs/AppShell版本记录.md)

发生描述冲突时，现行操作以运行时 `help`、命令集页面、自动生成命令手册和当前源码为准；历史版本文档只解释当时决策。
