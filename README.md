# AppShell 3.0

本仓库是 OneHistory AppShell 的独立源码、冻结合同与发布资产真值。`3.0.0` 是长期冻结基线；
版本线不再与 OneHistoryStudio 对齐，`0.7.x` 仅保留用于回滚。

## 仓库结构

| 路径 | 内容 |
|---|---|
| `b-Code-AppShell/` | Core、Services、Shell、ServiceHost、演示宿主与测试 |
| `b-Code-Samples/` | 模块开发示例 |
| `b-Office/package/` | 消费文档编辑源 |
| `b-Office/maintenance/` | 内部迁移、升级、回滚和完整版本历史 |
| `b-Office/release/` | 发布清单和复用说明模板 |
| `b-Office/` | 冻结契约、内部设计与执行证据 |
| `b-Publish/` | 候选构建、历史包、符号、Demo、完整文档和发布证据 |
| `z-Package-AppShell/` | 当前发布快照的展开内容，不保存历史版本目录 |

根级 `AppShell.sln` 是仓库验收入口，只包含六个冻结项目；组件目录内的
`b-Code-AppShell/AppShell.sln` 是发布脚本使用的等价入口。

## 构建与测试

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --no-restore
dotnet format .\AppShell.sln --verify-no-changes --no-restore
```

## 审核候选与正式发布

```powershell
# 重建 b-Publish/staging 下的可覆盖审核候选
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.0

# 仅在审核通过、代码和消费文档均已提交且干净后执行
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.0 -Publish

# 使用 b-Publish 归档中的历史包验证发布生成链，只更新 b-Publish/virtual
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish

# 测试/回滚时将已验证快照的内部内容整体替换到 z 根目录
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish -DeployToZ
```

审核阶段的消费方必须临时指向
`b-Publish/staging/3.0.0/packages`；正式提升后改用
`z-Package-AppShell/feed`。`z-Package-AppShell` 始终只保留一个当前发布快照的展开内容，不建立版本号外层目录；每次发布都会整体替换旧内容，历史和虚拟发布只保存在 `b-Publish`。发布脚本不会执行 Git commit、tag、push，也不会推送 NuGet.org。

桌面消费者通常引用 `OneHistory.AppShell.Shell`；服务化宿主额外引用
`OneHistory.AppShell.ServiceHost`。当前已验证消费方为 OneHistoryStudio（020）和 WBall（022）。

维护入口见 [b-Office/README.md](b-Office/README.md)。其他项目和 AI 先读取
`z-Package-AppShell/AppShell.reuse.md`，再按需索引同一当前快照中的 `z-Package-AppShell/docs/`；历史版本文档与发布证据从 `b-Publish` 查阅。
