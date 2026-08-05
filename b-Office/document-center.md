# MyAPI 文档中心

本目录是 MyAPI V4 探索的文档入口。根目录 README 只负责定位；现行约束以 `b-Office/current/` 为准，跨项目消费说明以 `b-Office/package/` 为准，历史材料只放在 `b-Office/history/`。

## 入口

| 文档 | 用途 |
| --- | --- |
| [overview](current/overview.md) | 项目身份、范围和交付状态 |
| [technical-contract](current/technical-contract.md) | MyAPI 后端运行时的现行技术边界 |
| [v4-exploration](current/v4-exploration.md) | 前后端模块化、命令管线和 AppShell/Web 层的探索模型 |
| [decisions](current/decisions.md) | 已生效的 V4 设计决定 |
| [verification-contract](current/verification-contract.md) | 构建、结构和冒烟验收方式 |
| [reuse](package/reuse.md) | 给 OHS、AppShell 和其他消费者的最小接入说明 |
| [模块开放说明](../Module/模块开放说明.md) | OHS、AppShell 等上层模块的实验性开放合同和临时工程区规则 |
| [history-boundary](history/history-boundary.md) | 历史材料的存放与读取边界 |

## 目录规范

- `b-Code-MyAPI/src` 和 `tests` 承载生产内核与契约测试；根级 `b-Code-TestModule` 承载外置测试能力和注册适配，不属于生产发布内容。
- `Module/` 承载 OHS、AppShell 等上层模块的临时源码和工程实验；它不属于正式发布包，稳定后再按模块独立评审。
- `b-Office/current/` 只保留当前有效文档，文档与实现冲突必须记录。
- `b-Office/package/` 只保留消费者真正需要的接入信息，不复制完整设计史。
- `b-Office/history/` 只保存版本背景；默认不进入日常 AI 上下文。
- `Unused/` 是旧资料区，不属于 V4 活动目录，未经单独授权不删除。
