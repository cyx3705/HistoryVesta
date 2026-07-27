# AppShell 0.7.0 · 模块内嵌窗口与工作页 —— 框架工程实施文档

> **历史说明：** 本文记录 0.7.0 当时的设计，工作页、固定首页和 `page.*` 已在
> [AppShell 0.7.2 统一工具窗口模型](AppShell-0.7.2-统一工具窗口模型.md) 中删除。新模块不得再按本文的工作页 API 接入。

> 文档性质:框架能力升级(**全部加法**,现有装配路径与派生应用零破坏)
> 基线版本:AppShell 0.6.1(会话层与协议协商)｜ 目标:**0.7.0**
> 日期:2026-07-27 ｜ 状态:**已实施并通过自动化验收**
> 交付节奏:与 OHS **V2.7.0** 同步交付,严格串行;任一侧验收未过,配对侧不得交付。
>
> **版本区间说明**:0.6.n 区间按 0.6 系列文档的约定只收「OHS 服务化演进」主题,
> 本版是**新的框架级主题**(窗口注册器对模块开放),因此占用 **0.7.0**。
>
> 派生侧驱动文档(需求场景与验收载体在派生侧,框架契约在本文):
> - [OHS 34-V2.7.0-模块内嵌窗口与中央工作页](../../b-Office/versions/34-V2.7.0-模块内嵌窗口与中央工作页.md)
>
> 相关框架文档:[版本记录](AppShell版本记录.md) · [0.6 系列](AppShell-0.6.0-服务宿主与会话层.md) ·
> [0.5.0 发布与质量整备](AppShell-0.5.0-发布与质量整备.md) · [二次开发演进手册](二次开发演进手册.md)

---

## 0. 一句话定义

把 0.6.0 交付的 `IUiModule` 从「能在 UI 线程上被创建」提升到「**能把界面放进主窗体**」:
停靠服务接受运行期注册与注销,中央文档区从一个固定页放宽为「首页 + N 个工作页」,
模块经一个新的 Core 契约拿到这两样能力。

0.6.0 给了模块 UI 生命周期,却没给它落脚的地方——模块只能 `new Window().Show()`。
本版补的就是落脚点。

---

## 1. 设计原则(铁律)

1. **加法原则**:`IUiModule` 签名一字不动;`ShellWindow` 装配顺序一行不动;
   不使用新契约的模块与派生应用行为与 0.6.1 逐条一致(验收硬判据)。
2. **模块只见 Core**:模块不接触 AvalonDock、不接触 `DockingHost`、不接触
   `Application.Current.Dispatcher`(§14.2 封装原则)。新契约全部落在 `AppShell.Core`,
   内容以 `object` 表达,与既有 `ToolWindowDescriptor.ContentFactory` 同惯例。
3. **框架兜底回收**:模块忘了注销不能成为 ALC 泄漏的理由。owner 级回收在
   ALC 卸载前无条件执行。
4. **只用不改三大机制**:比例语义、防再入、样式接管是 `DockingHost` 的冻结机制(§6),
   新增路径必须**复用**它们(`Suppress()` / `_ratios` / `PlaceAtSide`),
   不得为了实现新功能而修改它们本身。
5. **框架不认识 OHS**:SE2SW、活动坞等派生货色不进框架;框架只提供通用契约。

---

## 2. 现状契约与三处缺口(源码级复核)

### 2.1 停靠服务只接受启动期注册

```csharp
// AppShell.Shell/Docking/DockingHost.cs:62-69 —— 描述符在构造函数一次性灌入
foreach (var d in windows)
{
    if (_byId.ContainsKey(d.Id))
        throw new InvalidOperationException($"工具窗口 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");
    _descriptors.Add(d);
    _byId.Add(d.Id, d);
    _ratios[d.Id] = d.DefaultRatio;
}
```

`IDockingService` 现有 11 个成员(`ListWindows / Show / Hide / Float / Dock / SetRatio /
ResetWindow / ResetLayout / SaveLayout / LoadLayout / ListLayouts` + `CommandGenerated` 事件)
**没有 Register,也没有 Unregister**。

> 实现者数量核查:全仓库 `: IDockingService` 仅 `DockingHost` 一处。
> 因此向接口新增成员不会破坏任何实现方(见 §6 治理判定)。

### 2.2 模块拿不到框架服务

```csharp
// AppShell.Core/Modules/IUiModule.cs:4-9
public interface IUiModule
{
    void CreateUi();
    void DestroyUi();
}
```

无参、无上下文;实例化走 `Activator.CreateInstance` 无参构造
(`ModuleHost.cs:267` → `Snapshot.GetInstance` → `:612`),既无构造注入也无属性注入入口。

时序上:`_docking.Initialize()` 在 `ShellWindow.xaml.cs:130`,`_modules.Start()` 在 `:201`。
模块 `CreateUi()` **必然**发生在停靠系统定型之后——所以缺口 2.1 不补,单补 2.2 也没用。

### 2.3 中央区只有一个写死的页

```csharp
// DockingHost.cs:340-348
var mainDoc = new LayoutDocument
{
    Title = "主窗口",
    ContentId = MainContentId,   // "__main__"
    Content = _mainContent,      // ShellConfig.MainContent,构造期一次性
    CanClose = false,
    CanFloat = false,
};
var docPane = new LayoutDocumentPane(mainDoc);
```

`LayoutDocumentPane` 本身是多文档容器,框架却只放了一个不可关闭的文档,
且无任何对外的开页/关页/列页 API。

### 2.4 附带缺陷:迟到窗口的布局永远保不住

```csharp
// DockingHost.cs:451-455
else
{
    // 布局文件里有当前版本未注册的窗口 → 丢弃,不阻断加载
    e.Cancel = true;
}
```

模块窗口天然在 `Initialize()` 之后注册,每次启动都命中此分支,
用户排好的位置每次都丢。本版一并修(§4.8)。

---

## 3. 契约变更(AppShell.Core)

### 3.1 `IDockingService` 加法扩展

```csharp
// Core/Docking/IDockingService.cs —— 既有 11 个成员一字不动,以下为新增
public interface IDockingService
{
    // …既有成员…

    /// <summary>运行期注册工具窗口。owner 为回收键;Id 冲突抛异常,不静默覆盖(§5.3)。</summary>
    void RegisterWindow(ToolWindowDescriptor descriptor, string owner);

    /// <summary>运行期注销工具窗口;未注册的 id 静默忽略(幂等,回收路径要能重复调)。</summary>
    void UnregisterWindow(string id);

    /// <summary>在中央文档区打开工作页;同 id 已存在则激活。</summary>
    void OpenWorkPage(WorkPageDescriptor page, string owner);

    /// <summary>关闭工作页(销毁,非隐藏);未打开的 id 静默忽略。</summary>
    void CloseWorkPage(string id);

    /// <summary>仅激活已打开的工作页;未打开返回 false。</summary>
    bool ActivateWorkPage(string id);

    IReadOnlyList<WorkPageInfo> ListWorkPages();

    /// <summary>回收 owner 名下全部工具窗口与工作页(热重载兜底)。</summary>
    void UnregisterOwner(string owner);

    /// <summary>窗口或工作页集合发生变化(注册/注销/开页/关页)。视图菜单据此重建。</summary>
    event EventHandler? WindowsChanged;
}
```

### 3.2 工作页模型

```csharp
// Core/Docking/WorkPageDescriptor.cs —— 新文件
namespace AppShell.Core.Docking;

/// <summary>
/// 中央文档区的一页。与 ToolWindowDescriptor 的区别:
/// 工具窗口是常驻面板(关闭=隐藏),工作页是任务视图(关闭=销毁)。
/// </summary>
public sealed class WorkPageDescriptor
{
    /// <summary>page.* 的寻址名;小写、进程内唯一,与工具窗口共享同一命名空间。</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>内容工厂。返回值在 WPF 宿主中应为 FrameworkElement;
    /// 声明为 object 以保持 Core 层不依赖 WPF。</summary>
    public required Func<object> ContentFactory { get; init; }

    /// <summary>false 时不显示关闭按钮(框架首页用)。</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>是否允许拖出为普通浮动窗口并重新停靠；模块工作页默认允许。</summary>
    public bool CanFloat { get; init; } = true;
}

public sealed record WorkPageInfo(string Id, string Title, bool IsActive, string Owner);
```

### 3.3 模块 UI 注册器

```csharp
// Core/Modules/IShellUiRegistrar.cs —— 新文件
namespace AppShell.Core.Modules;

using AppShell.Core.Docking;

/// <summary>
/// 模块向宿主注册界面的唯一门面。
/// 线程契约:**实现方自行编组到 UI 线程**,模块可在任意线程调用;
/// 需要连续多步操作时用 <see cref="Invoke"/> 一次性编组以避免闪烁。
/// </summary>
public interface IShellUiRegistrar
{
    bool IsUiThread { get; }

    /// <summary>把 action 编组到宿主 UI 线程同步执行。</summary>
    void Invoke(Action action);

    /// <summary>注册工具窗口;返回值 Dispose 等价于注销。</summary>
    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    void UnregisterToolWindow(string id);

    /// <summary>打开工作页;返回值 Dispose 等价于关闭。同 id 已开则激活并返回既有句柄。</summary>
    IDisposable OpenWorkPage(WorkPageDescriptor page, string owner);

    void CloseWorkPage(string id);

    bool ActivateWorkPage(string id);

    IReadOnlyList<WorkPageInfo> ListWorkPages();

    void UnregisterOwner(string owner);
}
```

### 3.4 模块感知接口

```csharp
// Core/Modules/IUiModule.cs —— 追加到既有文件,IUiModule 本身不动
/// <summary>
/// 模块若实现本接口,ModuleHost 在实例化后、CreateUi 之前注入注册器。
/// 选属性注入而非构造注入:ModuleHost 用无参构造反射实例化(ModuleHost.cs:612),
/// 改构造签名会波及全部既有模块。
/// </summary>
public interface IShellUiAware
{
    IShellUiRegistrar ShellUi { set; }
}
```

### 3.5 `ToolWindowInfo` 增 owner 列

```csharp
// Core/Docking/IDockingService.cs
public sealed record ToolWindowInfo(
    string Id, string Title, bool IsVisible, bool IsFloating,
    DockSide? Side, double? Ratio,
    string Owner = "framework");   // 新增,带默认值
```

带默认值的位置参数:**源码兼容,二进制不兼容**。四包同版本齐发(0.5.0 建立的发布链),
派生应用重编译即可,可接受。若交付期发现有二进制消费者,改走旁路
`IReadOnlyList<(string Id, string Owner)> ListWindowOwners()`,不要临时改记录。

---

## 4. Shell 层实现

### 4.1 `DockingHost` 运行期注册

集合字段从只读变可变(`_descriptors / _byId / _ratios` 已是可变集合,只是没有写入口)。

```csharp
public void RegisterWindow(ToolWindowDescriptor d, string owner)
{
    if (_byId.ContainsKey(d.Id) || _pages.ContainsKey(d.Id))
        throw new InvalidOperationException($"窗口/工作页 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");

    using (Suppress())                       // 铁律 4:注册动作不得回声成 win.dock
    {
        _descriptors.Add(d);
        _byId.Add(d.Id, d);
        _owners[d.Id] = owner;

        var a = CreateAnchorable(d);
        var placement = TakeOrphanPlacement(d.Id);      // §4.8
        PlaceAtSide(a,
            placement?.Side ?? d.DefaultSide,
            placement?.Ratio ?? d.DefaultRatio,
            placement?.TabTarget ?? d.DefaultTabTarget);
        if (placement?.Hidden ?? !d.DefaultVisible)
            a.Hide();
    }

    ScheduleReapplyRatios();
    WindowsChanged?.Invoke(this, EventArgs.Empty);
}
```

要点:

- `PlaceAtSide` 内部已写 `_ratios[a.ContentId] = ratio`(`DockingHost.cs:556-557`),不要重复写;
- `Suppress()` 的 `Dispose` 会触发 `RebaseSoon()`,基线按新的 `_descriptors` 重建 —— 正确;
- `EmitLayoutDiffs` 用 `_baseline.TryGetValue` 取基线(`:869`),注册期并发进来的新窗口会被跳过,
  不会 NRE;
- `WindowsChanged` 在 `Suppress` 作用域**之外**触发,避免订阅方在抑制期内回调进来。

### 4.2 `DockingHost` 注销(五个握持点全断)

```csharp
public void UnregisterWindow(string id)
{
    if (!_byId.TryGetValue(id, out var d))
        return;                                   // 幂等

    using (Suppress())
    {
        var a = FindAnchorable(id);
        if (a != null)
        {
            Detach(a);                            // 含 Hidden 集合移除
            a.Content = null;                     // 断开布局树 → 模块对象
        }

        _descriptors.Remove(d);
        _byId.Remove(id);
        _ratios.Remove(id);
        _baseline.Remove(id);
        _preserveDefaultRatioOnSeed.Remove(id);
        _owners.Remove(id);

        if (_contents.Remove(id, out var content) && content is IDisposable disposable)
            TryDispose(disposable);               // 异常吞掉并 Warn,不阻断回收

        _manager.Layout.CollectGarbage();
    }

    WindowsChanged?.Invoke(this, EventArgs.Empty);
}
```

**`_contents.Remove` 与 `a.Content = null` 两条都不能省。** 它们分别是 §5 握持点 1 和 2;
少任何一条,可回收 ALC 卸不掉,而且不报错。

### 4.3 工作页容器

工作页与首页共用同一个 `LayoutDocumentPane`。运行期定位方式:

```csharp
private LayoutDocumentPane ResolveDocumentPane()
    => _manager.Layout.Descendents().OfType<LayoutDocument>()
           .FirstOrDefault(x => x.ContentId == MainContentId)?.Parent as LayoutDocumentPane
       ?? _manager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault()
       ?? throw new InvalidOperationException("布局中找不到中央文档区");
```

(不能缓存构造期的 `docPane` 引用:`ApplyLayoutXml` / `ResetLayout` 会整树替换。)

- `OpenWorkPage`:命中 `_pages` → `IsSelected = IsActive = true` 并返回;
  否则 `new LayoutDocument { ContentId = page.Id, Title = page.Title, Content = 工厂(), CanClose = page.CanClose }`
  加入 pane、选中、记 `_pages[id]` 与 `_owners[id]`、触发 `WindowsChanged`;
- `CloseWorkPage`:从 pane 移除、`Content = null`、内容可 Dispose 则 Dispose、
  清 `_pages`/`_owners`、`CollectGarbage`、触发事件;
- **用户点关闭按钮的路径必须同样清账**:订阅 `_manager.DocumentClosed`,
  按 `ContentId` 走与 `CloseWorkPage` 相同的清理。只清 `_pages` 不清 `_contents`
  是最容易漏的一处。

**`ApplyLayoutXml` 的反序列化回调必须认识工作页。** 现有回调只查 `_byId`(工具窗口),
未命中即 `e.Cancel = true` 丢弃(`DockingHost.cs:445-456`)。工作页登记在 `_pages`,
不补这一路,则 `LoadLayout` / `ResetLayout` 以及 0.7.1 的最大化还原**都会静默清空
用户打开的全部工作页**:

```csharp
else if (contentId != null && _pages.TryGetValue(contentId, out var page))
    e.Content = page.Content;      // 复用既有内容实例,不重新调 ContentFactory
```

这一路属本版必做项,不是可选优化(验收判据见 §7 判据 3 的延伸:
开页 → `layout.load` → 页仍在)。

**关闭语义分裂(写进手册)**:

| 对象 | 关闭按钮 | 依据 |
|---|---|---|
| 工具窗口 `LayoutAnchorable` | 隐藏,可 `win.show` 唤回 | §4.1 既有不变量(`CanClose=false, CanHide=true`) |
| 工作页 `LayoutDocument` | 销毁,需 `page.open` 重开 | 本版新增 |

### 4.4 `ShellUiRegistrar`(Shell 层)

新文件 `Shell/Modules/ShellUiRegistrar.cs`,`IShellUiRegistrar` 的唯一实现:

- 持有 `DockingHost` + `Dispatcher` + `IShellLog`;
- `IsUiThread => _dispatcher.CheckAccess()`;
- **每个方法自行编组**:`_dispatcher.CheckAccess() ? 直接执行 : _dispatcher.Invoke(...)`;
- 维护 `_byOwner: Dictionary<string, HashSet<string>>`,`UnregisterOwner` 遍历副本逐个注销,
  单个失败 Warn 后继续(回收路径不许因一个异常中断);
- `RegisterToolWindow` / `OpenWorkPage` 返回的 `IDisposable` 是幂等的一次性句柄。

### 4.5 `ShellWindow` 装配点(不改顺序)

在 `_docking.Initialize()`(`:130`)之后追加两行,`_modules?.Start()`(`:201`)之前生效:

```csharp
_shellUi = new Modules.ShellUiRegistrar(_docking, Dispatcher, log);
// …既有 BuiltinCommands / EnableModules 分支不动…
if (config.EnableModules)
{
    _modules = new Services.Modules.ModuleHost(...) { UiContext = ..., ShellUi = _shellUi };
}
```

并订阅菜单重建:

```csharp
_docking.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(BuildMenus);
```

`BuildMenus` 现在就是整体 `MainMenu.Items.Clear()` 后重建(`:490`),
整体替换顺带释放旧菜单项的 Click 闭包 —— 这正好断开 §5 握持点 4。

**装配顺序一行不改**是本版「加法」性质的关键证据:因为注册已经是运行期能力,
不再需要把模块装载提前到停靠系统之前。

### 4.6 `ModuleHost` 变更

1. 新增 `public IShellUiRegistrar? ShellUi { get; set; }`;
2. `Snapshot.UiModules` 从 `List<IUiModule>` 改为 `List<(IUiModule Module, string Owner)>`;
   **owner 取值**:`slot.Length > 0 ? slot : Path.GetFileNameWithoutExtension(dll)`——
   这是 UI 模块实例化那一刻(`ScanAssembly` 的 `uiEnabled` 分支,`:260-274`)唯一确定可得的名字,
   `moduleName` 要到后面的 `infoTypes` 循环才算出来。owner 只是回收键,不要求与 `module.list`
   的模块名一致,但**必须写进模块开发手册**,否则模块作者会用错名字调 `UnregisterOwner`;
3. 实例化后注入:`if (instance is IShellUiAware aware && ShellUi != null) aware.ShellUi = ShellUi;`
4. **兜底回收**——`DestroyUi(Snapshot)` 改为:

```csharp
private void DestroyUi(Snapshot snapshot)
{
    foreach (var (module, owner) in snapshot.UiModules)
    {
        try { module.DestroyUi(); }
        catch (Exception ex) { _log.Warn("module", $"销毁 UI 模块失败: {ex.Message}"); }

        // 模块自己注销与否都执行:忘了注销不能成为 ALC 泄漏的理由
        try { ShellUi?.UnregisterOwner(owner); }
        catch (Exception ex) { _log.Warn("module", $"回收模块界面失败 ({owner}): {ex.Message}"); }
    }
}
```

`Reload()` 的 `ui.Send` 块已经是「`DestroyUi` → `SwapRegistrations` → `CreateUi`」且在
`alc.Unload()` 之前(`ModuleHost.cs:138-148`),扩展 `DestroyUi` 即自动落在正确位置。
`Dispose()` 里那次 `DestroyUi` 同样受益。

### 4.7 指令面(BuiltinCommands,谨慎区)

| 指令 | 语义 | 参照 |
|---|---|---|
| `page.list` | 列出工作页(id/标题/活动/owner) | `win.list` |
| `page.open id=` | 打开或激活 | `win.show` |
| `page.close id=` | 关闭(销毁) | —— |
| `page.activate id=` | 仅激活,未打开报错 | —— |

`win.list` 输出增加 owner 列。新增指令必须复跑**验收 7(help 完整)**。

> 注意:`page.open` 只能打开**已注册**的工作页描述符吗?——不能。工作页描述符由模块在
> `OpenWorkPage` 时提供,框架不持有「可开但未开」的页目录。因此 `page.open` 的语义是
> 「激活已打开的页」,与 `page.activate` 同义。**保留两个名字是为了指令直觉**,
> 文档要写明二者等价;若将来要支持「按需开页」,需要模块先注册页目录,那是 0.7.n 的事。

### 4.8 孤儿布局记忆

**不动 `ApplyLayoutXml` 的反序列化回调**(它在 §3.3 冻结机制的邻接区,且回调期拿不到
可靠的位置信息)。改用**旁车记录**,零 Core 契约变更:

- `DockingHost` 构造函数增加可选参数 `ISettingsService? settings = null`,
  由 `ShellWindow` 传入(唯一构造点 `:127`);
- `SaveCurrentLayout()` 时,把当前全部**已注册**窗口的 `{id → side/ratio/hidden/tabTarget}`
  序列化为 JSON,写设置键 `layout.placements`;
- `Initialize()` 时读回到 `_orphanPlacements`;
- `RegisterWindow` 命中则按记录放置(见 §4.1),命中后**立即移除**(一次性);
- 未命中或 settings 为 null → 回落 `DefaultSide/DefaultRatio`,行为与今日一致。

**这是近似恢复,不是完整布局树还原**:标签页顺序、浮动窗口屏幕坐标、
自动隐藏(AutoHide)状态都不在恢复范围内。文档如实标注,不要在验收里承诺。

---

## 5. 生命周期与释放契约(本版最高风险)

模块 UI 对象一旦被框架侧任何长生命周期结构握住,可回收 ALC 就卸不掉。
表现是「`module.reload` 看起来成功、旧版本仍在跑、DLL 删不掉」,**而且不报错**。

已知握持点与断开方式:

| # | 握持点 | 断开方式 | 落在哪 |
|---|---|---|---|
| 1 | `DockingHost._contents[id]` | `Remove` + 可 Dispose 则 Dispose | §4.2 |
| 2 | `LayoutAnchorable.Content` / `LayoutDocument.Content` | `Detach` + `Content = null` + `CollectGarbage` | §4.2 / §4.3 |
| 3 | `_manager.Layout.Hidden` 集合 | `Detach` 内部已处理(`DockingHost.cs:587-591`),注销路径必须走 `Detach` 而非直接从父容器移除 | §4.2 |
| 4 | 视图菜单 `MenuItem` 的 Click 闭包 | `BuildMenus` 整体重建替换 Items | §4.5 |
| 5 | 模块订阅的框架事件(`CommandBus.Executed` / `IShellLog.EntryAdded` / `WindowsChanged`) | **框架无法强制**:契约写明「谁订阅谁退订」;`UnregisterOwner` 完成后若检测到该 owner 仍有窗口/页残留则 Warn | 模块开发手册 |

第 5 条是唯一靠文档兜的,所以坏模块反向用例(§7 判据 4)不许省——它是这条约束的唯一检验手段。

---

## 6. 治理归属与上报边界

按[二次开发演进手册](二次开发演进手册.md)逐条判定:

| 改动 | 分区 | 判定依据 | 是否需上报 |
|---|---|---|---|
| `IDockingService` 新增成员 | 冻结区 §3.2 | 手册原文「**允许**:新增成员」;且全仓库仅 `DockingHost` 一个实现方(已核查) | 否 |
| `ToolWindowInfo` 增带默认值的位置参数 | 冻结区 §3.2 | 同上「允许新增成员」;源码兼容 | 否,但交付记录须写明二进制影响 |
| 新增 `WorkPageDescriptor` / `IShellUiRegistrar` / `IShellUiAware` | 冻结区 §3.2 | 纯新增类型 | 否 |
| `IUiModule` 签名 | —— | **不动** | —— |
| `DockingHost` 新增注册/注销/工作页方法 | 谨慎区 §4「DockingHost 中非 §3.3 机制的部分」 | 新增方法,未修改三大机制 | 否,须复跑验收 1/2/3/10 |
| `BuiltinCommands` 新增 `page.*` | 谨慎区 | 手册明列 | 否,须复跑验收 7 |
| `ShellWindow` 接线与菜单重建 | 谨慎区 | 手册明列 | 否,须复跑验收 5 + 启动冒烟 |
| `ModuleHost` UI 模块生命周期 | 自由区/谨慎区边界 | 0.6.0 新增代码,非 M1 遗产 | 否 |

**触发上报的情形(执行者遇到必须停下)**:

1. 发现不修改 `Suppress()` / `_ratios` / `ReapplyRatios` 就无法实现运行期注册;
2. 发现不改 `ApplyLayoutXml` 的反序列化回调就无法做孤儿记忆(§4.8 已给出旁车方案避开,
   若旁车方案也不成立);
3. 发现必须修改 `IUiModule` 既有签名;
4. 发现工作页与工具窗口无法共存于同一 `LayoutDocumentPane`(需要改默认布局构建的骨架)。

上报格式见手册 §6。**禁止**先改了再在总结里提一句。

---

## 7. 0.7.0 验收(框架侧)

| # | 判据 | 通过条件 |
|---|---|---|
| 1 | 构建 | Debug/Release **0 警告 0 错误** |
| 2 | 运行期注册可用 | 演示宿主里最小演示模块注册一个工具窗口:出现在停靠区、进视图菜单、受 `win.show/hide/dock/ratio` 管辖、随 `layout.save/load` 存活;注销后从上述全部位置消失 |
| 3 | 工作页可用 | 开 3 页、切换、关 1 页;`page.list` 与实际一致;首页不可关 |
| 4 | **ALC 不泄漏(本版核心)** | 连续 `module.reload` **10 次**后:① 可回收 `AssemblyLoadContext` 数量回落到基线;② 模块 DLL 可被删除(未被锁);③ 工具窗口与工作页数量回落到基线(无重影) |
| 5 | **坏模块反向用例** | 一个「注册了窗口但 `DestroyUi` 里不注销」的模块,热重载 10 次后判据 4 三条仍全绿(证明框架兜底真的兜住) |
| 6 | 布局双向兼容 | 老布局文件(无模块窗口)+ 新框架正常;新布局文件 + 模块缺席不产生告警风暴;模块窗口位置近似恢复(side/ratio/hidden) |
| 7 | 派生零感知 | OHS 以现有装配路径构建:七套 Smoke 全绿、MCP 快照与 0.6.1 基线逐条一致 |
| 8 | 既有布局验收复跑 | 验收 1 / 2 / 3 / 10 全套(动了 `DockingHost` 谨慎区) |
| 9 | help 完整 | 验收 7(新增 `page.*` 后 help 分组与计数正确) |
| 10 | 包链 | 四包 staging、包身份、XML 文档、符号包、漏洞审计、隔离 PackageSmoke 全绿 |

判据 4 与 5 是本版存在的理由。**只验证「模块窗口能内嵌」而不验证「热重载后能干净下线」,
等于把泄漏留给用户**——这条写在这里,交付时按它对账。

---

## 8. 排除项

| 项 | 理由 |
|---|---|
| 多实例窗口 / 多实例工作页 | `IsSingleton` 保持 W-11 预留状态,首版仅单例 |
| 修改 `IUiModule` 既有签名 | 加法原则 |
| ServiceHost 无窗档承载工作页 | 无窗进程没有停靠系统;该档 `ShellUi` 为 null,模块须判空 |
| 工作页布局的完整树还原 | 见 §4.8,只做近似恢复 |
| 模块直接引用 AvalonDock 类型 | §14.2 封装原则 |
| 面板系统(`PanelManager`)改造 | 维持启动期通道,新增面板仍需重启(P-08 语义不变);运行期动态界面走本版新路 |
| 「可开但未开」的工作页目录 | 见 §4.7 注,留给 0.7.n |

---

## 9. 版本与交付纪律

- 版本真值沿 0.5.0 机制(中央版本/锁文件/包身份),完整走发布链
  (pack / validation / PackageSmoke / manifest / SHA-256);
- 交付后在[版本记录](AppShell版本记录.md)加一行(**交付时写,规划期不预写**);
- 与 OHS **V2.7.0** 严格配对,任一侧验收未过不得交付;
- 0.7.n 区间收「窗口与界面契约」主题的后续增量
  (已规划:[0.7.1 页面最大化](AppShell-0.7.1-页面最大化.md));不相关的框架改动等 0.8.0;
- 执行者纪律同 OHS 32 号 §7(含 CET 环境变量:`DOTNET_EnableWriteXorExecute=0`)。

---

## 10. 交付记录

- Core 已新增 `IShellUiRegistrar`、`IShellUiAware`、`WorkPageDescriptor` 和工作页信息模型；现有
  `IUiModule` 签名保持不变。
- `DockingHost` 已支持工具窗/工作页运行期注册与注销、owner 回收、`page.*` 命令、动态视图菜单和
  迟到模块的近似位置恢复。布局载入保留已注册工作页，注册动作全程抑制布局命令回声。
- `ModuleHost` 在 `CreateUi` 前注入 UI 注册器，并在旧 ALC 卸载前无条件执行 owner 清理。故意不在
  `DestroyUi` 注销窗口的坏模块连续热重载 10 次，无窗口重影，旧 ALC 弱引用全部回落，DLL 可删除。
- AppShell Debug/Release 均为 0 警告、0 错误；OHS Debug/Release 原七套 Smoke 加 DockingSuite
  全部通过。真实载体 SE2SW 已从顶层 `Window` 迁移到中央工作页，生产代码不再包含窗口宿主。
- 与 0.7.1 合并执行正式 staging：四个包及符号包身份正确，漏洞项 0，隔离 PackageSmoke 通过，
  演示宿主 FileVersion=`0.7.1.0`。

—— 文档结束 ——
