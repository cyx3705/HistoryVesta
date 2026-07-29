# AppShell 发布区

`b-Publish/` 是本仓库唯一的完整发布工作区，直接承载 AppShell 候选构建、不可覆盖历史归档、虚拟快照和发布证据，不再设置多余的 `AppShell/` 子层。

本目录不是消费项目或 AI 的默认接入入口。当前启用快照始终位于 `../z-Package-AppShell/`；只有升级、回滚、审计或排障时才按需读取这里的历史内容。

## 目录结构

| 路径 | 可变性 | 用途 |
| --- | --- | --- |
| `staging/<版本>/` | 可删除重建 | 构建、测试、漏洞审计、PackageSmoke 和 Demo 验收候选；已被 Git 忽略 |
| `feed/` | 正式归档不可覆盖 | 历史 `.nupkg`、`.snupkg` 和 Demo ZIP |
| `docs/<版本>/` | 正式归档不可覆盖 | 从 `../b-Office/package/` 自动生成的版本化消费合同 |
| `reuse/<版本>/AppShell.reuse.md` | 正式归档不可覆盖 | 每次正式发布的精简复用合同 |
| `manifest/` | 正式归档不可覆盖 | 版本、源码提交、构建环境与产物元数据 |
| `checksums/` | 正式归档不可覆盖 | 正式归档文件的 SHA-256 |
| `changelog/` | 历史保留 | 版本变更和部署证据 |
| `virtual/<版本>/` | 可重复生成 | 历史四包与当前消费合同的链路验证快照，不代表兼容性已验证 |

尚未发生对应正式发布时，`docs/` 或 `reuse/` 可以不存在；发布脚本负责在首次归档时创建。

## 发布流程

```powershell
# 重建可覆盖候选，不写正式归档和 z
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.0

# 审核通过后写入不可覆盖历史归档，并整体替换 z 当前快照
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.0 -Publish

# 只在本目录生成历史版本的虚拟验证快照
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish

# 显式将已验证快照内部内容整体部署到 z，用于测试或回滚
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish -DeployToZ
```

虚拟发布从 `feed/` 读取指定版本的四个历史运行时包，输出到 `virtual/<版本>/`。`-DeployToZ` 会先校验包身份、manifest 和全文件校验和，再整体替换 z；z 根目录不设置版本号外层目录，也不会保留上一个快照的残余文件。

## 保留规则

- `feed/`、`docs/`、`reuse/`、`manifest/` 和 `checksums/` 中的正式版本一经归档不得覆盖。
- `staging/` 只是候选工作区，可以删除重建，不得作为长期包源。
- `virtual/` 只承担生成链验证和回滚准备；其 manifest 必须保留 `channel=virtual` 与真实兼容性标记。
- z 只表示当前启用快照，历史版本必须继续保存在本目录。
- 发布脚本不执行 Git commit、tag、push，也不向 NuGet.org 或其他外部包源推送。
- 消费项目默认读取 `../z-Package-AppShell/AppShell.reuse.md` 和 `../z-Package-AppShell/feed/`，不得默认扫描整个发布归档。

当前 `0.7.2` 历史包及虚拟快照保存在本目录；z 中的 `0.7.2` 是测试部署快照，manifest 明确标记为 `channel=virtual`、`compatibilityValidated=false`。
