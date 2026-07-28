# OneHistoryStudio 文档中心

> 当前版本：V2.7.3
> 更新日期：2026-07-28
> 面向对象：仓库开发者、维护者与 AI

这里是 OneHistoryStudio 仓库内文档的唯一导航入口。现行手册、一次性证据和当前版本施工文档分区保存，彼此不能混用。

> **长期治理规则（2026-07-28 起强制执行）**：未来开发只以当前源码、运行时注册表和 `meta/`
> 现行手册为基线，不得要求开发者或 AI 阅读已交付的 V 版本文档才能继续工作。`versions/` 只保存
> 当前版本相对 `meta/` 的施工增量；版本交付时必须把仍有效的行为、架构、安全、升级和回滚合同融入
> 对应 `meta/` 手册，重新生成命令手册，随后让该施工文档退出当前工作树。这里的“删除”不是销毁历史：
> 原文完整保留在 Git 中，需要时可只读恢复。退出工作树是为了避免 AI 在日常检索中反复读取旧文档，
> 占用上下文和 token，并把过期结论带入当前开发。

## 目录结构

```text
b-Office/
├─ README.md       仓库文档导航
├─ meta/           随程序发布的六份现行 Help 源文件
├─ evidence/       小型、不可替代的一次性文本证据
└─ versions/       当前版本施工增量；交付后抽干并删除
```

| 区域 | 性质 | 维护规则 |
|---|---|---|
| `meta/` | 当前产品手册，也是 `Publish\docs\` 的唯一来源 | 随当前版本更新 |
| `evidence/` | 部署、清理、迁移和实验形成的小型文本事实 | 形成后保持原文；禁止数据库、压缩包、完整发布物和可再生成的原始输出 |
| `versions/` | 当前在制版本相对现行手册的设计、实施与验收增量 | 不得成为现行基线；交付时抽入 `meta/` 并删除，详细规则见 `versions/README.md` |

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
| `docs\00-开发提示.md` | 现行规则见 `meta\发布与升级.md`、`meta\模块开发手册.md` |
| `docs\01-需求文档.md` | 现行产品合同见 `meta\使用说明.md`、`meta\项目库与备份.md`、`meta\模块开发手册.md` |
| `docs\02-项目验收单.md` | 通用验收基线见 `meta\发布与升级.md`；旧执行记录使用 Git 历史读取 |
| `docs\03-V2.0.1-Meta文件窗口.md` | 现行规则见 `meta\使用说明.md`、`meta\项目库与备份.md`；交付摘要见 `meta\发布与升级.md` |
| `docs\04-...` 起的已交付版本文档 | 已从工作树抽干；使用 Git 历史读取，不得恢复为开发依赖 |
| `docs\snapshots\vNNN\` | 快照目录已于 2026-07-28 删除；使用 Git 历史读取 |
| `b-Office\evidence\main-db-predeploy-*.db` | 已迁至 `C:\OneHistory\OneHistory-Push\OneHistoryStudio-DatabaseBackups\` |
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
- [V2.4.5 structuredContent 客户端实验](./evidence/v245-structuredcontent-probe/README.md)
- [AppShell 0.7.2 正式部署证据](./evidence/deploy-appshell-0.7.2-20260727-232824.md)
- [OneHistoryStudio 2.7.3 正式部署证据](./evidence/deploy-ohs-2.7.3-20260728-091042.md)

## 历史快照（已取消）

`b-Office/snapshots/` 已于 2026-07-28 整体删除。它没有代码、测试或发布消费方，完整输出副本不能形成
自动回归门禁。历史调查使用 Git tag、提交和正式部署证据；确需读取旧文件时使用
`git log --all -- b-Office/snapshots` 与 `git show <提交>:<路径>`，不把快照重新写回工作树。

V2.7.5 将在测试目录维护由 Smoke 实际消费的紧凑契约，只保存命令数量、目录哈希和必要协议字段；
完整命令手册、控制台输出和接口响应只进入临时测试产物或 CI artifacts，不进入源码仓库。

## 版本施工文档

[治理与生命周期](./versions/README.md) 是 `versions/` 中唯一长期保留的文件。当前在制文档可以暂存于
该目录，但必须只描述相对六份现行手册的增量，不得复制历史基线，也不得引用已交付版本文档作为前置条件。

- [当前 V2.7.5 代码管道化与质量修复](./versions/37-V2.7.5-代码管道化手册.md)

已交付版本的用户可见变化、迁移和回滚摘要统一见 [发布与升级](./meta/发布与升级.md)。需要调查原始施工过程时：

```powershell
git log --all -- b-Office/versions
git show <提交>:b-Office/versions/<历史文件名>.md
```

读取历史仅用于调查。可以临时通过 `git show` 查看，但不得把旧文件长期恢复到仓库工作树；否则它仍可能
被 AI 默认搜索和读取，增加 token 消耗并干扰当前判断。

## 架构参考

- [AppShell 文档索引](./appshell/README.md)
- [AppShell 二次开发演进手册](./appshell/二次开发演进手册.md)
- [AppShell 框架需求规划](./appshell/通用窗口框架模板_需求规划_README_3.md)
- [AppShell 版本记录](./appshell/AppShell版本记录.md)

发生描述冲突时，现行操作以当前源码、运行时 `help`、命令集页面、自动生成命令手册和六份 `meta`
手册为准；当前版本施工文档只描述尚未交付的增量，不能覆盖现行基线。
