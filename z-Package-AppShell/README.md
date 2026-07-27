# AppShell 版本包

本目录是 AppShell 对外版本包的交付位，不参与 020 内部 OHS 构建。OHS 继续直接引用
`b-Code-AppShell` 的唯一源码。

- `feed`：不可覆盖的 `.nupkg`、`.snupkg` 与演示 ZIP。
- `manifest`：版本、源码提交、SDK、目标框架和产物哈希。
- `checksums`：全部正式产物的 SHA-256。
- `changelog`：逐版本变更和兼容性说明。
- `staging`：发布脚本的可覆盖验收区，不进入 Git。

当前正式版本为 **0.7.2**。完整桌面消费者只需引用：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="0.7.2" />
```

服务宿主消费者额外引用 `OneHistory.AppShell.ServiceHost`。使用本地 feed 时，把 `feed` 的绝对路径加入消费项目的 `NuGet.Config`。0.7.2 仅供
OneHistory 自有项目使用；仓库尚未确定公共许可证，因此没有推送 NuGet.org。

发布入口为 `b-Code-AppShell/eng/Publish-AppShell.ps1`。脚本负责构建、漏洞审计、隔离包消费、
演示发布、哈希和清单，不执行 Git commit/tag/push，也不向外部包源推送。
