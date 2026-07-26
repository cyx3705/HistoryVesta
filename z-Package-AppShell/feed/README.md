# Feed

本目录保存 AppShell 不可覆盖的正式本地版本包。0.5.0 包含：

- `OneHistory.AppShell.Core.0.5.0.nupkg` / `.snupkg`
- `OneHistory.AppShell.Services.0.5.0.nupkg` / `.snupkg`
- `OneHistory.AppShell.Shell.0.5.0.nupkg` / `.snupkg`
- `AppShell-Demo-0.5.0-win-x64-framework-dependent.zip`

消费项目应把本目录加入 `NuGet.Config`，并引用 `OneHistory.AppShell.Shell`。本地 feed
不是 NuGet.org 镜像；第三方依赖仍从 nuget.org 恢复。

同一版本号禁止覆盖。需要修复时发布新的补丁版本。
