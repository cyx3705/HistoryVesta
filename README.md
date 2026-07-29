# 2026-020 OneHistoryStudio

本项目承载 OneHistoryStudio V2 产品线。AppShell 自 3.0.0 起由独立项目
`2026-023-AppShell` 维护；OHS 通过固定版本 NuGet 包消费框架，不再包含框架源码、发布包仓或文档副本。

## 组件

| 目录 | 职责 | 构建状态 |
|---|---|---|
| `b-Code-Studio` | OHS 产品源码、Contracts 与 Smoke | 纳入 `OHS.sln` |
| `b-Code-Studio.Service` | OHS 常驻后台服务入口 | 独立构建 |
| `b-Publish` | 可覆盖的本机发布暂存快照 | 生成物，不入 Git |
| `z-Package` | 经 manifest/checksum 验证的正式可消费快照 | 不参与解决方案构建 |
| `b-Code-OneHistory-V1` | OneHistory V1 历史组件 | 只读，不构建 |
| `b-Office` | OHS 现行手册、版本记录与必要证据 | 不构建 |

AppShell 权威源码、包仓和开发文档位于平级项目 `..\2026-023-AppShell`。当前消费基线为
`OneHistory.AppShell.* 3.0.0`，包源由根目录 `nuget.config` 指向 023 的正式 feed；冻结审核期间使用
023 的 `b-Publish\staging\3.0.0\packages` 候选源；`z-Package-AppShell\feed` 仅在正式提升后使用。

OHS 发布流固定为 `b-Publish` 暂存、`z-Package` 正式包、
`C:\OneHistory\OneHistory-Push\OneHistoryStudio` 部署；部署只能消费 `z-Package`。

文档入口：[OHS 文档中心](./b-Office/README.md) ｜
[发布与升级](./b-Office/meta/发布与升级.md) ｜
[AppShell 权威文档](../2026-023-AppShell/b-Office/README.md)

## 构建与回归

```powershell
dotnet restore .\OHS.sln
dotnet build .\OHS.sln -c Debug --no-restore
dotnet build .\OHS.sln -c Release --no-restore
dotnet test .\b-Code-Studio\tests\Contracts\Contracts.csproj -c Debug --no-build
dotnet run --project .\b-Code-Studio\tests\Smoke\Smoke.csproj -c Debug --no-build
```

## AI 工作边界

### 现行开发基线

- 只以当前源码、运行时行为和 `b-Office/meta/` 为现行开发基线。
- `b-Office/versions/` 中的 V 版本文档是留存记录，不是需求、设计或实现依据。
- AppShell 由平级 `2026-023-AppShell` 独立维护；本仓只消费冻结的 `OneHistory.AppShell.* 3.0.0` 包，
  不得复制框架源码或权威文档。
- `bin/`、`obj/`、`.vs/` 和 `b-Publish/` 不入库；正式 `z-Package` 快照通过 Git LFS 保留。

### 历史文档读取规则

- 除治理说明 `b-Office/versions/README.md` 外，默认不得列举、搜索、批量读取或概括
  `b-Office/versions/` 下的任何内容，也不得主动搜索 Git 中的版本历史。
- 仓库级全文检索必须遵守根目录 `.ignore`；不得为了扩大结果而绕过其中对
  `b-Office/versions/` 的排除。
- ripgrep 的正向 `-g` / `--glob` 会覆盖 `.ignore`。任何覆盖仓库根目录或 `b-Office/` 的此类命令，
  都必须同时添加 `-g '!b-Office/versions/**'`；不得先执行宽泛检索、再从输出中过滤历史文档。
- 只有用户明确要求追溯某个版本、核对某份 V 文档，或明确要求继续维护指定版本文档时，才可读取
  直接相关的指定文件；读取范围应保持最小。
- 历史文档与源码、运行时或 `b-Office/meta/` 冲突时，以源码、运行时和 `meta/` 为准，不得用旧文档
  覆盖现行事实。
- 版本交付时，把仍然有效的产品行为、接口合同、运维规则和升级信息融入对应 `meta/` 文件。
  V 文档原文可以留存，但不得成为后续任务的前置阅读材料。
