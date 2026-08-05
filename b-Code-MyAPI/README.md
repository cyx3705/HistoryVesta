# MyAPI 后端运行时原型

本目录保留 MyAPI V4 的可运行原型：`MyAPI.sln`、`src/MyAPI.Abstractions` 合同、`src/MyAPI.Runtime` 命令内核、`src/MyAPI.Host` 宿主和 `tests/` 契约测试。用于模块加载验收的能力与注册适配位于同级 `b-Code-TestModule/`，不属于生产内核。

详细的运行方式、V4 边界和消费限制见仓库 [文档中心](../b-Office/document-center.md)。宿主默认空闲，HTTP、MCP 和模块扫描都必须显式启用。
