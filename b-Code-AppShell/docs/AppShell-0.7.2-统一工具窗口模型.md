# AppShell 0.7.2：统一工具窗口模型

> 日期：2026-07-27
> 基线：AppShell 0.7.1
> 状态：代码已实施，自动化验收通过，尚未发布或正式部署

## 1. 决策

AppShell 只保留一种模块界面对象：**工具窗口**。0.7.0 引入的工作页、固定主内容和中央首页全部撤销。

中央区域继续保留一个空的 AvalonDock `LayoutDocumentPane`，仅用作背景和四边停靠布局的中心参照。宿主不在其中创建 `LayoutDocument`，因此不会出现主窗口标签、工作页标签或不可拖动的固定页面。

选择统一模型的原因：

- 现有工作页没有文件目录、多实例、编辑历史、脏状态或关闭确认等独立文档语义。
- `page.open` 实际只能激活模块启动时已经创建的页面，不具备真正的“打开文档”能力。
- 0.7.1 已让工具窗口支持拖动、浮动、重新停靠、隐藏和双击标题条最大化，覆盖当前模块的全部交互需求。
- 两套高度重复的注册、owner 回收、布局恢复、命令和测试分支会增加模块接入成本和故障面。

若未来出现真正的文档编辑器，应以带文档身份、保存状态和多实例生命周期的新契约重新设计，而不是恢复本次删除的轻量工作页别名。

## 2. 契约变化

模块 UI 唯一入口：

```csharp
ShellUi.RegisterToolWindow(new ToolWindowDescriptor
{
    Id = "example",
    Title = "示例窗口",
    DefaultSide = DockSide.Right,
    DefaultRatio = 0.4,
    ContentFactory = static () => new ExampleView(),
}, "ExampleModule");
```

删除以下公开类型与成员：

- `WorkPageDescriptor`、`WorkPageInfo`
- `IShellUiRegistrar.OpenWorkPage/CloseWorkPage/ActivateWorkPage/ListWorkPages`
- `IDockingService` 中全部工作页成员
- `ShellConfig.MainContent`

删除以下命令，不保留兼容别名：

- `page.list`
- `page.open`
- `page.close`
- `page.activate`

统一使用：`win.list/show/hide/float/dock/ratio/reset/max/restore` 与 `layout.*`。

## 3. 运行与布局语义

- 工具窗口关闭按钮仍表示隐藏，不销毁模块内容；`win.show` 可再次显示。
- 工具窗口均可拖出为普通浮动窗口，并可重新停靠。
- 双击窗口自身带点的蓝色标题拖动条执行最大化/恢复；不在标签上绑定双击。
- 模块卸载或热重载时，owner 兜底注销该模块的全部工具窗口并释放可释放内容。
- 最大化期间注销目标窗口时，宿主先恢复完整布局，再移除窗口。
- 0.7.1 及更早布局中的全部 `LayoutDocument` 在反序列化时丢弃；仍注册的工具窗口尽量按原布局恢复。
- 布局缺少空中央背景区时视为不兼容布局，回退默认布局。

## 4. 迁移

旧模块：

```csharp
_page = ShellUi.OpenWorkPage(new WorkPageDescriptor { ... }, owner);
```

改为：

```csharp
_window = ShellUi.RegisterToolWindow(new ToolWindowDescriptor { ... }, owner);
```

同时把 `page.open id=x`、`page.close id=x` 改为 `win.show name=x`、`win.hide name=x`。原 `CanFloat` 不再需要，工具窗口统一允许浮动；原 `CanClose=false` 对应工具窗口的“关闭即隐藏”语义。

本次删除公开类型和成员，属于源码及二进制破坏性修订。所有引用 AppShell 0.7.1 UI 契约的模块必须针对 0.7.2 重新编译，不能只替换宿主 DLL。

## 5. SE2SW 迁移

SE2SW 2.2.0 是本次真实迁移载体：窗口 ID 保持 `se2sw`，改用 `RegisterToolWindow`，默认停靠在上方并占主窗体高度的 75%。转换、COM worker、OHS/外界模式和文件输出语义均不变。

迁移后可用命令：

```text
win.show name=se2sw
win.hide name=se2sw
win.float name=se2sw
win.max name=se2sw
win.restore
```

## 6. 验收

| 项目 | 判据 | 当前结果 |
|---|---|---|
| 默认布局 | 没有任何 `LayoutDocument`；中央背景 pane 为空 | PASS |
| 普通窗口 | 动态模块窗口均可浮动 | PASS |
| 最大化 | 最大化/恢复不重建内容、不产生布局命令回声 | PASS |
| 布局 | 保存/载入后仍无工作页文档，工具窗口保留 | PASS |
| 生命周期 | 注销最大化目标先恢复；owner 清理释放内容 | PASS |
| 构建 | OHS 主解决方案 Debug/Release，0 warning / 0 error | PASS |
| 模块 | SE2SW 主工程及 Smoke/UiSmoke 工程 Debug 构建 | PASS |
| 自动测试 | OHS 八套 Smoke Debug/Release；SE2SW Release Smoke | PASS |
| staging | 四包、漏洞审计、PackageSmoke、演示宿主 | PASS |
| 发布部署 | 正式 feed、正式目录部署、GUI 人工验收 | 待执行 |

## 7. 发布边界

0.7.2 staging 已完成，四个主包、四个符号包、漏洞审计、隔离 PackageSmoke 和演示宿主均通过。
正式交付仍需执行不可覆盖的正式 feed 发布；OHS 产品侧需重新生成命令手册、完成成套部署，并在真实
主程序中验证拖动、浮动、双击最大化/恢复和转换流程。
