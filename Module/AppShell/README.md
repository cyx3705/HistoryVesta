# AppShell V4 模块实验区

这里是 AppShell V4 的临时源码和工程区。当前版本为 `4.0.0-exploration`，工程入口是 `AppShell.Desktop.csproj`。

## V4 边界

AppShell V4 的内核只保留桌面 Shell 前端：

- WPF 主窗口和桌面视觉基底；
- 页面目录、页面选择和页面承载；
- 页面拖入主工作区的前端交互；
- 给上层页面模块使用的 `DesktopPageDefinition` 和 `RegisterPage` 入口。

AppShell V4 不包含也不引用：

- `CommandBus`、`ICommandDispatcher` 或任何命令管线；
- MyAPI 后端项目、HTTP、MCP 或 ServiceHost；
- 前后端分离适配层、业务能力和 OHS 数据模型；
- AppShell 3.0.3 的 Core、Services 或冻结包。

OHS 页面和其他前端模块以页面定义接入 Shell。后端能力属于其他模块或独立宿主，不由这个桌面 Shell 提供。

## 本地验证

```powershell
dotnet build .\Module\AppShell\AppShell.Desktop.csproj -c Release
dotnet run --project .\Module\AppShell\AppShell.Desktop.csproj -c Debug
```

当前迁移只建立可运行的 Shell 前端基线，不宣称与 AppShell 3.0.3 功能等价，也不进入正式发布包。AppShell 3.0.3 仍由 `2026-023-AppShell` 独立维护并保持冻结。
