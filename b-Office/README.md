# OneHistoryStudio 文档中心

本目录按 AIReady 模板分为现行合同、跨项目消费包和历史资料。当前源码、运行时注册表、`current/` 与
`package/` 共同构成有效基线；`history/` 只用于明确的版本追溯，不得作为新开发的默认输入。

## 目录结构

```text
b-Office/
├─ README.md   唯一文档导航
├─ current/    OHS 当前产品与运维合同
├─ package/    供模块及其他集成项目消费的稳定合同
└─ history/    已交付版本施工记录，默认不读取
```

`evidence/` 已取消。部署证据由发布 manifest、checksum、数据库备份、回滚目录、Git 提交和自动测试承载，
不再另建一次性文档区。人工备忘录保持根级原位，不属于 AI 可读取资料。

## Current

- [使用说明](./current/使用说明.md)：面向最终用户的 Help 首页。
- [项目库与备份](./current/项目库与备份.md)：项目库、worktree、Git 规则、LFS、提交与恢复。
- [发布与升级](./current/发布与升级.md)：版本身份、测试、发布、部署与回滚合同。

## Package

`package/` 是其他项目开发 OHS 模块或接入 OHS 服务时可以直接消费的文档包，只保留当前有效合同：

- [模块开发手册](./package/模块开发手册.md)：模块契约、装载、热重载、窗口和工具同步。
- [命令手册](./package/命令手册.md)：由运行时注册表生成的命令、参数和 MCP 投影。
- [MCP 接入与安全](./package/MCP接入与安全.md)：网关、策略、鉴权、确认和审计合同。

模块项目以 `package/` 为消费入口，不需要读取 OHS 的 `history/` 或内部发布记录。命令手册仍由运行时生成，
禁止手工维护命令条目。

## 发布映射

六份文档继续以原文件名发布到产品 `docs/`，源码分区不会改变用户侧 Help 路径：

| 仓库源文件 | 发布文件 |
|---|---|
| `current/使用说明.md` | `docs/README.md` |
| `current/项目库与备份.md` | `docs/项目库与备份.md` |
| `current/发布与升级.md` | `docs/发布与升级.md` |
| `package/模块开发手册.md` | `docs/模块开发手册.md` |
| `package/命令手册.md` | `docs/命令手册.md` |
| `package/MCP接入与安全.md` | `docs/MCP接入与安全.md` |

## 历史边界

`history/` 保存已交付版本文档，没有独立 README。默认不得列举、全文搜索、批量读取或概括该目录；只有用户
明确指定追溯某个版本时，才读取最小必要文件，并以当前源码、运行时、`current/` 和 `package/` 复核结论。
仍然有效的规则必须提炼回现行文档，不能让后续工作依赖历史施工记录。

AppShell 权威资料位于平级 `2026-023-AppShell/b-Office`，OHS 不保存其源码、包仓或路线图副本。
