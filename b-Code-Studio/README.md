# OneHistoryStudio

本目录只承载 OneHistoryStudio 产品源码。验证组件位于 `..\b-Code-Verify`，产品文档位于 `..\b-Office`；AppShell 由平级
`2026-023-AppShell` 独立维护，本项目只消费 `Studio.csproj` 中固定声明的正式包版本。

## 结构

- `Studio.csproj`：OHS 唯一产品工程与版本真值。
- `Git`、`Views`、`App.xaml*`：OHS 装配、项目管理与页面源码。
- `..\b-Code-Verify`：Contracts、Smoke 与测试架构门禁。

本机构建、候选、历史和失败隔离数据位于平级 `..\b-Publish`，正式可消费快照位于 `..\z-Package`。

## 构建

从 020 根目录执行：

```powershell
dotnet build .\OHS.sln -c Debug -p:NuGetAudit=false
dotnet build .\OHS.sln -c Release -p:NuGetAudit=false
```

## 验证

Contracts、功能 Smoke、定向 Suite 与真实鼠标规则统一见 [验证组件](../b-Code-Verify/README.md)。

## OHS 发布入口

发布统一使用 `eng\Publish-Studio.ps1`。默认模式会执行锁定还原、Debug/Release 构建、两套 Smoke、
Release 发布、运行时命令手册重生成、六份 Help 校验、版本校验、SHA-256 和 manifest，并写入新的
`b-Publish\current` 当前候选；每次使用 `b-Publish\work` 中的同卷临时候选原子替换，
不提交 Git。只有显式 `-Publish` 且
`b-Code-Studio`、`b-Code-Verify`、`b-Office` 都洁净时，才会继续把已验证暂存原子提升到
`z-Package` 正式区。两个阶段都按 manifest 和 checksum 复验完整文件集合、大小与 SHA-256。只有旧正式包
直接进入 `b-Publish\history\package-*`；旧暂存候选在事务成功后删除。`work` 和 `quarantine` 只服务
当前事务，下一次发布前清理，成功后也会移除。发布脚本不得创建仓库根 `stage`。

```powershell
.\b-Code-Studio\eng\Publish-Studio.ps1
.\b-Code-Studio\eng\Publish-Studio.ps1 -Publish
```

不得用独立的 `dotnet publish` 或手工复制替代该入口；版本始终从 `StudioVersion.props` 求值。

部署只消费 `z-Package`，目标固定为 `C:\OneHistory\OneHistory-Push\OneHistoryStudio`；该目录是运行位置，
不是第三层发布区。默认仅预览并
验证正式包；`-Apply` 才执行数据库备份和整目录事务替换。脚本不会启动、停止或重启程序，也不修改自启动：

```powershell
.\b-Code-Studio\eng\Deploy-Studio.ps1
.\b-Code-Studio\eng\Deploy-Studio.ps1 -Apply
```

本仓不包含 AppShell 源码、包仓或开发文档副本；框架契约以 023 的 3.0 权威文档为准。

产品行为、命令、MCP 工具形态和页面布局以当前源码、运行时和
[现行手册](../b-Office/文档中心.md) 为准；构建、Smoke、GUI、发布与部署门见
[发布与升级](../b-Office/current/发布与升级.md)。已交付版本文档不得作为开发前置资料。
