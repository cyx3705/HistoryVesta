# OneHistoryStudio

本目录只承载 OneHistoryStudio 产品源码和测试。产品文档位于 `..\b-Office`；AppShell 由平级
`2026-023-AppShell` 独立维护，本项目固定消费 `OneHistory.AppShell.* 3.0.0` 包。

## 结构

- `Studio.csproj`：OHS 唯一产品工程与版本真值。
- `Git`、`Views`、`App.xaml*`：OHS 装配、项目管理与页面源码。
- `tests/Smoke`：合并后的完整自动化冒烟套件。

可覆盖的发布暂存快照位于平级 `..\b-Publish`，正式可消费快照位于 `..\z-Package`。

## 构建

从 020 根目录执行：

```powershell
dotnet build .\OHS.sln -c Debug -p:NuGetAudit=false
dotnet build .\OHS.sln -c Release -p:NuGetAudit=false
```

## Smoke

完整 Smoke 默认不移动鼠标：

```powershell
.\b-Code-Studio\tests\Smoke\bin\Debug\net8.0-windows\Smoke.exe
.\b-Code-Studio\tests\Smoke\bin\Release\net8.0-windows\Smoke.exe
```

停靠真实输入验收必须在可交互 Windows 桌面显式启用。它只创建标题为
`OneHistoryStudio Docking Real-Mouse Smoke` 的隔离窗口，使用真实鼠标完成工具标签拖出、嵌入中央主窗口、
从中央再次拖出并拖回右侧工具区，结束时释放按键、关闭测试窗口并恢复原鼠标位置；不读取或修改正式 OHS 布局：

```powershell
.\b-Code-Studio\tests\Smoke\bin\Debug\net8.0-windows\Smoke.exe --suite Docking --real-mouse
.\b-Code-Studio\tests\Smoke\bin\Release\net8.0-windows\Smoke.exe --suite Docking --real-mouse
```

## OHS 发布入口

发布统一使用 `eng\Publish-Studio.ps1`。默认模式会执行锁定还原、Debug/Release 构建、两套 Smoke、
Release 发布、运行时命令手册重生成、六份 Help 校验、版本校验、SHA-256 和 manifest，并写入新的
`b-Publish` 暂存区；每次使用同卷临时候选原子替换，不提交 Git。只有显式 `-Publish` 且
`b-Code-Studio`、`b-Code-Studio.Service`、`b-Office` 都洁净时，才会继续把已验证暂存原子提升到
`z-Package` 正式区。两个阶段都按 manifest 和 checksum 复验完整文件集合、大小与 SHA-256；旧暂存、
旧正式包及失败候选进入 `stage` 作为回滚或故障隔离。

```powershell
.\b-Code-Studio\eng\Publish-Studio.ps1 -Version 2.7.6
.\b-Code-Studio\eng\Publish-Studio.ps1 -Version 2.7.6 -Publish
```

不得用独立的 `dotnet publish` 或手工复制替代该入口；版本始终从 `StudioVersion.props` 求值。

部署只消费 `z-Package`，目标固定为 `C:\OneHistory\OneHistory-Push\OneHistoryStudio`。默认仅预览并
验证正式包；`-Apply` 才执行数据库备份和整目录事务替换。脚本不会启动、停止或重启程序，也不修改自启动：

```powershell
.\b-Code-Studio\eng\Deploy-Studio.ps1
.\b-Code-Studio\eng\Deploy-Studio.ps1 -Apply
```

本仓不包含 AppShell 源码、包仓或开发文档副本；框架契约以 023 的 3.0 权威文档为准。

产品行为、命令、MCP 工具形态和页面布局以当前源码、运行时和
[现行手册](../b-Office/README.md) 为准；构建、Smoke、GUI、发布与部署门见
[发布与升级](../b-Office/meta/发布与升级.md)。已交付版本文档不得作为开发前置资料。
