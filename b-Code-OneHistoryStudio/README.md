# OneHistoryStudio

本目录只承载 OneHistoryStudio 产品代码、测试、样例和发布快照。跨组件文档位于
`..\b-Office\OHS`，AppShell 唯一源码位于 `..\b-Code-AppShell`。

## 结构

- `src/App`：OHS 装配点、Git 项目管理、页面与产品配置。
- `tests/Smoke`：合并后的完整自动化冒烟套件。
- `samples`：模块开发样例。
- `Publish`：当前正式发布快照。

## 构建

从 020 根目录执行：

```powershell
dotnet build .\OHS.sln -c Debug -p:NuGetAudit=false
dotnet build .\OHS.sln -c Release -p:NuGetAudit=false
```

本目录不再包含 `AppShell.Core/Services/Shell` 副本。
