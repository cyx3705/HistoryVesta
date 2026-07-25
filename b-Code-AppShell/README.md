# AppShell

AppShell 是 OHS 伞形项目中的通用桌面应用框架组件，也是框架源码的唯一真值。

## 结构

- `src/AppShell.Core`：指令、数据、停靠、日志和存储契约。
- `src/AppShell.Services`：日志、SQLite、设置、工作区、MCP 与模块托管实现。
- `src/AppShell.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `docs`：框架需求、演进纪律和 `AppShell版本记录.md`。

## 构建

```powershell
dotnet build .\AppShell.sln -c Debug -p:NuGetAudit=false
dotnet build .\AppShell.sln -c Release -p:NuGetAudit=false
```

V2.4.0 起，OHS 直接通过 `ProjectReference` 使用本目录源码，不再维护框架副本或执行哈希回灌。
