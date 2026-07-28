# OneHistoryStudio

本目录只承载 OneHistoryStudio 产品源码和测试。跨组件文档位于
`..\b-Office`，AppShell 唯一源码位于 `..\b-Code-AppShell`。

## 结构

- `Studio.csproj`：OHS 唯一产品工程与版本真值。
- `Git`、`Views`、`App.xaml*`：OHS 装配、项目管理与页面源码。
- `tests/Smoke`：合并后的完整自动化冒烟套件。

模块样例位于平级 `..\b-Code-Samples`，正式发布快照位于平级 `..\b-Publish`。

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
`OneHistoryStudio Docking Real-Mouse Smoke` 的隔离窗口，使用真实鼠标完成工具标签拖出、中央无效释放和
右侧工具区拖回，结束时释放按键、关闭测试窗口并恢复原鼠标位置；不读取或修改正式 OHS 布局：

```powershell
.\b-Code-Studio\tests\Smoke\bin\Debug\net8.0-windows\Smoke.exe --suite Docking --real-mouse
.\b-Code-Studio\tests\Smoke\bin\Release\net8.0-windows\Smoke.exe --suite Docking --real-mouse
```

## OHS 发布入口

发布统一使用 `eng\Publish-Studio.ps1`。默认模式会执行锁定还原、Debug/Release 构建、两套 Smoke、
Release 发布、运行时命令手册重生成、六份 Help 校验、版本校验、SHA-256 和 manifest，并写入新的
`stage\ohs-<version>`；脚本拒绝覆盖已有 staging。只有显式 `-Publish` 且 `b-Code-Studio`、
`b-Code-AppShell`、`b-Office` 都洁净时，才会把候选原子提升到 `b-Publish`。提升前后都按 manifest 和
checksum 复验完整文件集合、大小与 SHA-256；失败的新目录进入 `stage\b-Publish-failed-*`，原
`b-Publish` 从 `stage\b-Publish-pre-*` 自动恢复。

```powershell
.\b-Code-Studio\eng\Publish-Studio.ps1 -Version 2.7.5
.\b-Code-Studio\eng\Publish-Studio.ps1 -Version 2.7.5 -Publish
```

不得用独立的 `dotnet publish` 或手工复制替代该入口；版本始终从 `StudioVersion.props` 求值。

本目录不再包含 `AppShell.Core/Services/Shell` 副本。

产品行为、命令、MCP 工具形态和页面布局以当前源码、运行时和
[现行手册](../b-Office/README.md) 为准；构建、Smoke、GUI、发布与部署门见
[发布与升级](../b-Office/meta/发布与升级.md)。已交付版本文档不得作为开发前置资料。
