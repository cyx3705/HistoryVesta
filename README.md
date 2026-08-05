# 2026-020 OneHistoryStudio

本项目承载 OneHistoryStudio V2 产品线。AppShell 自 3.0.0 起由独立项目
`2026-023-AppShell` 维护；OHS 通过固定版本 NuGet 包消费框架，不再包含框架源码、发布包仓或文档副本。

## 组件

| 目录 | 职责 | 构建状态 |
|---|---|---|
| `b-Code-Studio` | OHS 产品源码 | 纳入 `OHS.sln` |
| `b-Code-Verify` | Contracts、功能 Smoke 与测试架构门禁 | 纳入 `OHS.sln` |
| `b-Publish` | 单槽测试候选、正式包历史与临时事务区 | 生成物，不入 Git |
| `z-Package` | 经 manifest/checksum 验证的正式可消费快照 | 不参与解决方案构建 |
| `b-Code-OneHistory-V1` | OneHistory V1 历史组件 | 只读，不构建 |
| `b-Office` | OHS 现行合同、模块消费文档包与历史记录 | 不构建 |

AppShell 权威源码、包仓和开发文档位于平级项目 `..\2026-023-AppShell`。OHS 只按 `Studio.csproj` 中
固定的 `PackageReference` 消费正式包，包源由根目录 `nuget.config` 指向 023 的正式 feed；框架版本与
演进计划不在 OHS 手册中维护平行文本。

OHS 只有两层发布区：`b-Publish` 保存最后一次测试候选、正式包历史和临时事务，`z-Package` 只保存
最新正式可消费快照。`C:\OneHistory\OneHistory-Push\OneHistoryStudio` 是部署运行位置，不是第三层发布区；
部署只能消费 `z-Package`。

文档入口：[OHS 文档中心](./b-Office/文档中心.md) ｜
[发布与升级](./b-Office/current/发布与升级.md) ｜
[AppShell 权威文档](../2026-023-AppShell/b-Office/README.md)

## 构建与回归

```powershell
dotnet restore .\OHS.sln
dotnet build .\OHS.sln -c Debug --no-restore
.\b-Code-Studio\eng\Test-Deploy-Studio.ps1 -Suite Wiring
```

日常功能完成只执行相关单测、Contracts、Debug 构建和定向 Smoke。完整 Debug/Release 门禁与正式发布物只在
执行 `Publish-Studio.ps1` 形成发布候选时生成。

## AI 工作边界

### 现行开发基线

- 只以当前源码、运行时行为、`b-Office/current/` 和 `b-Office/OneHistoryStudio/` 为现行开发基线。
- `b-Office/OneHistoryStudio/` 是模块及其他集成项目可直接消费的稳定文档合同。
- `b-Office/history/` 中的版本文档是留存记录，不是需求、设计或实现依据。
- AppShell 由平级 `2026-023-AppShell` 独立维护；本仓只消费冻结的 `OneHistory.AppShell.* 3.0.3` 包，
  不得复制框架源码或权威文档。
- `bin/`、`obj/`、`.vs/` 和 `b-Publish/` 不入库；正式 `z-Package` 快照通过 Git LFS 保留。

### 历史文档读取规则

- 默认不得列举、搜索、批量读取或概括 `b-Office/history/` 下的任何内容，也不得主动搜索 Git 中的版本历史。
- 仓库级全文检索必须遵守根目录 `.ignore`；不得为了扩大结果而绕过其中对
  `b-Office/history/` 的排除。
- ripgrep 的正向 `-g` / `--glob` 会覆盖 `.ignore`。任何覆盖仓库根目录或 `b-Office/` 的此类命令，
  都必须同时添加 `-g '!b-Office/history/**'`；不得先执行宽泛检索、再从输出中过滤历史文档。
- 只有用户明确要求追溯某个版本、核对某份 V 文档，或明确要求继续维护指定版本文档时，才可读取
  直接相关的指定文件；读取范围应保持最小。
- 历史文档与源码、运行时或现行文档冲突时，以源码、运行时、`current/` 和 `OneHistoryStudio/` 为准，不得用旧文档
  覆盖现行事实。
- 版本交付时，把仍然有效的产品行为和运维规则融入 `current/`，跨项目消费合同融入 `OneHistoryStudio/`。
  V 文档原文可以留存，但不得成为后续任务的前置阅读材料。
