# 2026-020 OneHistoryStudio

本项目是 OneHistoryStudio V2 产品线的伞形元项目，由 `2026-018-MyAPI` 在产品血缘上继承而来。
V2.4.0 起，OHS、AppShell、历史版本、产品文档和未来框架包在同一项目边界内协同演进；
V2.4.1 将产品源码、样例与发布快照拆成平级组件，进一步缩短内部路径。

## 组件

| 目录 | 职责 | 构建状态 |
|---|---|---|
| `b-Code-AppShell` | AppShell 唯一框架源码与独立演示宿主 | 纳入 `OHS.sln` |
| `b-Code-Studio` | OHS 产品源码与 Smoke | 纳入 `OHS.sln` |
| `b-Code-Samples` | 模块开发样例 | 独立构建 |
| `b-Publish` | 当前正式发布快照 | 不参与解决方案构建 |
| `b-Code-OneHistory-V1` | OneHistory V1 历史组件 | 只读，不构建 |
| `b-Office` | OHS 现行手册、版本工程文档与行为快照 | 不构建 |
| `z-Package-AppShell` | AppShell 对外版本包空壳 | 不参与内部构建 |

文档入口：[OHS 文档中心](./b-Office/meta/README.md) ｜
[V2.4.1 结构重构与交付记录](./b-Office/versions/27-V2.4.1-骨干削薄与源码上抛.md) ｜
[AppShell 演进手册](./b-Code-AppShell/docs/二次开发演进手册.md)

## 构建入口

```powershell
dotnet build .\OHS.sln -c Debug -p:NuGetAudit=false
dotnet build .\OHS.sln -c Release -p:NuGetAudit=false
```

AppShell 也可在 `b-Code-AppShell` 中使用 `AppShell.sln` 独立构建。OHS 直接引用 AppShell
源码，不再维护副本、模板哈希对账或回灌流程。

## 演进规则

- 020 长期承载 OHS V2.x，小版本通过提交、标签和 `b-Office/versions` 管理。
- 只有架构代际变化才从 020 创建新项目，例如未来的 OHS V3。
- AppShell 公共契约变更必须通过框架演示宿主、OHS Smoke 和 GUI 实跑。
- `bin/obj/.vs` 不入库；正式 Publish 作为当前交付快照保留。
