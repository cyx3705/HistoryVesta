# AppShell 0.7.1 · 页面最大化 —— 框架工程实施文档

> **历史说明：** 本文记录 0.7.1 当时覆盖工作页和主内容的实现。0.7.2 已删除这两类对象，
> 最大化现仅适用于普通工具窗口，见 [AppShell 0.7.2 统一工具窗口模型](AppShell-0.7.2-统一工具窗口模型.md)。

> 文档性质:框架小增量(**全部加法**,不改任何既有契约签名)
> 基线版本:AppShell **0.7.0**(模块内嵌窗口与工作页)｜ 目标:**0.7.1**
> 日期:2026-07-27 ｜ 状态:**已实施并通过自动化验收**
> 硬前提:0.7.0 已交付(工作页存在,否则本版只覆盖工具窗口一半场景)
>
> 交付配对:**可独立交付**。OHS 侧零代码改动,只需重编译 + 命令手册补一条;
> 若与 OHS V2.7.0 同批,则作为该版的第二个框架增量一并走发布链。
>
> 相关框架文档:[0.7.0 模块内嵌窗口与工作页](AppShell-0.7.0-模块内嵌窗口与工作页.md) ·
> [版本记录](AppShell版本记录.md) · [二次开发演进手册](二次开发演进手册.md)

---

## 0. 一句话定义

双击内嵌页面顶部的蓝色标题拖动条,该页面铺满整个主窗体(其余页面全部让位);
再次双击回到双击前的布局。配套 `win.max` / `win.restore` 指令,
让「只用某个模块页面、不看整个 Shell」既能手动做,也能脚本化。

---

## 1. 需求与动机

派生应用(OHS)的模块页面越来越重要,但主窗体永远带着控制台、命令集、模块管理、
资源窗口等一整套框架窗口。用户想**单独使用某个模块页面**时,现在只能一个个手动隐藏,
下次还得一个个恢复。

本版给一个可逆的单手势:双击蓝色标题拖动条 → 独占全窗;再双击 → 原样回来。

---

## 2. 施工前必须实测确认的两件事

本文其余部分基于两个**尚未实测**的判断。执行者动手第一步就是验证它们,
结论与下述不一致时**停下上报**,不要照着改。

### 2.1 双击手势当前落在哪里(决定 hook 点)

本 Shell 的锚定窗格模板是自定义的。`ShellWindow.xaml` 的
`TabsTopAnchorablePaneTemplate`(:14-48)**没有 `AnchorablePaneTitle`**,
头部只有 `adc:AnchorablePaneTabPanel`(:25-30):

```xml
<adc:AnchorablePaneTabPanel x:Name="HeaderPanel" Grid.Row="0" ... IsItemsHost="True" ... />
```

维护复核确认，用户所指的「顶部拖动条」是内容上方带点的蓝色标题条，而不是更上方的标签条。
工具窗使用 AvalonDock 原生 `AnchorablePaneTitle`；工作页与主内容原本没有等价标题条，因此由 Shell
统一增加 `DockDocumentChrome` / `DockDocumentTitleBar`。标题条在单窗格最大化布局中仍然存在，用户可
在同一位置再次双击恢复；上方标签不再承载最大化手势。

**要确认**:当前双击这两处分别发生什么。AvalonDock 4.72.1 的程序集里确有
`ClickCount` / `OnMouseLeftButtonDown` 符号,双击很可能已被用于「浮动/停靠切换」。
若确实已有默认行为,本版就是**覆盖既有手势**,必须写进版本记录与手册的行为变更条目。

### 2.2 `LayoutDocumentPane` 能否容纳 `LayoutAnchorable`

`LayoutAnchorable` 派生自 `LayoutContent`,AvalonDock 支持把锚定窗口拖成「标签式文档」,
因此**推测**可以。这一条决定 §4.2 用哪种临时布局形状:

- **能**(首选):最大化态统一为「一个 `LayoutDocumentPane` 装唯一目标」。
  `FindCenterChild` / `EnsureCenterColumn` 仍能找到中央区,现有逻辑全部照常。
- **不能**(回落):工具窗口用 `LayoutAnchorablePane`,此时布局里没有文档窗格,
  `EnsureCenterColumn` 会抛「布局中找不到主内容区」。必须按 §4.5 对
  `win.dock` / `win.ratio` / `win.reset` / `layout.*` 在最大化态下明确拒绝。

**实测方式**:演示宿主里手动把一个锚定窗口拖进文档区,看是否成立并可正常序列化/反序列化。

---

## 3. 契约变更(AppShell.Core)

`IDockingService` 加法扩展三项,既有成员一字不动:

```csharp
// Core/Docking/IDockingService.cs
public interface IDockingService
{
    // …0.7.0 及以前的成员全部不动…

    /// <summary>当前被最大化的窗口/工作页 id;未最大化为 null。</summary>
    string? MaximizedId { get; }

    /// <summary>
    /// 最大化指定窗口或工作页:其余全部让位,目标铺满主窗体。
    /// 已处于最大化态时:目标相同为幂等;目标不同则先还原再最大化。
    /// id 不存在抛 ArgumentException。
    /// </summary>
    void MaximizeWindow(string id);

    /// <summary>退出最大化,回到进入前的布局;未最大化时静默忽略(幂等)。</summary>
    void RestoreLayoutFromMaximized();
}
```

`MaximizedId` 变化时复用 0.7.0 已有的 `WindowsChanged` 事件通知(不新增事件)。

> 治理判定:`IDockingService` 在冻结区 §3.2,但手册原文「**允许**:新增成员」;
> 且全仓库 `: IDockingService` 仅 `DockingHost` 一处(0.7.0 已核查)。**不需上报**。

---

## 4. Shell 层实现

### 4.1 状态与总体思路

`DockingHost` 增两个字段:

```csharp
private string? _maximizedId;
private string? _layoutBeforeMaximize;   // 进入最大化前的完整布局 XML(仅内存)
```

**方案:布局快照法。** 进入最大化 = 序列化当前布局到内存 + 换上只有目标的临时布局;
退出 = 把快照反序列化回来。理由是**完全复用既有机制**:
`SerializeLayout()` / `ApplyLayoutXml()` / `EnsureRegisteredWindows()` / `AttachLayout()`
是 `LoadLayout` 已经在跑的路径,不引入第二套布局操作语义。

内容实例不会丢:`ApplyLayoutXml` 的回调经 `GetOrCreateContent` 取 `_contents` 缓存
(`DockingHost.cs:493-504`),同一个 id 拿到的是**同一个** UI 对象。
面板里已输入的文本、表格滚动位置等因此在最大化往返后保持——**这是验收判据,不是推测**(§6 判据 3)。

被否决的替代方案:把目标内容临时搬进一个铺满窗体的 `ContentControl` 覆盖层。
WPF 元素跨可视树搬迁会重置滚动位置与焦点,且 AvalonDock 会看到一个空的窗格,
风险高于收益。

### 4.2 进入最大化

```csharp
public void MaximizeWindow(string id)
{
    if (_maximizedId == id)
        return;                                   // 幂等
    if (_maximizedId != null)
        RestoreLayoutFromMaximized();             // 换目标 = 先还原

    if (!_byId.ContainsKey(id) && !_pages.ContainsKey(id))
        throw new ArgumentException($"未注册的窗口或工作页: {id}", nameof(id));

    using (Suppress())                            // 铁律:布局换血不得回声成 win.* 指令风暴
    {
        _layoutBeforeMaximize = SerializeLayout();
        BuildMaximizedLayout(id);                 // 见 §2.2 两种形状
        AttachLayout();
        _maximizedId = id;
    }

    WindowsChanged?.Invoke(this, EventArgs.Empty);
    RebaseSoon();
}
```

`BuildMaximizedLayout` 按 §2.2 的实测结论选形状,内容一律经 `GetOrCreateContent`
(工具窗口)或 `_pages` 的既有内容(工作页)取,**不要重新调用 ContentFactory**。

### 4.3 退出最大化

```csharp
public void RestoreLayoutFromMaximized()
{
    if (_maximizedId == null || _layoutBeforeMaximize == null)
        return;                                   // 幂等

    using (Suppress())
    {
        ApplyLayoutXml(_layoutBeforeMaximize);
        EnsureRegisteredWindows();                // 最大化期间新注册的窗口在此补位
        AttachLayout();
        _maximizedId = null;
        _layoutBeforeMaximize = null;
        _seedRatiosFromLayout = true;             // 与 LoadLayout 一致:以快照尺寸反向采集比例
    }

    WindowsChanged?.Invoke(this, EventArgs.Empty);
    RebaseSoon();
}
```

### 4.4 ⚠ 前置依赖:`ApplyLayoutXml` 必须认识工作页

0.7.0 文档 §4.3 已把这一路列为必做项。**本版开工第一件事是核对它真的做了**——
没做的话最大化还原会静默清空全部工作页,而且不报错。

原始回调只认识工具窗口:

```csharp
// DockingHost.cs:445-456
else if (contentId != null && _byId.TryGetValue(contentId, out var d))
    e.Content = GetOrCreateContent(d);
else
    e.Cancel = true;      // ← 工作页会落到这里被丢弃
```

0.7.0 的工作页登记在 `_pages`,不在 `_byId`。**不补这个分支,每次退出最大化会把用户
打开的全部工作页清空**——而且不报错。回调必须加一路:

```csharp
else if (contentId != null && _pages.TryGetValue(contentId, out var page))
    e.Content = page.Content;   // 复用既有内容实例,不重建
```

同一路同时保住 `LoadLayout` / `ResetLayout`。若 0.7.0 实施时遗漏,本版补上并在
交付记录里注明是补 0.7.0 的漏项。

### 4.5 最大化态下的操作约束

| 操作 | 最大化态下的行为 |
|---|---|
| `win.show` / `win.hide` / `win.float`(目标是被最大化者) | 先自动还原,再执行 |
| `win.show`(目标是其他窗口) | 先自动还原,再执行 |
| `win.dock` / `win.ratio` / `win.reset` | §2.2 回落形状时**明确拒绝**并提示「请先 win.restore」;首选形状下可自动还原后执行 |
| `layout.save` | 保存**底层布局**(`_layoutBeforeMaximize`),不保存临时布局 |
| `layout.load` / `layout.reset` | 先还原,再执行 |
| 注销当前被最大化的窗口/工作页(0.7.0 `UnregisterWindow` / `CloseWorkPage`) | **必须先自动还原**,否则临时布局里留下空壳 |
| 模块在最大化期间注册新窗口 | 允许;它进临时布局,还原后由 `EnsureRegisteredWindows` 按默认位补位(快照里没有它) |
| `ReapplyRatios` | 最大化态直接 return(只有一个窗格,比例无意义) |

「先自动还原再执行」是缺省策略;拒绝只用于 §2.2 回落形状下确实做不到的三条。

### 4.6 退出时的持久化

`SaveCurrentLayout()`(`DockingHost.cs:164-175`)必须写**底层布局**:

```csharp
_store.WriteCurrent(_layoutBeforeMaximize ?? SerializeLayout());
```

**不持久化最大化状态本身。** 理由是可用性陷阱:若退出时停在最大化态、重启后直接
只剩一个面板,菜单和控制台都还在但用户不知道发生了什么,只能试 `layout.reset`。
需要「启动即最大化」的场景走 §4.8 的指令 + 派生应用启动参数,是显式的。

### 4.7 手势接线（维护修订：只认标题拖动条）

在 `ShellWindow` 里对 `DockManager` 挂一个冒泡的双击处理器,而不是逐个标签项订阅:

```csharp
DockManager.AddHandler(Control.MouseDoubleClickEvent,
    new MouseButtonEventHandler(OnDockDoubleClick), handledEventsToo: true);
```

处理器从 `e.OriginalSource` 沿可视树上溯,命中下列任一即取其 `Model` 的 `ContentId`:

| 命中类型 | 对应 |
|---|---|
| `AnchorablePaneTitle` | 工具窗口原生蓝色标题拖动条 |
| `DockDocumentTitleBar` | 主内容与工作页统一蓝色标题拖动条 |

拿到 id 后:`MaximizedId == id ? RestoreLayoutFromMaximized() : MaximizeWindow(id)`,
并置 `e.Handled = true`。

**三条纪律**:

1. **不要改 `TabsTopAnchorablePaneTemplate`。** 工具窗继续使用原生 `AnchorablePaneTitle`；文档区通过
   内容 chrome 加标题条，不破坏既有窗格模板和主题绑定。
2. **单处订阅,不逐项订阅。** 逐个标签项挂事件会在 0.7.0 的运行期注册/注销中泄漏
   处理器,并握住模块对象(0.7.0 §5 握持点 4 的同类问题)。
3. **手势可关。** `ShellConfig` 增 `public bool EnableMaximizeOnDoubleClick { get; set; } = true;`
   ——若 §2.1 实测发现双击已有既定用途且派生应用依赖它,派生侧可关掉手势、保留指令。

### 4.8 指令与菜单(谨慎区)

| 指令 | 语义 |
|---|---|
| `win.max name=<id>` | 最大化指定窗口/工作页(幂等) |
| `win.restore` | 退出最大化(幂等) |

- `win.list` 增一列标记当前最大化项(或在 `win.max` 无参时回显当前状态);
- 视图菜单加一项「退出最大化」,仅在 `MaximizedId != null` 时可用——
  菜单已在 0.7.0 改为随 `WindowsChanged` 重建,直接受益;
- 状态栏右侧现显示「布局: {名称}」(`ShellWindow.xaml.cs:543-544`),
  最大化时追加标记,例如 `布局: 默认 · 最大化: se2sw`——**这是用户的状态出口**,不许省;
- 新增指令须复跑**验收 7(help 完整)**。

派生应用要「启动即只看某页」,在启动参数/`ConfigureCommands` 之后执行
`win.max name=<id>` 即可,无需框架增设启动配置项。

---

## 5. 治理归属

| 改动 | 分区 | 是否需上报 |
|---|---|---|
| `IDockingService` 增 3 个成员 | 冻结区 §3.2,但「允许新增成员」;唯一实现方已核查 | 否 |
| `DockingHost` 增最大化字段与方法 | 谨慎区(非 §3.3 三大机制) | 否,须复跑验收 1/2/3/10 |
| `ApplyLayoutXml` 回调增工作页分支(§4.4) | 谨慎区;**是修既有缺陷,不是改机制** | 否,但交付记录须单列 |
| `ShellWindow` 事件接线、菜单、状态栏 | 谨慎区 | 否,须复跑验收 5 + 启动冒烟 |
| `BuiltinCommands` 增 `win.max`/`win.restore` | 谨慎区 | 否,须复跑验收 7 |
| `TabsTopAnchorablePaneTemplate` | 冻结区 §3.3 | **不动**;若发现非改不可 → 上报 |

**触发上报**:①§2.1/§2.2 实测结论与本文不符;②不改样式接管就接不到双击;
③不改 `Suppress()`/`_ratios`/`ReapplyRatios` 就实现不了最大化往返。

---

## 6. 0.7.1 验收(框架侧)

| # | 判据 | 通过条件 |
|---|---|---|
| 1 | 构建 | Debug/Release **0 警告 0 错误** |
| 2 | 手势往返 | 双击工具窗口蓝色标题条 → 铺满主窗体、其余窗口不可见;再双击同一标题条 → **逐窗口比对**位置/比例/显隐与双击前一致 |
| 3 | **内容不丢状态** | 最大化前在面板文本框输入内容、把表格滚到第 N 行;往返后内容与滚动位置保持(证明走的是 `_contents` 缓存而非重建) |
| 4 | **工作页不被清空(§4.4)** | 开 3 个工作页 → 最大化其中一个 → 还原 → 3 个页都在、选中项正确 |
| 5 | 文档标题条 | 主内容与工作页都有蓝色标题条；双击同样往返，模块工作页可拖出浮动并重新停靠 |
| 6 | 指令面 | `win.max` / `win.restore` 幂等;`win.list` 与状态栏反映最大化态;help 完整(验收 7) |
| 7 | 退出持久化 | 处于最大化态时退出 → 重启后是**底层布局**,不是单面板 |
| 8 | 交叉操作 | §4.5 表格逐行实测,尤其:注销被最大化的窗口自动还原、最大化期间注册的新窗口还原后能按默认位出现 |
| 9 | 无指令风暴 | 最大化/还原往返 5 次,控制台的 `[layout] >` 回显条数为 0(证明 `Suppress()` 生效) |
| 10 | 既有布局验收复跑 | 验收 1 / 2 / 3 / 10 全套 |
| 11 | 派生零感知 | OHS 重编译后七套 Smoke 全绿、MCP 快照与 0.7.0 基线逐条一致 |
| 12 | 包链 | 四包 staging、包身份、XML 文档、符号包、漏洞审计、隔离 PackageSmoke 全绿 |

判据 3、4、9 是本版真正的技术风险点,不许用「看起来对了」替代。

---

## 7. 排除项

| 项 | 理由 |
|---|---|
| 持久化最大化状态 | §4.6 的可用性陷阱;需要「启动即最大化」走指令 |
| 同时最大化多个页面 | 语义即「独占全窗」,多个无意义 |
| 浮动窗口的最大化 | 浮动窗有自己的系统标题栏与 AvalonDock 自带的 `IsMaximized`,不在本版范围 |
| 自动隐藏(AutoHide)状态的往返保真 | 快照法能存下 XML 里有的部分;AutoHide 的还原保真度不做承诺,验收判据 2 以「位置/比例/显隐」为准 |
| Esc 键退出最大化 | Esc 在派生应用里用途太多,不抢;出口是再次双击 + 菜单 + `win.restore` |
| 改 `TabsTopAnchorablePaneTemplate` 加专用标题栏 | 冻结区 §3.3 |

---

## 8. 版本与交付纪律

- 版本真值沿 0.5.0 机制,完整走发布链(pack / validation / PackageSmoke / manifest / SHA-256);
- 交付后在[版本记录](AppShell版本记录.md)加一行(交付时写,规划期不预写);
  若 §2.1 实测确认覆盖了既有双击行为,该行**必须写明行为变更**;
- 0.7.1 属 0.7.n「窗口与界面契约」区间,与 0.7.0 同主题;
- 本版对 OHS 无代码要求,但**命令手册需补 `win.max` / `win.restore` 与双击手势说明**;
- 执行者纪律同 OHS 32 号 §7(含 CET 环境变量:`DOTNET_EnableWriteXorExecute=0`)。

---

## 9. 交付记录

- `DockingHost` 已实现页面/工具窗最大化与恢复；重复最大化幂等，恢复后回到最大化前布局，内容对象不重建。
- 蓝色标题拖动条双击已接入同一状态机；最大化后标题条保留，可在原位置再次双击恢复。主内容与工作页
  统一补齐标题条，模块工作页默认允许浮动；命令面 `win.max`、`win.restore` 继续适用于工作页和工具窗。
- 文档标题条在绑定 `LayoutDocument` 后，并在 `Loaded` 与可视父级变化时重绑 AvalonDock `LayoutItem`，
  避免首次装载、最大化重建或重新停靠时因模型绑定早晚不同而丢失拖动能力。
- 同批修复 0.7.0 布局载入漏项：`ApplyLayoutXml` 可正确恢复已注册工作页，不再只处理首页和工具窗。
- DockingSuite 覆盖三工作页、最大化/恢复、内容不重建、布局保存/载入及无命令风暴；Debug/Release
  均通过。AppShell 四包 staging、漏洞审计、PackageSmoke 与演示发布全部通过。
- 正式 OHS MCP 实跑 `page.list/open`、`win.max name=se2sw`、`win.restore` 全部成功，最终启动段
  Warn/Error 为 0。本机 Computer Use 截图捕获返回 `SetIsBorderRequired failed (0x80004002)`，因此未把
  坐标级双击/拖拽写成已通过；对应行为由 DockingSuite、标题条事件路由和正式 MCP 往返共同覆盖。

—— 文档结束 ——
