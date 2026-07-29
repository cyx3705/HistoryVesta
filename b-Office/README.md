# AppShell 文档中心

本目录保存 AppShell 3.0 的现行合同、消费文档源、冻结证据和冻结方案。
消费项目不应维护手工副本。其他项目和 AI 先读取 Z 级当前快照中的精简复用说明，再按需索引同级 `docs/` 下的完整消费合同。

## 消费文档编辑源

`package/` 是面向消费方的合同编辑源。以下四份文档由发布脚本复制到候选目录、正式
`b-Publish/docs/<版本>` 和 `z-Package-AppShell/docs/` 当前快照，不再直接写入发布目录或重复装入四个 NuGet 包。
发布副本不得直接修改；发布名单由 `release/consumer-docs.json` 固定：

- [消费文档源索引](package/README.md)
- [AppShell API 与指令手册](package/AppShell_API与指令手册.md)
- [模块与 MCP 接入](package/AppShell_3.0_模块与MCP接入.md)
- [运行时约束与已知限制](package/AppShell_3.0_运行时约束与已知限制.md)
- [3.0 消费变更摘要](package/AppShell_3.0_消费变更摘要.md)

## 内部维护与发布输入

- [AppShell 升级手册](maintenance/AppShell升级手册.md)：消费项目迁移、候选验证和回滚流程，不随消费合同发布。
- [AppShell 版本记录](maintenance/AppShell版本记录.md)：完整开发史和维护者技术记录，不随消费合同发布。
- `release/consumer-docs.json`：发布消费文档清单。
- `release/AppShell.reuse.template.md`：生成正式精简复用说明的模板。

## 内部合同与证据

- [通用窗口框架需求规划](通用窗口框架模板_需求规划_README_3.md)
- [冻结执行证据](AppShell_3.0_冻结执行证据.md)
- [冻结前代码审查与整改清单](AppShell_3.0_冻结前代码审查与整改清单.md)：FZR 编号的放行前检查单；整改并复审通过前不得打 3.0 标签。
- [默认最小能力问题整改](AppShell_3.0_默认最小能力问题整改.md)：3.0.0 发布后发现的默认 MCP/模块隐式启用问题、同类项审计与 3.0.1 验证证据。

## 冻结施工

- [v3.0 冻结演进方案](AppShell_v3.0_冻结演进方案.md)：M1-M8 的实施依据；施工结束后只作历史审计。

## 待评审改造

- [中央主工作区体验改造](AppShell_3.0_中央主工作区体验改造.md)：以命令集作为默认中央窗口，支持独立页面头、多页面选择和四边工具页拖入/拖回；已实施，等待冻结审核。

框架公开面以四个 `PublicAPI.Shipped.txt`、包内 XML API 文档、运行时命令注册表和上述现行合同共同为准。
发布脚本从 `release/AppShell.reuse.template.md` 生成当前正式版的 `../z-Package-AppShell/AppShell.reuse.md`；
当前版本完整消费合同随它写入 `../z-Package-AppShell/docs/`。历史文档、历史包、符号、Demo、归档 manifest、归档校验和及候选包保存在 `../b-Publish/`。
