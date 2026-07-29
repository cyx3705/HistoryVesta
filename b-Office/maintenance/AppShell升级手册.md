# AppShell 升级手册

> 适用范围：AppShell 3.0.x 及从旧源码副本、0.7.x、2.7.x 迁移到 3.0.x 的消费应用。
> 当前模式：AppShell 在 023 独立维护，消费方只使用固定版本 NuGet 包，不复制框架源码，不跨项目 `ProjectReference`。

## 1. 升级模型

旧模式把 AppShell 源码复制进应用，再从派生应用回灌框架。这个模式已经停止。当前只有一份框架源码和一条版本线：

```text
2026-023-AppShell 源码
  -> staging 审核候选
  -> 不可覆盖的正式 feed
  -> 消费方固定 PackageReference
```

消费方可以新增自己的命令、窗口、面板、模块和服务组合，但不得修改 NuGet 缓存中的 AppShell 文件，也不得把包源码复制回应用仓库。发现框架缺陷时，在 023 登记复现和影响面，由 AppShell 发补丁版本。

## 2. 选择包

| 使用场景 | 直接引用 | 传递依赖 |
|---|---|---|
| WPF 桌面应用 | `OneHistory.AppShell.Shell` | Core、Services |
| 无窗口服务宿主 | `OneHistory.AppShell.ServiceHost` | Core、Services |
| 桌面 + 独立服务进程 | Shell、ServiceHost | Core、Services |
| 只使用契约或命令模型 | `OneHistory.AppShell.Core` | 无 |
| 只使用文件、模块、MCP/Web 服务 | `OneHistory.AppShell.Services` | Core |

四包必须使用同一版本。不要在项目中同时直接声明不同版本的 Core、Services、Shell 或 ServiceHost。

```xml
<ItemGroup>
  <PackageReference Include="OneHistory.AppShell.Shell" Version="3.0.0" />
  <PackageReference Include="OneHistory.AppShell.ServiceHost" Version="3.0.0" />
</ItemGroup>
```

## 3. 包源

审核候选与正式版本是两个不同来源：

- 审核阶段：`b-Publish/staging/<版本>/packages`，可覆盖，只用于候选验证。
- 审核通过后：`z-Package-AppShell/feed`，只保存当前正式版本，是消费方长期引用源；完整不可覆盖归档位于 `b-Publish`。
- `nuget.org`：第三方依赖源；AppShell 自有包不发布到 NuGet.org。

消费方正式 `nuget.config` 只应指向正式 feed。验证未发布候选时使用临时配置或显式 `--source`，验证后删除临时配置，避免把 staging 误当正式源。

## 4. 标准升级流程

### 4.1 升级前

1. 阅读目标版本的 changelog、已知限制和本手册。
2. 记录当前 AppShell 版本、消费项目 HEAD、构建命令和关键业务回归结果。
3. 确认所有 AppShell `PackageReference` 使用同一版本，且没有残留 AppShell `ProjectReference` 或源码副本。
4. 对生产应用保留当前锁文件和可回滚包；不要覆盖正式 feed 中的旧版本。

### 4.2 候选验证

```powershell
dotnet restore .\YourApp.sln --force-evaluate --source <staging-packages> --source https://api.nuget.org/v3/index.json
dotnet build .\YourApp.sln -c Debug --no-restore
dotnet build .\YourApp.sln -c Release --no-restore
dotnet test .\YourApp.sln -c Release --no-build --no-restore
```

随后运行消费应用自己的 Smoke、GUI、确定性或协议回归。仅“编译通过”不等于升级完成；窗口布局、命令目录、MCP/Web、模块热重载和关闭释放必须按实际使用能力验证。

### 4.3 正式切换

1. 等待 AppShell 审核通过并提升到正式 feed。
2. 把消费项目版本改为正式版本，从正式 feed 执行一次 `--force-evaluate` 全新还原。
3. 更新并审查 `packages.lock.json`，确认四包版本一致且没有意外依赖漂移。
4. 重跑候选阶段的完整回归后，才提交消费方升级。

## 5. 从源码副本迁移

旧应用若包含 `AppShell.Core`、`AppShell.Services`、`AppShell.Shell` 或 `AppShell.ServiceHost` 源码副本，按以下顺序迁移：

1. 先加入固定版本 `PackageReference`，不要先删源码。
2. 从解决方案移除 AppShell 工程节点，并移除指向它们的 `ProjectReference`。
3. 构建消费应用，处理真正的公共 API 差异；禁止为了过编译重新引入副本。
4. 完整回归通过后再删除应用仓库里的 AppShell 源码、框架发布脚本和重复手册。
5. 消费应用默认链接 `z-Package-AppShell/AppShell.reuse.md`；需要完整 API、命令、模块、MCP 或运行时约束时，继续读取同一快照中的 `z-Package-AppShell/docs/`。只有查阅历史版本时才使用 `b-Publish/docs/<版本>`；`b-Office/package` 是 023 内部编辑源，不是消费方的运行时文档副本。

删除顺序不可颠倒。先删源码会失去可工作的对照基线，并把包迁移问题和文件缺失混在一起。

## 6. 升级到 3.0.0 的破坏性变化

### 6.1 数据库与表窗口删除

3.0.0 不再提供 `IDataService`、SQLite/远端数据服务、通用表窗口、`ShellConfig.DataService` 或 `db.*`。消费方需要数据能力时，应在自己的模块或工具窗口中实现，并通过业务命令暴露。

迁移时删除：

- `IDataService` 构造参数和兼容重载；
- `QueryResult`、`ColumnInfo` 等只为旧表窗口服务的反序列化分支；
- `db.*` 注册、按钮、脚本和命令白名单；
- 对 `TableView` 或 `SqliteDataService` 的直接引用。

### 6.2 中央业务区

命令集是 AppShell 3.0 的默认中央主窗口。`DockSide.Center` 在 Shell 内部由原生文档主区承载，不是一个
并排的工具窗格。中央区始终显示自己的页面头和页面选择标签；消费方注册第二个 `DockSide.Center` 窗口后，
两者组成同一中央文档标签组，`Show` 可稳定切换当前页。命令集不能隐藏、浮动或停靠到四边；业务中央文档隐藏、浮动或卸载后，命令集仍
留在主区，不会重新暴露空白裂痕。

原本注册在上、下、左、右的普通工具页可以由用户拖到中央页面选择区，成为中央标签页；也可以从中央再次
拖出或停回四边。嵌入不会把工具页永久改成另一套页面类型，原 `ToolWindowDescriptor`、owner 回收和内容实例
继续有效。`win.dock name=<id> pos=center` 可重放同一结果，布局保存与重启恢复会保留中央嵌入状态。

中央业务窗口必须显式注册为 `DockSide.Center`。不要继续依赖“Top/Bottom 恰好落在中央列”的实现细节。

```csharp
config.ToolWindows.Add(new ToolWindowDescriptor
{
    Id = "workspace",
    Title = "工作区",
    DefaultSide = DockSide.Center,
    DefaultRatio = 1,
    ContentFactory = () => new System.Windows.Controls.TextBlock { Text = "工作区" },
});
```

旧布局中的命令集若保存在顶部、右侧、普通标签组，或保存在 3.0 早期候选的“0.01px 空文档区 + 并排中央
工具窗”拓扑，首次恢复时都会迁入原生文档主区，无需删除用户布局。`DockSide` 数值已固定为
`Left=0`、`Right=1`、`Top=2`、`Bottom=3`、`Tab=4`、
`Center=5`；旧模块中的 `Tab=4` 不会被解释成 `Center`。

### 6.3 命令目录与前端代理

服务端权威目录由最终 `CommandRegistry` 生成。Shell 前端连接 Web 服务后自动发布命令能力，服务端动态建立 `ExecutionSite=Frontend` 代理。消费方应删除静态前端命令名清单。

纯 UI 命令默认不允许 MCP 执行。确需开放时，描述符必须显式设置 `AllowMcpExecution=true`，并继续经过在线前端选择和确认策略。

### 6.4 MCP/Web 端口

默认端口按应用名稳定派生，冲突时有界顺延。消费方不应假设所有应用都监听固定的 8737/8938；需要固定端口时使用 `mcp.port`、`web.port` 配置，并读取网关实际 `Port`。

### 6.5 文件治理

MCP 审计和提示词治理使用应用数据目录中的文件，不依赖数据库。不要再为启用 MCP 注入数据服务。

## 7. 兼容性规则

- `3.0.x`：只接受兼容性补丁；已 shipped public API 不删除、不改名、不改签名。
- 新增可选 API：可以进入后续 3.x，但必须有默认行为并更新 API 基线、XML 文档和本手册。
- 公共契约或协议的破坏性变化：进入 4.0，并提供迁移文档和至少一个消费方试点。
- 命令名、参数名、默认值、危险性、`ExecutionSite` 和 MCP 暴露语义都属于兼容性合同，不只是 UI 文案。

升级评审应对照包内 XML API 文档和四个 `PublicAPI.Shipped.txt`；不要以实现类的 `internal` 成员或源码路径作为消费合同。

## 8. 回归矩阵

| 使用的能力 | 最低回归 |
|---|---|
| 命令总线 | 注册、解析、校验、确认、执行、取消、异常结果、Help |
| WPF Shell | 启动、关闭、四边工具窗操作、中央主文档、标签显隐、旧布局迁移与保存恢复 |
| Workspace | 根目录边界、列举、新建、重命名、回收站删除 |
| 面板 | JSON 加载、输入值绑定、按钮生成命令、reload |
| 模块 | 好/坏模块隔离、命令注册、UI owner 回收、热重载后 DLL 可删除 |
| MCP | tools/list、tools/call、策略、确认、审计、Stop 后端口释放 |
| Web/前端 | 目录发布、单前端、多前端歧义、定向、离线失败、鉴权与限流 |
| ServiceHost | 单实例、启动、停止、重启、登录自启、Dispose |

## 9. 回滚

回滚只改消费方包版本和锁文件，不覆盖或删除正式 feed 中的任何版本：

1. 将所有 AppShell 包统一改回上一正式版本。
2. 从正式 feed 强制还原，确认锁文件只回退预期包。
3. 恢复与该版本匹配的消费方代码和配置；3.0.0 删除的数据库接口不能靠单独降一个包恢复。
4. 重跑与升级时相同的回归矩阵。

布局、设置和模块目录属于用户状态。跨主版本回滚前先备份 `%AppData%/<应用名>`，不要用删除用户目录代替迁移。

## 10. 禁止事项

- 不复制 AppShell 源码作为新应用底座。
- 不跨仓库 `ProjectReference` 到 023。
- 不修改全局 NuGet 缓存或解压后的包内容。
- 不混用不同版本的四个 AppShell 包。
- 不把 staging 路径长期写入正式 `nuget.config`。
- 不在消费方维护 AppShell 命令、前端代理或文档的第二份名单。
- 不用“删除布局/用户数据后能启动”代替兼容性修复。

API 与基础命令的接入方式见同一版本归档中的 `AppShell_API与指令手册.md`。
