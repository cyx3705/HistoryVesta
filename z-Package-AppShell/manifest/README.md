# Manifest

每个 `<version>.json` 由发布脚本生成，记录：

- 产品、版本与发布通道；
- AppShell 源码提交及源码路径是否干净；
- .NET SDK、目标框架、演示 RID 和 self-contained 状态；
- 全部正式产物的文件名、字节数和 SHA-256。

manifest 与 `checksums/<version>.sha256` 必须指向同一组不可覆盖产物。
