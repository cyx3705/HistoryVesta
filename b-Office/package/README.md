# AppShell 消费合同源（内部索引）

本目录只保存面向消费方的 AppShell 合同文档编辑源。它是 023 内部源目录，不是消费者运行时目录；发布副本不得直接修改。

## 文档流向

```text
b-Office/package/
  -> b-Publish/staging/<版本>/docs/
  -> b-Publish/docs/<版本>/
  -> z-Package-AppShell/docs/（当前正式快照）
```

`z-Package-AppShell` 同时保留当前正式包、精简复用说明和四份当前版本消费合同，是其他项目与 AI 可稳定索引的当前快照入口；这些文件均由发布脚本整体生成，不得手工修改。历史版本的消费合同以 `b-Publish/docs/<版本>` 为准。

## 消费合同

- [AppShell API 与指令手册](AppShell_API与指令手册.md)：包选择、公开 API、命令合同和最小宿主。
- [模块与 MCP 接入](AppShell_3.0_模块与MCP接入.md)：模块、工具窗口、MCP 和 Web 接入边界。
- [运行时约束与已知限制](AppShell_3.0_运行时约束与已知限制.md)：消费方必须遵守的运行时限制。
- [3.0 消费变更摘要](AppShell_3.0_消费变更摘要.md)：只保留影响消费者的版本变化。

发布清单和复用说明模板位于 `../release/`；升级手册和完整版本记录位于 `../maintenance/`。新增或改名消费手册时，必须同时更新 `../release/consumer-docs.json` 和本索引；不得直接编辑 `b-Publish/docs` 下的生成副本。
