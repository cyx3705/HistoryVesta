# AppShell 发布输入

本目录只保存发布流水线读取的元数据和模板，不是消费文档目录。

- `consumer-docs.json`：声明从 `../package/` 复制到版本化 `docs/` 的消费合同。
- `AppShell.reuse.template.md`：生成当前正式 `z-Package-AppShell/AppShell.reuse.md` 的模板。
- `CurrentSnapshot.README.template.md`：正式发布时生成 z 级当前快照 README 的模板。

发布脚本可以读取这里的文件，但不得把本 README、JSON 清单或模板原文复制进消费 `docs/`。

历史包链路验证使用 `Publish-AppShell.ps1 -Version <版本> -VirtualPublish`。虚拟发布只读取 `b-Publish/feed` 中同版本的四个归档包，并写入 `b-Publish/virtual/<版本>/`；它不构建源码、不读写 z 级当前快照，也不声明当前消费合同与历史包兼容。

需要测试或回滚到已验证的历史快照时，显式增加 `-DeployToZ`。脚本先完整校验 `b-Publish/virtual/<版本>/`，再整体替换 z 级目录，并把快照内部的 `feed/`、`docs/`、复用说明、README、manifest 和校验和直接展开到 z 根目录。z 不保存版本号外层目录，旧内容不会与新版本混合。
