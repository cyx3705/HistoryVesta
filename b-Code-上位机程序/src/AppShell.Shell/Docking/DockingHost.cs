using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace AppShell.Shell.Docking;

/// <summary>
/// AvalonDock 二次封装(§14.2)。Shell 对外只暴露 IDockingService,
/// 派生应用与四类标准窗口不接触任何 AvalonDock 类型。
/// 职责:窗口注册、默认布局构建、布局持久化(含损坏回退 N-06)、
/// 布局手势 → 等价指令(W-10,含防再入抑制)。
/// </summary>
public sealed class DockingHost : IDockingService
{
    private const string MainContentId = "__main__";
    private const string LayoutSource = "layout";
    private const double RatioEpsilon = 0.02;

    private readonly DockingManager _manager;
    private readonly ILayoutStore _store;
    private readonly IShellLog _log;
    private readonly List<ToolWindowDescriptor> _descriptors = new();
    private readonly Dictionary<string, ToolWindowDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mainContent;
    private readonly DispatcherTimer _debounce;

    private Dictionary<string, WinState> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private LayoutRoot? _attachedRoot;
    private int _suppress;

    // W-05 比例语义:AvalonDock 对与文档区同面板的侧窗格采用像素语义
    // (LayoutPanelControl.OnFixChildrenDockLengths 会把星值固化为像素),
    // “占主程序窗体百分比”由封装层维护:记录目标比例,在首次排布后与
    // 窗体缩放后重新按比例施加像素尺寸。
    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _resizeDebounce;

    public DockingHost(
        DockingManager manager,
        IEnumerable<ToolWindowDescriptor> windows,
        object mainContent,
        ILayoutStore store,
        IShellLog log)
    {
        _manager = manager;
        _store = store;
        _log = log;
        _mainContent = mainContent;

        foreach (var d in windows)
        {
            if (_byId.ContainsKey(d.Id))
                throw new InvalidOperationException($"工具窗口 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");
            _descriptors.Add(d);
            _byId.Add(d.Id, d);
            _ratios[d.Id] = d.DefaultRatio;
        }

        // W-10:拖拽类连续手势在动作结束时才生成指令 —— 用去抖合并布局事件
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            EmitLayoutDiffs();
        };

        _manager.Loaded += (_, _) => RebaseSoon();

        // 主窗体缩放 → 按记录的百分比重算各停靠区尺寸(W-05)
        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            using (Suppress())
            {
                ReapplyRatios();
            }
        };
        _manager.SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    /// <summary>当前布局方案名(状态栏显示用)。</summary>
    public string CurrentLayoutName { get; private set; } = "默认";

    /// <summary>true 表示下一次比例处理应从已恢复的布局反向采集比例,而非施加记录值。</summary>
    private bool _seedRatiosFromLayout;

    public event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    /// <summary>已注册窗口(视图菜单构建用)。</summary>
    public IReadOnlyList<ToolWindowDescriptor> Descriptors => _descriptors;

    // ---------------------------------------------------------------- 启动/退出

    /// <summary>启动时调用:恢复上次布局,失败或不存在则构建默认布局(W-07 / N-06)。</summary>
    public void Initialize()
    {
        using (Suppress())
        {
            string? xml = null;
            try
            {
                xml = _store.ReadCurrent();
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取布局文件失败: {ex.Message}");
            }

            var restored = false;
            if (xml != null)
            {
                try
                {
                    ApplyLayoutXml(xml);
                    if (!LayoutHasMainContent())
                        throw new InvalidOperationException("布局中缺少主内容区");
                    EnsureRegisteredWindows();
                    restored = true;
                    _seedRatiosFromLayout = true; // 以文件里的尺寸为准,反向采集比例
                    _log.Info(LayoutSource, "已恢复上次退出时的布局");
                }
                catch (Exception ex)
                {
                    // N-06:布局文件损坏 → 回退默认布局 + 告警,不阻断启动
                    _log.Warn(LayoutSource, $"布局文件损坏,已回退默认布局(原因: {ex.Message})");
                    try { _store.DeleteCurrent(); } catch { /* 清理失败可忽略 */ }
                }
            }

            if (!restored)
                BuildDefaultLayout();

            AttachLayout();
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    /// <summary>退出时调用:自动保存当前布局(W-07)。</summary>
    public void SaveCurrentLayout()
    {
        try
        {
            _store.WriteCurrent(SerializeLayout());
            _log.Info(LayoutSource, "退出前已自动保存布局");
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"保存布局失败: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- IDockingService

    public IReadOnlyList<ToolWindowInfo> ListWindows()
        => _descriptors
            .Select(d =>
            {
                var s = ComputeState(d.Id);
                return new ToolWindowInfo(d.Id, d.Title, s.Visible, s.Floating, s.Side, s.Visible && !s.Floating ? s.Ratio : null);
            })
            .ToList();

    public void Show(string id)
    {
        var a = FindRequired(id);
        using (Suppress())
        {
            if (a.IsHidden)
                a.Show();
            a.IsSelected = true;
            a.IsActive = true;
        }
    }

    public void Hide(string id)
    {
        var a = FindRequired(id);
        using (Suppress())
        {
            if (!a.IsHidden)
                a.Hide();
        }
    }

    public void Float(string id)
    {
        var a = FindRequired(id);
        using (Suppress())
        {
            if (a.IsHidden)
                a.Show();
            if (!IsFloating(a))
                a.Float();
        }
    }

    public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null)
    {
        var a = FindRequired(id);
        using (Suppress())
        {
            PlaceAtSide(a, side, ratio ?? _byId[id].DefaultRatio, targetId);
            a.IsSelected = true;
        }
    }

    public void SetRatio(string id, double ratio)
    {
        if (ratio is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须在 (0,1) 之间");

        var a = FindRequired(id);
        var side = DetectSide(a);
        if (side is null or DockSide.Tab)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 当前不是四边停靠状态,无法调整比例");
            return;
        }

        using (Suppress())
        {
            ApplyRatio(a, side.Value, ratio);
        }
    }

    public void ResetWindow(string id)
    {
        var d = _byId[id];
        var a = FindRequired(id);
        using (Suppress())
        {
            PlaceAtSide(a, d.DefaultSide, d.DefaultRatio, d.DefaultTabTarget);
            a.IsSelected = true;
        }
    }

    public void ResetLayout()
    {
        using (Suppress())
        {
            BuildDefaultLayout();
            AttachLayout();
            CurrentLayoutName = "默认";
            _seedRatiosFromLayout = false;
            foreach (var d in _descriptors)
                _ratios[d.Id] = d.DefaultRatio;
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    public void SaveLayout(string name)
    {
        _store.WriteNamed(name, SerializeLayout());
        CurrentLayoutName = name;
        _log.Info(LayoutSource, $"布局方案已保存: {name}");
    }

    public bool LoadLayout(string name)
    {
        string? xml;
        try
        {
            xml = _store.ReadNamed(name);
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"读取布局方案 {name} 失败: {ex.Message}");
            return false;
        }

        if (xml == null)
        {
            _log.Warn(LayoutSource, $"布局方案不存在: {name}");
            return false;
        }

        try
        {
            using (Suppress())
            {
                ApplyLayoutXml(xml);
                EnsureRegisteredWindows();
                AttachLayout();
                CurrentLayoutName = name;
                _seedRatiosFromLayout = true;
            }

            RebaseSoon();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(LayoutSource, $"加载布局方案 {name} 失败: {ex.Message}");
            using (Suppress())
            {
                BuildDefaultLayout();
                AttachLayout();
                CurrentLayoutName = "默认";
            }

            RebaseSoon();
            return false;
        }
    }

    public IReadOnlyList<string> ListLayouts() => _store.ListNamed();

    // ---------------------------------------------------------------- 布局构建与序列化

    private void BuildDefaultLayout()
    {
        var mainDoc = new LayoutDocument
        {
            Title = "主窗口",
            ContentId = MainContentId,
            Content = _mainContent,
            CanClose = false,
            CanFloat = false,
        };
        var docPane = new LayoutDocumentPane(mainDoc);

        // 中央列:文档区 +(可选)上/下停靠区,垂直排布
        var centerColumn = new LayoutPanel(docPane) { Orientation = Orientation.Vertical };
        var rootPanel = new LayoutPanel(centerColumn) { Orientation = Orientation.Horizontal };
        var root = new LayoutRoot { RootPanel = rootPanel };
        _manager.Layout = root;

        double leftRatio = 0, rightRatio = 0, topRatio = 0, bottomRatio = 0;

        LayoutAnchorablePane MakeSidePane(IEnumerable<ToolWindowDescriptor> group)
        {
            var pane = new LayoutAnchorablePane();
            foreach (var d in group)
                pane.Children.Add(CreateAnchorable(d));
            return pane;
        }

        var bySide = _descriptors
            .Where(d => d.DefaultSide != DockSide.Tab)
            .GroupBy(d => d.DefaultSide)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bySide.TryGetValue(DockSide.Left, out var lefts))
        {
            leftRatio = lefts.Max(d => d.DefaultRatio);
            var pane = MakeSidePane(lefts);
            pane.DockWidth = Star(leftRatio);
            rootPanel.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Right, out var rights))
        {
            rightRatio = rights.Max(d => d.DefaultRatio);
            var pane = MakeSidePane(rights);
            pane.DockWidth = Star(rightRatio);
            rootPanel.Children.Add(pane);
        }

        if (bySide.TryGetValue(DockSide.Top, out var tops))
        {
            topRatio = tops.Max(d => d.DefaultRatio);
            var pane = MakeSidePane(tops);
            pane.DockHeight = Star(topRatio);
            centerColumn.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Bottom, out var bottoms))
        {
            bottomRatio = bottoms.Max(d => d.DefaultRatio);
            var pane = MakeSidePane(bottoms);
            pane.DockHeight = Star(bottomRatio);
            centerColumn.Children.Add(pane);
        }

        centerColumn.DockWidth = Star(1 - leftRatio - rightRatio);
        docPane.DockHeight = Star(1 - topRatio - bottomRatio);

        // 第二遍:并入标签组的窗口(DefaultSide = Tab)
        foreach (var d in _descriptors.Where(d => d.DefaultSide == DockSide.Tab))
        {
            var a = CreateAnchorable(d);
            var target = d.DefaultTabTarget != null ? FindAnchorable(d.DefaultTabTarget) : null;
            if (target?.Parent is LayoutAnchorablePane tp)
            {
                tp.Children.Add(a);
            }
            else
            {
                _log.Warn(LayoutSource, $"窗口 {d.Id} 的默认标签组目标 {d.DefaultTabTarget ?? "(空)"} 不存在,改为右侧停靠");
                var pane = new LayoutAnchorablePane(a) { DockWidth = Star(d.DefaultRatio) };
                rootPanel.Children.Add(pane);
            }
        }

        // 默认隐藏的窗口
        foreach (var d in _descriptors.Where(d => !d.DefaultVisible))
            FindAnchorable(d.Id)?.Hide();

        root.CollectGarbage();
    }

    private string SerializeLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(_manager).Serialize(writer);
        return writer.ToString();
    }

    private void ApplyLayoutXml(string xml)
    {
        var serializer = new XmlLayoutSerializer(_manager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            var contentId = e.Model.ContentId;
            if (contentId == MainContentId)
            {
                e.Content = _mainContent;
            }
            else if (contentId != null && _byId.TryGetValue(contentId, out var d))
            {
                e.Content = GetOrCreateContent(d);
            }
            else
            {
                // 布局文件里有当前版本未注册的窗口 → 丢弃,不阻断加载
                e.Cancel = true;
            }
        };

        using var reader = new StringReader(xml);
        serializer.Deserialize(reader);
    }

    /// <summary>布局加载后补齐缺失的已注册窗口(旧布局文件兼容)。</summary>
    private void EnsureRegisteredWindows()
    {
        foreach (var d in _descriptors)
        {
            if (FindAnchorable(d.Id) != null)
                continue;

            var a = CreateAnchorable(d);
            PlaceAtSide(a, d.DefaultSide, d.DefaultRatio, d.DefaultTabTarget);
            if (!d.DefaultVisible)
                a.Hide();
        }
    }

    private bool LayoutHasMainContent()
        => _manager.Layout.Descendents().OfType<LayoutDocument>().Any(x => x.ContentId == MainContentId);

    private LayoutAnchorable CreateAnchorable(ToolWindowDescriptor d) => new()
    {
        ContentId = d.Id,
        Title = d.Title,
        Content = GetOrCreateContent(d),
        // §4.1:关闭按钮语义为隐藏,不销毁
        CanClose = false,
        CanHide = true,
        CanAutoHide = true,
        CanFloat = true,
    };

    private object GetOrCreateContent(ToolWindowDescriptor d)
    {
        if (!_contents.TryGetValue(d.Id, out var content))
        {
            content = d.ContentFactory?.Invoke()
                      ?? throw new InvalidOperationException(
                          $"窗口 {d.Id} 未提供内容工厂(仅 console 由 Shell 接管内容)");
            _contents.Add(d.Id, content);
        }

        return content;
    }

    // ---------------------------------------------------------------- 布局树操作

    private void PlaceAtSide(LayoutAnchorable a, DockSide side, double ratio, string? targetId)
    {
        var root = _manager.Layout;
        Detach(a);

        if (side == DockSide.Tab)
        {
            var target = targetId != null ? FindAnchorable(targetId) : null;
            if (target?.Parent is LayoutAnchorablePane targetPane)
            {
                targetPane.Children.Add(a);
                targetPane.SelectedContentIndex = targetPane.Children.Count - 1;
                root.CollectGarbage();
                return;
            }

            _log.Warn(LayoutSource, $"标签组目标 {targetId ?? "(空)"} 不可用,改为右侧停靠");
            side = DockSide.Right;
        }

        var rootPanel = root.RootPanel;
        var pane = new LayoutAnchorablePane(a);

        switch (side)
        {
            case DockSide.Left:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                rootPanel.Children.Insert(0, pane);
                break;

            case DockSide.Right:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                rootPanel.Children.Add(pane);
                break;

            case DockSide.Top:
            case DockSide.Bottom:
            {
                var column = EnsureCenterColumn();
                pane.DockHeight = DockLengthFor(horizontal: false, ratio);
                if (side == DockSide.Top)
                    column.Children.Insert(0, pane);
                else
                    column.Children.Add(pane);
                break;
            }
        }

        if (a.ContentId != null)
            _ratios[a.ContentId] = ratio;
        root.CollectGarbage();
    }

    /// <summary>找到包含文档区的中央列;若中央区不是垂直面板,则就地包一层。</summary>
    private LayoutPanel EnsureCenterColumn()
    {
        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel)
                     ?? throw new InvalidOperationException("布局中找不到主内容区");

        if (center is LayoutPanel { Orientation: Orientation.Vertical } column)
            return column;

        var idx = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(idx);
        var wrap = new LayoutPanel { Orientation = Orientation.Vertical };
        wrap.DockWidth = GetDockLength(center, horizontal: true);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(idx, wrap);
        return wrap;
    }

    private static ILayoutPanelElement? FindCenterChild(LayoutPanel rootPanel)
        => rootPanel.Children.FirstOrDefault(c =>
            c is LayoutDocumentPane ||
            c.Descendents().OfType<LayoutDocumentPane>().Any());

    private void Detach(LayoutAnchorable a)
    {
        if (a.IsHidden)
        {
            _manager.Layout.Hidden.Remove(a);
            return;
        }

        if (a.Parent is ILayoutContainer container)
            container.RemoveChild(a);
    }

    private void ApplyRatio(LayoutAnchorable a, DockSide side, double ratio)
    {
        var horizontal = side is DockSide.Left or DockSide.Right;
        var panel = horizontal
            ? _manager.Layout.RootPanel
            : EnsureCenterColumn();

        var child = ChildContaining(panel, a);
        if (child == null)
        {
            _log.Warn(LayoutSource, "未能定位窗口所在的布局分区,比例未调整");
            return;
        }

        SetDockLength(child, horizontal, DockLengthFor(horizontal, ratio));
        if (a.ContentId != null)
            _ratios[a.ContentId] = ratio;
    }

    /// <summary>
    /// 目标尺寸的表达:主窗体已量得实际尺寸时用像素(AvalonDock 对侧窗格
    /// 的原生语义),否则先用星值占位,待首次排布后由 ReapplyRatios 修正。
    /// </summary>
    private GridLength DockLengthFor(bool horizontal, double ratio)
    {
        var total = horizontal ? _manager.ActualWidth : _manager.ActualHeight;
        return total > 0
            ? new GridLength(Math.Max(ratio * total, 25), GridUnitType.Pixel)
            : Star(ratio);
    }

    private void ScheduleReapplyRatios()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            () =>
            {
                using (Suppress())
                {
                    ReapplyRatios();
                }
            });

    /// <summary>
    /// 按记录的“占主程序窗体百分比”重设各停靠分区尺寸(W-05)。
    /// 恢复布局后的首次调用改为反向采集:以布局文件里的尺寸为准更新比例记录。
    /// </summary>
    private void ReapplyRatios()
    {
        if (_manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return;

        if (_seedRatiosFromLayout)
        {
            _seedRatiosFromLayout = false;
            foreach (var d in _descriptors)
            {
                var s = ComputeState(d.Id);
                if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab, Ratio: > 0 })
                    _ratios[d.Id] = s.Ratio;
            }

            return;
        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel);
        if (center == null)
            return;

        var rootHorizontal = rootPanel.Orientation == Orientation.Horizontal;
        foreach (var child in rootPanel.Children.Where(c => !ReferenceEquals(c, center)))
        {
            if (RatioOfSubtree(child) is { } ratio)
                SetDockLength(child, rootHorizontal, DockLengthFor(rootHorizontal, ratio));
        }

        if (center is LayoutPanel column)
        {
            var innerDoc = column.Children.FirstOrDefault(c =>
                c is LayoutDocumentPane || c.Descendents().OfType<LayoutDocumentPane>().Any());
            var columnHorizontal = column.Orientation == Orientation.Horizontal;
            foreach (var child in column.Children.Where(c => !ReferenceEquals(c, innerDoc)))
            {
                if (RatioOfSubtree(child) is { } ratio)
                    SetDockLength(child, columnHorizontal, DockLengthFor(columnHorizontal, ratio));
            }
        }
    }

    /// <summary>分区内第一个已注册窗口的目标比例(分区尺寸由其代表)。</summary>
    private double? RatioOfSubtree(ILayoutPanelElement subtree)
    {
        var anchorables = subtree is LayoutAnchorable self
            ? new[] { self }.AsEnumerable()
            : subtree.Descendents().OfType<LayoutAnchorable>();
        foreach (var a in anchorables)
        {
            if (a.ContentId != null && _ratios.TryGetValue(a.ContentId, out var r))
                return r;
        }

        return null;
    }

    // ---------------------------------------------------------------- 状态检测

    private sealed record WinState(bool Visible, bool Floating, DockSide? Side, string? TabTarget, double Ratio);

    private WinState ComputeState(string id)
    {
        var a = FindAnchorable(id);
        if (a == null || a.IsHidden)
            return new WinState(false, false, null, null, 0);

        if (IsFloating(a))
            return new WinState(true, true, null, null, 0);

        var side = DetectSide(a);
        var tabTarget = side == null
            ? null
            : (a.Parent as LayoutAnchorablePane)?.Children
                .FirstOrDefault(c => !ReferenceEquals(c, a) && c.ContentId != null && _byId.ContainsKey(c.ContentId))
                ?.ContentId;

        return new WinState(true, false, side, tabTarget, DetectRatio(a, side));
    }

    private static bool IsFloating(LayoutAnchorable a)
    {
        for (ILayoutContainer? p = a.Parent; p != null; p = (p as ILayoutElement)?.Parent)
        {
            if (p is LayoutFloatingWindow)
                return true;
        }

        return false;
    }

    private DockSide? DetectSide(LayoutAnchorable a)
    {
        if (a.IsHidden || IsFloating(a))
            return null;

        var rootPanel = _manager.Layout.RootPanel;
        var rootChild = ChildContaining(rootPanel, a);
        var centerChild = FindCenterChild(rootPanel);
        if (rootChild == null || centerChild == null)
            return null;

        var horizontal = rootPanel.Orientation == Orientation.Horizontal;
        if (!ReferenceEquals(rootChild, centerChild))
        {
            var i = rootPanel.Children.IndexOf(rootChild);
            var c = rootPanel.Children.IndexOf(centerChild);
            return horizontal
                ? (i < c ? DockSide.Left : DockSide.Right)
                : (i < c ? DockSide.Top : DockSide.Bottom);
        }

        // 与文档区同列:判断在文档区上方还是下方
        if (centerChild is LayoutPanel column)
        {
            var inner = ChildContaining(column, a);
            var innerDoc = column.Children.FirstOrDefault(c =>
                c is LayoutDocumentPane || c.Descendents().OfType<LayoutDocumentPane>().Any());
            if (inner == null || innerDoc == null)
                return null;

            var i = column.Children.IndexOf(inner);
            var c = column.Children.IndexOf(innerDoc);
            return column.Orientation == Orientation.Vertical
                ? (i < c ? DockSide.Top : DockSide.Bottom)
                : (i < c ? DockSide.Left : DockSide.Right);
        }

        return null;
    }

    private double DetectRatio(LayoutAnchorable a, DockSide? side)
    {
        if (side == null)
            return 0;

        var pane = a.Parent as ILayoutElement;
        if (pane == null)
            return 0;

        var fe = FindControlFor(pane);
        if (fe == null || _manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return 0;

        var ratio = side is DockSide.Left or DockSide.Right
            ? fe.ActualWidth / _manager.ActualWidth
            : fe.ActualHeight / _manager.ActualHeight;
        return Math.Round(ratio, 2);
    }

    private static ILayoutPanelElement? ChildContaining(LayoutPanel panel, ILayoutElement element)
    {
        ILayoutElement current = element;
        while (current.Parent != null && !ReferenceEquals(current.Parent, panel))
            current = current.Parent;
        return ReferenceEquals(current.Parent, panel) ? current as ILayoutPanelElement : null;
    }

    private FrameworkElement? FindControlFor(ILayoutElement model)
        => FindVisualDescendants(_manager)
            .FirstOrDefault(fe => fe is ILayoutControl lc && ReferenceEquals(lc.Model, model));

    private static IEnumerable<FrameworkElement> FindVisualDescendants(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
                yield return fe;
            foreach (var g in FindVisualDescendants(child))
                yield return g;
        }
    }

    private LayoutAnchorable? FindAnchorable(string id)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Concat(_manager.Layout.Hidden)
            .FirstOrDefault(a => string.Equals(a.ContentId, id, StringComparison.OrdinalIgnoreCase));

    private LayoutAnchorable FindRequired(string id)
    {
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        return FindAnchorable(id)
               ?? throw new InvalidOperationException($"窗口 {id} 不在当前布局中");
    }

    // ---------------------------------------------------------------- 布局事件 → 指令(W-10)

    private void AttachLayout()
    {
        if (_attachedRoot != null)
            _attachedRoot.Updated -= OnLayoutUpdated;

        _attachedRoot = _manager.Layout;
        _attachedRoot.Updated += OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_suppress > 0)
            return;

        // 手势进行中持续触发 → 去抖,静默 500ms 后视为动作结束
        _debounce.Stop();
        _debounce.Start();
    }

    private void EmitLayoutDiffs()
    {
        if (_suppress > 0)
            return;

        var now = ComputeAllStates();
        foreach (var d in _descriptors)
        {
            if (!_baseline.TryGetValue(d.Id, out var was))
                continue;
            var cur = now[d.Id];

            if (was.Visible && !cur.Visible)
            {
                Emit($"win.hide name={d.Id}");
                continue;
            }

            if (!was.Visible && cur.Visible)
                Emit($"win.show name={d.Id}");

            if (!cur.Visible)
                continue;

            if (!was.Floating && cur.Floating)
            {
                Emit($"win.float name={d.Id}");
                continue;
            }

            if (cur.Floating || cur.Side == null)
                continue;

            var dockChanged = was.Floating || was.Side != cur.Side ||
                              !string.Equals(was.TabTarget, cur.TabTarget, StringComparison.OrdinalIgnoreCase);
            if (dockChanged)
            {
                Emit(cur.TabTarget != null
                    ? $"win.dock name={d.Id} pos=tab target={cur.TabTarget}"
                    : $"win.dock name={d.Id} pos={SideText(cur.Side.Value)} ratio={FormatRatio(cur.Ratio)}");
            }
            else if (was.Ratio > 0 && cur.Ratio > 0 && Math.Abs(was.Ratio - cur.Ratio) > RatioEpsilon)
            {
                // was.Ratio == 0 说明基线建立时尚未完成渲染(尺寸未知),不视为用户手势
                Emit($"win.ratio name={d.Id} value={FormatRatio(cur.Ratio)}");
            }
        }

        // 用户拖拽分隔条 / 重新停靠后,同步更新比例记录(W-05)
        foreach (var (id, s) in now)
        {
            if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab, Ratio: > 0 })
                _ratios[id] = s.Ratio;
        }

        _baseline = now;
    }

    private Dictionary<string, WinState> ComputeAllStates()
        => _descriptors.ToDictionary(d => d.Id, d => ComputeState(d.Id), StringComparer.OrdinalIgnoreCase);

    private void Emit(string commandText)
    {
        // 以指令回显类别落管道(L-03):控制台按 "[layout] > ..." 样式渲染,可一键屏蔽(C-04)
        _log.Info(Core.Commands.CommandBus.EchoCategoryPrefix + LayoutSource, commandText);
        CommandGenerated?.Invoke(this, new ShellCommandEventArgs
        {
            CommandText = commandText,
            Source = LayoutSource,
        });
    }

    /// <summary>防再入(§14.2 清单 4):指令/API 驱动的布局变更不回声成新指令。</summary>
    private IDisposable Suppress()
    {
        _suppress++;
        _debounce.Stop();
        return new SuppressScope(this);
    }

    private void RebaseSoon()
        => _manager.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, // 渲染完成后再取快照,保证比例已可测量
            () => _baseline = ComputeAllStates());

    private sealed class SuppressScope : IDisposable
    {
        private DockingHost? _host;

        public SuppressScope(DockingHost host) => _host = host;

        public void Dispose()
        {
            if (_host == null)
                return;
            var host = _host;
            _host = null;
            host._suppress--;
            if (host._suppress == 0)
                host.RebaseSoon(); // 渲染一拍后重建基线,吸收本次程序化变更
        }
    }

    // ---------------------------------------------------------------- 工具

    // AvalonDock 的 ILayoutPositionableElement 为 internal,
    // DockWidth / DockHeight 只能经各具体面板类型访问 —— 用反射统一读写
    private static GridLength GetDockLength(ILayoutElement element, bool horizontal)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        return prop?.GetValue(element) is GridLength len
            ? len
            : new GridLength(1, GridUnitType.Star);
    }

    private static void SetDockLength(ILayoutElement element, bool horizontal, GridLength value)
    {
        var prop = element.GetType().GetProperty(horizontal ? "DockWidth" : "DockHeight");
        if (prop != null && prop.CanWrite)
            prop.SetValue(element, value);
    }

    private static GridLength Star(double value)
        => new(Math.Max(value, 0.02), GridUnitType.Star);

    private static string SideText(DockSide side) => side switch
    {
        DockSide.Left => "left",
        DockSide.Right => "right",
        DockSide.Top => "top",
        DockSide.Bottom => "bottom",
        _ => "tab",
    };

    private static string FormatRatio(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
