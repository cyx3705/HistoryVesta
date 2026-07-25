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

本目录不再包含 `AppShell.Core/Services/Shell` 副本。

V2.4.1 完成源码上抛后，产品行为、106 条命令、MCP 工具形态和页面布局保持不变；完整构建、
Smoke、GUI、发布与部署证据见
[V2.4.1 骨干削薄与源码上抛](../b-Office/versions/27-V2.4.1-骨干削薄与源码上抛.md)。
