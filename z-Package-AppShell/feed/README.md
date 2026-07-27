# Feed

本目录保存 AppShell 不可覆盖的正式本地版本包。当前版本 0.7.2 包含：

- `OneHistory.AppShell.Core.0.7.2.nupkg` / `.snupkg`
- `OneHistory.AppShell.Services.0.7.2.nupkg` / `.snupkg`
- `OneHistory.AppShell.Shell.0.7.2.nupkg` / `.snupkg`
- `OneHistory.AppShell.ServiceHost.0.7.2.nupkg` / `.snupkg`
- `AppShell-Demo-0.7.2-win-x64-framework-dependent.zip`

消费项目应把本目录加入 `NuGet.Config`，桌面应用引用 `OneHistory.AppShell.Shell`，服务应用额外引用
`OneHistory.AppShell.ServiceHost`。本地 feed
不是 NuGet.org 镜像；第三方依赖仍从 nuget.org 恢复。

同一版本号禁止覆盖。需要修复时发布新的补丁版本。
