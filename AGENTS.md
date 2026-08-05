# MyAPI V4 AI 工作合同

本仓库用于 MyAPI V4 探索。它与 `2026-023-AppShell` 的 V3.0.3 冻结实现完全隔离；V4 的实验性设计不得回写 AppShell 3.0 或其正式包。

## 启动读取顺序

1. 读取根目录 `project.manifest.json`，确认项目身份、状态、活动目录和验证命令。
2. 读取根目录 `README.md` 与 `b-Office/current/overview.md`。
3. 涉及实现时读取 `b-Office/current/technical-contract.md`；涉及目录治理读取 `b-Office/document-center.md`；涉及消费时读取 `b-Office/package/reuse.md`；涉及模块实验时读取 `Module/模块开放说明.md`。
4. 只进入 manifest 声明的活动目录。`b-Office/history/`、`Unused/`、`**/bin/`、`**/obj/` 和 `**/.vs/` 默认不进入源码上下文。

## 真值与边界

- 用户当前指令决定任务范围，但不隐式授权 commit、tag、push、发布或删除。
- V4 当前真值由 `b-Code-MyAPI/`、外置测试模块 `b-Code-TestModule/`、临时模块实验区 `Module/`、`b-Office/current/`、测试和运行事实共同决定。
- `b-Office/package/` 是对外消费说明的编辑源；本仓库当前没有正式发布快照。
- 文档与实现冲突时必须指出冲突，不能静默改写其中一方。
- AppShell 是前端宿主模块，OHS 业务页面和后端业务能力都只能作为独立模块接入；不得在本仓库复制 AppShell 源码。

## V4 探索约束

- 当前版本状态为 `exploration`，不宣称 API 稳定，不兼容承诺，不生成 3.0.x 修复。
- 模块分为后端模块（命令、服务、管线）和前端模块（页面、宿主表面）；页面通过命令管线调用后端。
- MCP、HTTP 监听、模块扫描和后台能力默认关闭；消费方必须显式启用并承担安全配置。
- 反射暴露仅是 Lite 原型能力，任何公开命令都需要显式注册、权限、审计和参数校验设计。
- `b-Code-TestModule/` 只是独立模块接入夹具，不得成为 MyAPI 生产内核的运行时依赖或发布内容。
- `Module/` 是 OHS、AppShell 等上层模块的临时源码和工程实验区；其中内容默认不进入 V4 发布包，不替代 AppShell 3.0.3 正式仓库，也不构成稳定 API。

## 实施与验证

- 修改前后检查 Git 状态，保留用户已有改动，不回退无关文件。
- 先运行直接相关的快速检查，再按风险运行构建和冒烟测试。
- 无法执行、未执行和失败必须明确记录，不能写成已通过。
- 新增活动目录、依赖或命令时，同步更新 `project.manifest.json` 和现行文档。
- 未经用户明确授权，不执行 commit、tag、push、正式发布或进一步删除。
