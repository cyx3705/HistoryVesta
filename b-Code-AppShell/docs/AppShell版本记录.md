# AppShell 版本记录

> V2.4.0 起，`b-Code-AppShell` 是 OHS 伞形项目中的框架唯一源码。
> 0.1 至 0.4.4 保留下表历史；后续框架变更直接发生在本组件，不再复制、跟随或回灌源码副本。
> 每次框架变更必须通过 AppShell 演示宿主、OHS Smoke 与 GUI 实跑。

| 版本 | 日期 | 里程碑 | 说明 |
|---|---|---|---|
| 0.5.0 | 2026-07-26 | 首次固定版本包 | 建立 `OneHistory.AppShell.Core/Services/Shell` 三层 NuGet 包与 win-x64 framework-dependent 演示 ZIP；统一 0.5.0 版本真值、中央依赖与锁文件，补齐 SourceLink、XML 文档、符号包、package validation、隔离 PackageSmoke、漏洞审计、manifest 和 SHA-256 发布链。`Microsoft.Data.Sqlite` 升至 8.0.29，并以 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4 清除旧 `lib.e_sqlite3` 2.x High 公告链；AvalonDock 保持 4.72.1。命令手册与公开 MCP 示例完成通用化，独立宿主按能力隐藏 OHS `tool.*` 按钮；设置/日志/MCP 持久化改用 invariant 格式。收录已验收的 MCP `structuredContent` 加法兼容实现，旧 `content` 保持。AppShell Debug/Release 0 警告 0 错误，隔离包消费与演示 GUI 通过，OHS Debug/Release 与六套 Smoke 全绿。0.5.0 仅发布仓库内本地 feed；OHS 继续 `ProjectReference`，未推送 NuGet.org、未创建 tag |
| 0.4.4 | 2026-07-24 | 派生反哺(大) | OneHistoryStudio V2.4 独立化前的最后一次反哺:把 MCP 服务、模块注册器、提示词治理三大件上抛为框架自带能力(默认启用,ShellConfig 可关),从此**派生应用一建立即有前端 + MCP 服务 + 模块注册器**。① 新增 `Core\Mcp`(McpExposurePolicy/CommandSchemaExporter/CommandManualGenerator/McpConfirmationScope/PromptTextIntegrity/**IMcpAuditLog** 契约)、`Core\Data\SqlText`、`Core\AppIdentity`(取入口程序集,可 `Use()` 覆盖);② 新增 `Services\Mcp`(McpGateway/PromptGovernanceStore/**McpAuditRecorder** 缺省留痕)、`Services\Modules`(ModuleHost/ModulePanelSync);③ 新增 `Shell\Mcp`(McpCommands/CommandCatalogCommands/PromptGovernanceCommands/RemoteConfirmDialog)、`Shell\Modules`(ModuleCommands),由 ShellWindow 在内置指令后、派生指令前自动装配并自管生命周期。**解耦改造**:McpGateway 原依赖派生侧 HistoryRecorder,抽 `IMcpAuditLog` 接口断开;ModuleHost 原直取 `Application.Current.Dispatcher`,改注入 `SynchronizationContext`(§14.2 分层),Services 层因此零 WPF 依赖;McpExposurePolicy 只读白名单清空 14 条 OneHistory 专有指令,改 `RegisterReadonly()` 由派生登记(框架基线只含框架自注册指令)。**同批并入体积治理**:新增 `Directory.Build.props` 钉死 win-x64 RID,单份输出约 27M→3.3M。模板独立构建(含自带 App 演示)0 警告 0 错误;派生侧 OneHistoryStudio 换用后主解决方案 Debug/Release 0 警告、五套冒烟 10/10 PASS、三目录逐文件哈希 0 差异 |
| 0.4.3 | 2026-07-16 | 派生反哺 | OneHistoryStudio V2.1.2~V2.1.5 期间产生、V2.1.6 质量整备(Q16-M1)审阅后整体回灌五文件:①CommandRegistry——Register 增 source 溯源(默认 "framework",兼容旧调用)+ GetSource + Changed 事件,Unregister 同步清理并触发;②CommandParser——指令名放宽为多段(域.动作.子动作…);③CommandBus——ExecuteAsync 增可选 CancellationToken,取消返回「指令已取消」;④DockingHost——首建布局默认比例种子保护 + 比例施加覆盖停靠组全部成员(比例语义修正);⑤BuiltinCommands——help 按域分组计数/列宽 24/详情补参数类型与安全·线程提示。逐条审阅确认均为通用能力,无派生应用专有逻辑;模板独立构建 0 警告 0 错误 |
| 0.4.2 | 2026-07-15 | 派生反哺 | 由首个派生应用 OneHistoryStudio(V2-M2/M3)回灌两处修正:①控制台多行日志逐行拆分入列表 + 滚动单位 Item→Pixel,修复「底部长日志显示不全」(ConsoleRow/ConsoleView,谨慎区,验收 8 于派生侧复跑通过);②CommandRegistry 新增 Unregister(name)——模块热重载场景下线指令域所需,调用方只应注销自己注册过的名称,Register 冲突即抛的规则不变(§5.3) |
| 0.4.1-M4 | 2026-07-12 | M4 修补 | 修复 x64 回收站删除闪退(SHFILEOPSTRUCT 误用 Pack=1,详见下方 M4 要点);资源窗口新增“打开文件夹…”与“恢复默认工作区”(res.root 的图形入口) |
| 0.4.0-M4 | 2026-07-12 | M4 | 控制窗口群(JSON 面板)+ 资源窗口 + res.*/panel.* 指令组。验收 5 / 6 达成;R-06 越界拒绝实测。二次开发权限规则见 docs/二次开发演进手册.md |
| 0.3.0-M3 | 2026-07-12 | M3 | SQLite 数据服务 + 表窗口 + db.* 指令组。验收 4 / 9 达成(UI 单元格编辑手势留人工复验),N-04 十万行深页 13ms |
| 0.2.0-M2 | 2026-07-12 | M2 | 指令核心(解析/注册表/总线)+ 控制台窗口 + 正式日志服务。验收 7 / 8 达成,win.*、layout.* 可用 |
| 0.1.0-M1 | 2026-07-12 | M1 | 停靠二次封装 + 主窗口占位页 + 布局持久化。验收 1 / 2 / 3 / 10 达成(3 的多显示器混合 DPI 场景待人工复验) |

## 技术基线

- .NET 8(net8.0-windows)+ WPF + Dirkster.AvalonDock 4.72.1 + VS2013 Light 主题
- .NET 10 迁移预案见需求文档 §14.1

## 本机构建注意事项

1. **CET 兼容**:本开发机(Windows 10 LTSC 2021)对 CET 支持不全,.NET 10 SDK 自带的
   Roslyn 编译器进程会以 "Your Windows doesn't fully support CET" 崩溃。
   用 dotnet CLI 构建前需设置环境变量:`DOTNET_EnableWriteXorExecute=0`
   (仅影响编译器进程启动;可写入用户环境变量一劳永逸)。
   Visual Studio 内置 MSBuild 不受影响。
2. **NuGet 源**:机器全局配置里的 `https://nuget.cdn.azure.cn` 镜像已停服,
   本解决方案根目录的 `nuget.config` 已改为仅使用 nuget.org。

## M4 要点(维护者须知)

- **控制窗口群**:PanelManager 从 `<数据目录>/panels/*.json` + ShellConfig.Panels(C# 通道)
  加载 PanelDefinition,每个面板注册为独立可停靠窗口(窗口名 = 面板 id,win.*/layout.* 直接可用)。
  按钮点击 = 收集控件值 → `{控件id}` 填入指令模板 → 总线执行(验收 6)。
  八类控件见 Core/Panels/PanelControl;panel.set 反向驱动;panel.reload 原地重建(新增面板需重启)。
- **资源窗口**:懒加载单树;全部写操作生成 res.* 指令经总线;边界校验与回收站语义统一在
  Services/WorkspaceService(`res.mkdir path=..\x` 会被拒,已实测);FileSystemWatcher 500ms
  去抖自动刷新;双击打开可被 ShellConfig.OnResourceOpen 接管(R-03)。
- res.delete / db.update / db.delete(无 where)共用总线 ConfirmPrompt 拦截器 —— 危险操作单闸口。
- **x64 P/Invoke 教训(0.4.1 修复)**:SHFILEOPSTRUCT 绝不能带 `Pack = 1`(网上流传的 32 位写法)。
  x64 下会字段错位 → Shell 回写越界 → 栈损坏闪退,且 AccessViolation 无法被 .NET 捕获,
  总线的异常兜底(N-05)拦不住。新增任何 P/Invoke 结构体都要核对 64 位布局。
- **二次开发注意**:冻结区 / 谨慎区 / 自由区与上报规则见 `docs/二次开发演进手册.md`,
  接手前必读。

## M3 要点(维护者须知)

- **数据抽象在 Core/Data/IDataService**(D-02 提供者接入位),SQLite 实现在
  Services/SqliteDataService(Microsoft.Data.Sqlite 8.0.10)。库文件在 data/ 下,
  连接经 `RegisterConnection(name, file)` 注册,缺省连接名 main。
- **行定位用 rowid**:查询固定 `SELECT rowid AS __rowid__, *`,表窗口据此拼
  `where="rowid=N"` 做编辑/删除,无主键表同样可编辑;WITHOUT ROWID 表自动退化只读。
- **表窗口是 db.* 的图形外壳**(验收 4 的机制):一切用户动作(翻页/筛选/编辑/删除/导出)
  都生成指令文本经总线执行;数据变更再经 `IDataService.DataChanged` 事件驱动表窗口
  自动重载(200ms 去抖)。手输指令与 UI 操作因此天然双向同步。
- **危险操作单闸口**(T-08/验收 9):无 where 的 db.update/db.delete 由总线 ConfirmPrompt
  拦截,UI 路径(“按筛选删除”空筛选)与手输路径走同一拦截器;“删除选中行”另有 UI 侧确认。
- **where/order/set 是原始 SQL 片段**(§5.1 语义),不做注入防护(面向使用者自己的库);
  表名过 sqlite_master 存在性校验。
- 演示数据:首启建 users 表(1200 行);`debug.seedbench rows=100000` 生成 bench 大表
  (N-04 实测:十万行第 180 页查询 13ms)。
- **已知限制**:多实例并发时第二实例抢不到日志文件(静默丢文件日志,内存/控制台不受影响);
  单进程单实例开关为 N-07(P2)。

## M2 要点(维护者须知)

- **指令核心在 AppShell.Core/Commands**(零 WPF 依赖):CommandParser(§5.1 语法,引号/转义/注释)、
  CommandRegistry(注册冲突即抛,未知指令给相近候选)、CommandBus(校验 → 二次确认拦截 →
  执行 → 回显;异常全捕获不崩溃;RequiresUiThread 的指令经 SynchronizationContext 编组)。
- **回显与日志共用 IShellLog 管道**(L-03):回显类别 `cmd:<来源>`,结果 `cmd:result`,
  进度 `cmd:progress`;控制台按类别前缀渲染成附录 C 样式。布局手势指令(W-10)也走
  `cmd:layout` 类别,可在控制台一键屏蔽。
- **控制台承压路径**(N-03/验收 8):日志事件 → 并发队列 → 100ms 批量刷入
  RingCollection(追加发单条 Add,裁剪发 Reset,头部偏移摊销 O(1));实测 3000 条/秒
  持续 20 秒(60k 条,击穿 50k 上限走裁剪路径)UI 不冻结,文件零丢失。
- **控制台窗口内容由 Shell 接管**:描述符 Id="console" 可不设 ContentFactory,位置仍由
  派生应用声明;未声明时 Shell 强制注册(架构不变量 2)。
- **菜单项点击 = 发指令**(S-02):ShellWindow 菜单一律 `bus.ExecuteAsync(text, "UI")`。
- **启动参数 `--exec "<指令>"`** 可重复,启动后按序执行(来源 脚本:startup),自动化/自测入口。
- **派生应用注册指令**:`ShellConfig.ConfigureCommands = registry => registry.Register(...)`,
  示例见 App 的 debug.logflood(异步长任务 + Progress 上报范式)。
- 手动输入交互(↑/↓ 历史、Tab 补全、多行粘贴拆分、Ctrl+` 聚焦)属 UI 键盘路径,
  无法无头自动化,发布前人工过一遍。

## M1 封装要点(维护者须知)

- **AppShell.Shell 是唯一引用 AvalonDock 的项目**(§14.2 封装原则),派生应用只面向
  `AppShell.Core.Docking.IDockingService` 与 `ToolWindowDescriptor`。
- **标签条置顶(W-04)**:AvalonDock 窗格样式经 `DockingManager.AnchorablePaneControlStyle`
  属性下发(主题字典中的隐式 Style 不会命中)。ShellWindow 以主题键
  `AvalonDockThemeVs2013AnchorablePaneControlStyle` 取基底样式做 BasedOn,仅替换模板
  (标签行移至顶部)。**升级 v5 时该资源键会变,需同步调整。**
- **比例语义(W-05)**:AvalonDock 对"与文档区同面板的侧窗格"是像素语义
  (`LayoutPanelControl.OnFixChildrenDockLengths` 会把星值固化为像素,窗格未排布时
  会固化成最小值 25px)。DockingHost 维护每窗口目标比例,在首次排布后与主窗体缩放后
  (SizeChanged 去抖 200ms)按比例重新施加像素尺寸;恢复布局后首次改为反向采集比例。
- **布局手势 → 指令(W-10)**:监听 LayoutRoot.Updated,去抖 500ms 后对全部窗口状态
  做快照差分,输出 win.show/hide/float/dock/ratio 等价指令;程序化变更经 Suppress()
  抑制并重建基线,防再入回声。
- **布局损坏回退(N-06)**:反序列化异常 → 删除损坏文件 → 构建默认布局 → Warn 告警
  (占位页横幅可见,M2 起进控制台)。
