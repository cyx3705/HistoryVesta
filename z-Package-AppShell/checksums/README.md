# Checksums

每个 `<version>.sha256` 由发布脚本生成，覆盖该版本的全部 `.nupkg`、`.snupkg` 和演示 ZIP。

PowerShell 核验示例：

```powershell
Get-FileHash ..\feed\OneHistory.AppShell.Shell.0.7.2.nupkg -Algorithm SHA256
```

结果必须同时匹配 checksum 文件与 manifest；任一不一致都视为产物损坏。
