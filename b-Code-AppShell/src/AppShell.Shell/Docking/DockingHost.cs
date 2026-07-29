using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
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
    private const double MaximumSideAllocation = 0.8;
    private const string LayoutSource = "layout";
    private const double RatioEpsilon = 0.02;
    private const string PlacementSettingsKey = "layout.placements";
    private const string HiddenCenterMetadataPrefix = "AppShell.HiddenCenter.v1:";

    private readonly DockingManager _manager;
    private readonly ILayoutStore _store;
    private readonly IShellLog _log;
    private readonly ISettingsService? _settings;
    private readonly List<ToolWindowDescriptor> _descriptors = new();
    private readonly Dictionary<string, ToolWindowDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayoutDocument> _centerDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenCenterIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, OrphanPlacement> _orphanPlacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;

    private Dictionary<string, WinState> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private LayoutRoot? _attachedRoot;
    private int _suppress;
    private string? _maximizedId;
    private string? _layoutBeforeMaximize;
    private bool _centerRepairPending;
    private bool _presentationRefreshPending;

    // W-05 比例语义:AvalonDock 对与文档区同面板的侧窗格采用像素语义
    // (LayoutPanelControl.OnFixChildrenDockLengths 会把星值固化为像素),
    // “占主程序窗体百分比”由封装层维护:记录目标比例,在首次排布后与
    // 窗体缩放后重新按比例施加像素尺寸。
    private readonly Dictionary<string, double> _ratios = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preserveDefaultRatioOnSeed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _resizeDebounce;
    private bool _windowResizePending;

    public DockingHost(
        DockingManager manager,
        IEnumerable<ToolWindowDescriptor> windows,
        ILayoutStore store,
        IShellLog log,
        ISettingsService? settings = null)
    {
        _manager = manager;
        _store = store;
        _log = log;
        _settings = settings;

        foreach (var d in windows)
        {
            if (_byId.ContainsKey(d.Id))
                throw new InvalidOperationException($"工具窗口 Id 冲突: {d.Id}(禁止静默覆盖,§5.3)");
            _descriptors.Add(d);
            _byId.Add(d.Id, d);
            _owners[d.Id] = "framework";
            _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
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
            _manager.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                () =>
                {
                    _windowResizePending = false;
                    _baseline = ComputeAllStates();
                });
        };
        _manager.SizeChanged += (_, _) =>
        {
            _windowResizePending = true;
            _debounce.Stop();
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    /// <summary>当前布局方案名(状态栏显示用)。</summary>
    public string CurrentLayoutName { get; private set; } = "默认";

    public string? MaximizedId => _maximizedId;

    /// <summary>true 表示下一次比例处理应从已恢复的布局反向采集比例,而非施加记录值。</summary>
    private bool _seedRatiosFromLayout;

    public event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    public event EventHandler? WindowsChanged;

    /// <summary>已注册窗口(视图菜单构建用)。</summary>
    public IReadOnlyList<ToolWindowDescriptor> Descriptors => _descriptors;

    // ---------------------------------------------------------------- 启动/退出

    /// <summary>启动时调用:恢复上次布局,失败或不存在则构建默认布局(W-07 / N-06)。</summary>
    public void Initialize()
    {
        LoadOrphanPlacements();
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
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
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

            ApplyStoredCenterVisibility();
            EnsureCentralWorkspace();
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
            _store.WriteCurrent(_layoutBeforeMaximize ?? SerializeLayout());
            SavePlacements();
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
                return new ToolWindowInfo(
                    d.Id, d.Title, s.Visible, s.Floating, s.Side,
                    s.Visible && !s.Floating && s.Ratio > 0 ? s.Ratio : null,
                    _owners.GetValueOrDefault(d.Id, "framework"));
            })
            .ToList();

    public void Show(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                ShowCenterDocument(document);
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (anchorable.IsHidden)
                    anchorable.Show();
                anchorable.IsSelected = true;
                anchorable.IsActive = true;
            }
            EnsureCentralWorkspace();
            ReapplyRatios();
        }
    }

    public void Hide(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，不能隐藏");
                }
                else
                {
                    DetachDocument(document);
                    _hiddenCenterIds.Add(id);
                    ScheduleCenterDocumentPresentation();
                }
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (!anchorable.IsHidden)
                    anchorable.Hide();
            }
            EnsureCentralWorkspace();
        }
    }

    public void Float(string id)
    {
        RestoreLayoutFromMaximized();
        EnsureRegistered(id);
        using (Suppress())
        {
            var document = FindCenterDocument(id);
            if (document != null)
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，不能浮动");
                }
                else
                {
                    ShowCenterDocument(document);
                    if (!IsFloating(document))
                        document.Float();
                }
            }
            else
            {
                var anchorable = FindRequiredAnchorable(id);
                if (anchorable.IsHidden)
                    anchorable.Show();
                if (!IsFloating(anchorable))
                    anchorable.Float();
            }
            EnsureCentralWorkspace();
        }
    }

    public void Dock(string id, DockSide side, double? ratio = null, string? targetId = null)
    {
        RestoreLayoutFromMaximized();
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (ratio is { } providedRatio &&
            (!double.IsFinite(providedRatio) || providedRatio is <= 0 or >= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");
        }
        using (Suppress())
        {
            if (side == DockSide.Center ||
                side == DockSide.Tab && targetId != null && IsCenterContent(targetId))
            {
                if (UsesDocumentIdentity(descriptor))
                {
                    ShowCenterDocument(MoveToCenterDocument(descriptor));
                }
                else
                {
                    ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
                }
            }
            else
            {
                if (IsPrimaryCommandDocument(id))
                {
                    _log.Warn(LayoutSource, "命令集是主窗口，只能停靠在中央主区");
                    ShowCenterDocument(MoveToCenterDocument(descriptor));
                }
                else
                {
                    var anchorable = MoveToAnchorable(descriptor);
                    PlaceAtSide(anchorable, side, ratio ?? descriptor.DefaultRatio, targetId);
                    anchorable.IsSelected = true;
                }
            }
            EnsureCentralWorkspace();
        }
    }

    public void SetRatio(string id, double ratio)
    {
        RestoreLayoutFromMaximized();
        if (!double.IsFinite(ratio) || ratio is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(ratio), "比例须严格位于 (0,1)");

        if (FindCenterDocument(id) != null)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 位于中央主区，不支持比例调整");
            return;
        }

        var a = FindRequiredAnchorable(id);
        var side = DetectSide(a);
        if (side is null or DockSide.Tab or DockSide.Center)
        {
            _log.Warn(LayoutSource, $"窗口 {id} 当前不在可调整比例的四边停靠区");
            return;
        }

        using (Suppress())
        {
            ApplyRatio(a, side.Value, ratio);
            ReapplyRatios();
        }
    }

    public void ResetWindow(string id)
    {
        RestoreLayoutFromMaximized();
        var d = _byId[id];
        using (Suppress())
        {
            if (UsesDocumentIdentity(d))
            {
                ShowCenterDocument(MoveToCenterDocument(d));
            }
            else if (d.DefaultSide == DockSide.Tab && d.DefaultTabTarget != null &&
                     FindCenterDocument(d.DefaultTabTarget) != null)
            {
                ShowAnchorableAsCenterPage(MoveToAnchorable(d));
            }
            else
            {
                var anchorable = MoveToAnchorable(d);
                PlaceAtSide(anchorable, d.DefaultSide, d.DefaultRatio, d.DefaultTabTarget);
                anchorable.IsSelected = true;
            }
            EnsureCentralWorkspace();
        }
    }

    public void ResetLayout()
    {
        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            BuildDefaultLayout();
            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = "默认";
            _seedRatiosFromLayout = false;
            foreach (var d in _descriptors)
                _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
        }

        ScheduleReapplyRatios();
        RebaseSoon();
    }

    public void SaveLayout(string name)
    {
        _store.WriteNamed(name, _layoutBeforeMaximize ?? SerializeLayout());
        CurrentLayoutName = name;
        _log.Info(LayoutSource, $"布局方案已保存: {name}");
    }

    public bool LoadLayout(string name)
    {
        RestoreLayoutFromMaximized();
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
                if (!LayoutHasMainDocumentPane())
                    throw new InvalidOperationException("布局中缺少中央主文档区");
                EnsureRegisteredWindows();
                EnsureCentralWorkspace();
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
                EnsureCentralWorkspace();
                AttachLayout();
                CurrentLayoutName = "默认";
            }

            RebaseSoon();
            return false;
        }
    }

    public IReadOnlyList<string> ListLayouts() => _store.ListNamed();

    public void RegisterWindow(ToolWindowDescriptor descriptor, string owner)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_byId.ContainsKey(descriptor.Id))
        {
            throw new InvalidOperationException(
                $"工具窗口 Id 冲突: {descriptor.Id}(禁止静默覆盖,§5.3)");
        }

        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            _descriptors.Add(descriptor);
            _byId.Add(descriptor.Id, descriptor);
            _owners[descriptor.Id] = owner;
            var placement = TakeOrphanPlacement(descriptor.Id);
            var side = placement?.Side ?? descriptor.DefaultSide;
            var target = placement?.TabTarget ?? descriptor.DefaultTabTarget;
            var hidden = placement?.Hidden ??
                         (_hiddenCenterIds.Contains(descriptor.Id) || !descriptor.DefaultVisible);
            if (side == DockSide.Center ||
                side == DockSide.Tab && target != null && FindCenterDocument(target) != null)
            {
                var select = placement?.Selected ?? true;
                if (UsesDocumentIdentity(descriptor))
                {
                    var document = MoveToCenterDocument(descriptor);
                    if (hidden && !IsPrimaryCommandDocument(descriptor.Id))
                    {
                        DetachDocument(document);
                        _hiddenCenterIds.Add(descriptor.Id);
                    }
                    else
                    {
                        ShowCenterDocument(document, placement?.CenterIndex, select);
                    }
                }
                else
                {
                    var anchorable = MoveToAnchorable(descriptor);
                    ShowAnchorableAsCenterPage(anchorable, placement?.CenterIndex, select);
                    if (hidden)
                        anchorable.Hide();
                }
            }
            else
            {
                var anchorable = MoveToAnchorable(descriptor);
                PlaceAtSide(anchorable, side, placement?.Ratio ?? descriptor.DefaultRatio, target);
                if (hidden)
                    anchorable.Hide();
            }
            EnsureCentralWorkspace();
        }

        ScheduleReapplyRatios();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterWindow(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            return;

        RestoreLayoutFromMaximized();
        using (Suppress())
        {
            var anchorable = FindAnchorable(id);
            if (anchorable != null)
            {
                Detach(anchorable);
                anchorable.Content = null;
            }
            var document = FindCenterDocument(id);
            if (document != null)
            {
                DetachDocument(document);
                document.Content = null;
                _centerDocuments.Remove(id);
                _hiddenCenterIds.Remove(id);
                ScheduleCenterDocumentPresentation();
            }

            _descriptors.Remove(descriptor);
            _byId.Remove(id);
            _ratios.Remove(id);
            _baseline.Remove(id);
            _preserveDefaultRatioOnSeed.Remove(id);
            _owners.Remove(id);
            if (_contents.Remove(id, out var content))
                TryDispose(content, id);
            _manager.Layout.CollectGarbage();
            EnsureCentralWorkspace();
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnregisterOwner(string owner)
    {
        RestoreLayoutFromMaximized();
        foreach (var id in _owners
                     .Where(pair => pair.Value.Equals(owner, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            UnregisterWindow(id);
        }
    }

    public void MaximizeWindow(string id)
    {
        if (_maximizedId != null && _maximizedId.Equals(id, StringComparison.OrdinalIgnoreCase))
            return;
        RestoreLayoutFromMaximized();
        if (!_byId.ContainsKey(id))
            throw new ArgumentException($"未注册的工具窗口: {id}", nameof(id));

        using (Suppress())
        {
            _layoutBeforeMaximize = SerializeLayout();
            BuildMaximizedLayout(id);
            AttachLayout();
            _maximizedId = id;
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    public void RestoreLayoutFromMaximized()
    {
        if (_maximizedId == null || _layoutBeforeMaximize == null)
            return;

        using (Suppress())
        {
            var xml = _layoutBeforeMaximize;
            ApplyLayoutXml(xml);
            if (!LayoutHasMainDocumentPane())
                throw new InvalidOperationException("布局中缺少中央主文档区");
            EnsureRegisteredWindows();
            EnsureCentralWorkspace();
            AttachLayout();
            _maximizedId = null;
            _layoutBeforeMaximize = null;
            _seedRatiosFromLayout = true;
        }

        WindowsChanged?.Invoke(this, EventArgs.Empty);
        RebaseSoon();
    }

    // ---------------------------------------------------------------- 布局构建与序列化

    private void BuildDefaultLayout()
    {
        _preserveDefaultRatioOnSeed.Clear();
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        var docPane = new LayoutDocumentPane();

        // 中央主区使用 AvalonDock 原生文档窗格。命令集或消费方显式 Center 窗口
        // 直接成为主文档，避免“空文档背景 + 横向工具窗”被 AvalonDock 重新分配宽度。
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
            .Where(d => d.DefaultSide is not DockSide.Tab and not DockSide.Center)
            .GroupBy(d => d.DefaultSide)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bySide.TryGetValue(DockSide.Left, out var lefts))
        {
            leftRatio = lefts.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(lefts);
            pane.DockWidth = Star(leftRatio);
            rootPanel.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Right, out var rights))
        {
            rightRatio = rights.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(rights);
            pane.DockWidth = Star(rightRatio);
            rootPanel.Children.Add(pane);
        }

        if (bySide.TryGetValue(DockSide.Top, out var tops))
        {
            topRatio = tops.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(tops);
            pane.DockHeight = Star(topRatio);
            centerColumn.Children.Insert(0, pane);
        }

        if (bySide.TryGetValue(DockSide.Bottom, out var bottoms))
        {
            bottomRatio = bottoms.Max(d => NormalizeRatio(d.DefaultRatio, 0.25));
            var pane = MakeSidePane(bottoms);
            pane.DockHeight = Star(bottomRatio);
            centerColumn.Children.Add(pane);
        }

        foreach (var descriptor in _descriptors
                     .Where(d => d.DefaultSide == DockSide.Center)
                     .OrderByDescending(d => IsPrimaryCommandDocument(d.Id)))
        {
            var document = CreateDocument(descriptor);
            if (descriptor.DefaultVisible || IsPrimaryCommandDocument(descriptor.Id))
                docPane.Children.Add(document);
            else
                _hiddenCenterIds.Add(descriptor.Id);
        }

        centerColumn.DockWidth = Star(Math.Max(1 - leftRatio - rightRatio, 0.1));
        docPane.DockHeight = Star(Math.Max(1 - topRatio - bottomRatio, 0.1));

        // 第二遍:并入标签组的窗口(DefaultSide = Tab)
        foreach (var d in _descriptors.Where(d => d.DefaultSide == DockSide.Tab))
        {
            var documentTarget = d.DefaultTabTarget != null
                ? FindCenterDocument(d.DefaultTabTarget)
                : null;
            if (documentTarget?.Parent is LayoutDocumentPane documentPane)
            {
                documentPane.Children.Add(CreateAnchorable(d));
                continue;
            }

            var a = CreateAnchorable(d);
            var target = d.DefaultTabTarget != null ? FindAnchorable(d.DefaultTabTarget) : null;
            if (target?.Parent is LayoutAnchorablePane tp)
            {
                tp.Children.Add(a);
            }
            else
            {
                _log.Warn(LayoutSource, $"窗口 {d.Id} 的默认标签组目标 {d.DefaultTabTarget ?? "(空)"} 不存在,改为右侧停靠");
                var pane = new LayoutAnchorablePane(a)
                {
                    DockWidth = Star(NormalizeRatio(d.DefaultRatio, 0.25)),
                };
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
        // A close/save can race the 500 ms gesture debounce. Preserve any tool page that the
        // user intentionally embedded in the central document pane.
        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
        }
        if (!LayoutHasMainDocumentPane())
            throw new InvalidOperationException("布局中必须且只能存在一个中央主文档区");

        using var writer = new StringWriter();
        new XmlLayoutSerializer(_manager).Serialize(writer);
        var document = XDocument.Parse(writer.ToString(), LoadOptions.PreserveWhitespace);
        var hiddenIds = _hiddenCenterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var metadata = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(hiddenIds));
        document.AddFirst(new XComment(HiddenCenterMetadataPrefix + metadata));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private void ApplyLayoutXml(string xml)
    {
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        xml = ExtractHiddenCenterMetadata(xml);
        var serializer = new XmlLayoutSerializer(_manager);
        serializer.LayoutSerializationCallback += (_, e) =>
        {
            var contentId = e.Model.ContentId;
            if (e.Model is LayoutDocument document &&
                contentId != null && _byId.TryGetValue(contentId, out var documentDescriptor))
            {
                if (!UsesDocumentIdentity(documentDescriptor))
                {
                    e.Cancel = true;
                    return;
                }
                document.CanClose = false;
                document.CanFloat = !IsPrimaryCommandDocument(contentId);
                document.Title = documentDescriptor.Title;
                e.Content = GetOrCreateContent(documentDescriptor);
                _centerDocuments[contentId] = document;
            }
            else if (e.Model is LayoutAnchorable anchorable &&
                     contentId != null && _byId.TryGetValue(contentId, out var d))
            {
                // 3.0 早期候选把 Center 做成与空文档区并排的工具窗。取消该旧节点，
                // EnsureRegisteredWindows 会把默认中央窗口迁入真正的文档主区。
                if (d.DefaultSide == DockSide.Center || IsPrimaryCommandDocument(contentId))
                {
                    e.Cancel = true;
                    return;
                }
                // Side tool pages may be dragged into the central document pane and back out.
                anchorable.CanDockAsTabbedDocument = true;
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

    private string ExtractHiddenCenterMetadata(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var metadata = document.Nodes()
            .OfType<XComment>()
            .FirstOrDefault(comment => comment.Value.StartsWith(
                HiddenCenterMetadataPrefix,
                StringComparison.Ordinal));
        if (metadata == null)
            return xml;

        metadata.Remove();
        try
        {
            var encoded = metadata.Value[HiddenCenterMetadataPrefix.Length..];
            var ids = JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded)) ?? [];
            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                _hiddenCenterIds.Add(id);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            _log.Warn(LayoutSource, $"隐藏中央页元数据无效,已按默认可见性恢复: {ex.Message}");
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>布局加载后补齐缺失的已注册窗口(旧布局文件兼容)。</summary>
    private void EnsureRegisteredWindows()
    {
        var missing = _descriptors
            .Where(d => FindAnchorable(d.Id) == null && FindCenterDocument(d.Id) == null)
            .ToArray();

        // 先补齐中央文档，再处理 Tab 跟随页，避免恢复结果依赖描述符声明顺序。
        foreach (var descriptor in missing.Where(UsesDocumentIdentity))
            EnsureRegisteredWindow(descriptor);
        foreach (var descriptor in missing.Where(d => !UsesDocumentIdentity(d)))
            EnsureRegisteredWindow(descriptor);
    }

    private void EnsureRegisteredWindow(ToolWindowDescriptor d)
    {
        if (FindAnchorable(d.Id) != null || FindCenterDocument(d.Id) != null)
            return;

        if (UsesDocumentIdentity(d))
        {
            var placement = _orphanPlacements.GetValueOrDefault(d.Id);
            var hidden = _hiddenCenterIds.Contains(d.Id)
                         || !d.DefaultVisible
                         || placement?.Hidden == true;
            var document = MoveToCenterDocument(d);
            if (hidden && !IsPrimaryCommandDocument(d.Id))
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(d.Id);
            }
            else
            {
                ShowCenterDocument(
                    document,
                    placement?.CenterIndex,
                    placement?.Selected ?? false);
            }
        }
        else if (d.DefaultSide == DockSide.Tab && d.DefaultTabTarget != null &&
                 FindCenterDocument(d.DefaultTabTarget) != null)
        {
            var anchorable = MoveToAnchorable(d);
            ShowAnchorableAsCenterPage(anchorable);
            if (!d.DefaultVisible)
                anchorable.Hide();
        }
        else
        {
            var anchorable = CreateAnchorable(d);
            PlaceAtSide(anchorable, d.DefaultSide, d.DefaultRatio, d.DefaultTabTarget);
            if (!d.DefaultVisible)
                anchorable.Hide();
        }
        _preserveDefaultRatioOnSeed.Add(d.Id);
    }

    private bool LayoutHasMainDocumentPane()
    {
        var panes = _manager.Layout.RootPanel.Descendents().OfType<LayoutDocumentPane>().ToList();
        return panes.Count == 1 && panes[0].Children.OfType<LayoutDocument>().All(document =>
            document.ContentId != null && _byId.ContainsKey(document.ContentId));
    }

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
        CanDockAsTabbedDocument = true,
    };

    private LayoutDocument CreateDocument(ToolWindowDescriptor descriptor)
    {
        var document = new LayoutDocument
        {
            ContentId = descriptor.Id,
            Title = descriptor.Title,
            Content = GetOrCreateContent(descriptor),
            CanClose = false,
            CanFloat = !IsPrimaryCommandDocument(descriptor.Id),
        };
        _centerDocuments[descriptor.Id] = document;
        return document;
    }

    private bool IsPrimaryCommandDocument(string id)
        => id.Equals(StandardWindowIds.Mcp, StringComparison.OrdinalIgnoreCase);

    private bool UsesDocumentIdentity(ToolWindowDescriptor descriptor)
        => IsPrimaryCommandDocument(descriptor.Id) || descriptor.DefaultSide == DockSide.Center;

    private LayoutDocumentPane FindMainDocumentPane()
        => _manager.Layout.RootPanel.Descendents()
               .OfType<LayoutDocumentPane>()
               .FirstOrDefault(pane => !IsInsideFloatingWindow(pane))
           ?? throw new InvalidOperationException("布局中找不到中央主文档区");

    private LayoutDocument? FindCenterDocument(string id)
    {
        if (_centerDocuments.TryGetValue(id, out var cached))
            return cached;

        var document = _manager.Layout.Descendents()
            .OfType<LayoutDocument>()
            .FirstOrDefault(item => string.Equals(item.ContentId, id, StringComparison.OrdinalIgnoreCase));
        if (document != null)
            _centerDocuments[id] = document;
        return document;
    }

    private LayoutDocument MoveToCenterDocument(ToolWindowDescriptor descriptor)
    {
        var anchorable = FindAnchorable(descriptor.Id);
        if (anchorable != null)
        {
            Detach(anchorable);
            anchorable.Content = null;
        }

        var document = FindCenterDocument(descriptor.Id) ?? CreateDocument(descriptor);
        _hiddenCenterIds.Remove(descriptor.Id);
        return document;
    }

    private void ShowCenterDocument(LayoutDocument document, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(document.Parent, pane))
        {
            DetachDocument(document);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, document);
        }
        _hiddenCenterIds.Remove(document.ContentId ?? string.Empty);
        if (select)
        {
            document.IsSelected = true;
            document.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, document))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private void ShowAnchorableAsCenterPage(LayoutAnchorable anchorable, int? index = null, bool select = true)
    {
        var pane = FindMainDocumentPane();
        var previousSelection = pane.SelectedContent;
        if (!ReferenceEquals(anchorable.Parent, pane))
        {
            Detach(anchorable);
            var targetIndex = Math.Clamp(index ?? pane.Children.Count, 0, pane.Children.Count);
            pane.Children.Insert(targetIndex, anchorable);
        }
        anchorable.CanDockAsTabbedDocument = true;
        if (select)
        {
            anchorable.IsSelected = true;
            anchorable.IsActive = true;
        }
        else if (previousSelection != null && !ReferenceEquals(previousSelection, anchorable))
        {
            previousSelection.IsSelected = true;
        }
        NormalizeMainDocumentSizing(pane);
        ScheduleCenterDocumentPresentation();
    }

    private bool IsCenterContent(string id)
        => FindCenterDocument(id)?.Parent is LayoutDocumentPane ||
           FindAnchorable(id) is { } anchorable && IsHostedInDocumentPane(anchorable);

    private static void NormalizeMainDocumentSizing(LayoutDocumentPane pane)
    {
        pane.DockWidth = Star(1);
        pane.DockHeight = Star(1);

        ILayoutElement current = pane;
        while (current.Parent is LayoutPanel panel && panel.Children.Count == 1)
        {
            SetDockLength(current, panel.Orientation == Orientation.Horizontal, Star(1));
            current = panel;
        }
    }

    private LayoutAnchorable MoveToAnchorable(ToolWindowDescriptor descriptor)
    {
        var document = FindCenterDocument(descriptor.Id);
        if (document != null)
        {
            DetachDocument(document);
            document.Content = null;
            _centerDocuments.Remove(descriptor.Id);
            _hiddenCenterIds.Remove(descriptor.Id);
        }
        return FindAnchorable(descriptor.Id) ?? CreateAnchorable(descriptor);
    }

    private static void DetachDocument(LayoutDocument document)
    {
        if (document.Parent is ILayoutContainer container)
            container.RemoveChild(document);
    }

    private void ScheduleCenterDocumentPresentation()
    {
        if (_presentationRefreshPending)
            return;
        _presentationRefreshPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _presentationRefreshPending = false;
            var mainPane = _manager.Layout.RootPanel.Descendents()
                .OfType<LayoutDocumentPane>()
                .FirstOrDefault(pane => !IsInsideFloatingWindow(pane));
            if (mainPane == null)
                return;

            foreach (var control in FindVisualDescendants(_manager).OfType<LayoutDocumentPaneControl>())
            {
                if (control is not ILayoutControl { Model: LayoutDocumentPane pane } ||
                    !ReferenceEquals(pane, mainPane))
                    continue;
                foreach (var tabs in FindVisualDescendants(control).OfType<DocumentPaneTabPanel>())
                    tabs.Visibility = Visibility.Visible;
            }
        });
    }

    private void EnsureRegistered(string id)
    {
        if (!_byId.TryGetValue(id, out var descriptor))
            throw new ArgumentException($"未注册的窗口: {id}", nameof(id));
        if (FindAnchorable(id) != null || FindCenterDocument(id) != null)
            return;

        if (UsesDocumentIdentity(descriptor))
        {
            ShowCenterDocument(MoveToCenterDocument(descriptor));
            return;
        }

        if (descriptor.DefaultSide == DockSide.Tab && descriptor.DefaultTabTarget != null &&
            FindCenterDocument(descriptor.DefaultTabTarget) != null)
        {
            ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
            return;
        }

        var anchorable = CreateAnchorable(descriptor);
        PlaceAtSide(anchorable, descriptor.DefaultSide, descriptor.DefaultRatio, descriptor.DefaultTabTarget);
    }

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

    private void BuildMaximizedLayout(string id)
    {
        _centerDocuments.Clear();
        _hiddenCenterIds.Clear();
        ILayoutPanelElement pane = _byId[id].DefaultSide == DockSide.Center
            ? new LayoutDocumentPane(CreateDocument(_byId[id]))
            : new LayoutAnchorablePane(CreateAnchorable(_byId[id]));
        var rootPanel = new LayoutPanel(pane) { Orientation = Orientation.Horizontal };
        _manager.Layout = new LayoutRoot { RootPanel = rootPanel };
    }

    private void TryDispose(object content, string id)
    {
        if (content is not IDisposable disposable)
            return;
        try
        {
            disposable.Dispose();
        }

        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"释放界面内容 {id} 失败: {ex.Message}");
        }
    }

    private void LoadOrphanPlacements()
    {
        var json = _settings?.Get(PlacementSettingsKey);
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            _orphanPlacements = JsonSerializer.Deserialize<Dictionary<string, OrphanPlacement>>(json)
                                ?? new Dictionary<string, OrphanPlacement>(StringComparer.OrdinalIgnoreCase);
            _orphanPlacements = new Dictionary<string, OrphanPlacement>(
                _orphanPlacements, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"读取延迟窗口位置失败: {ex.Message}");
            _orphanPlacements.Clear();
        }
    }

    private void SavePlacements()
    {
        if (_settings == null)
            return;
        var placements = new Dictionary<string, OrphanPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors)
        {
            var state = ComputeState(descriptor.Id);
            LayoutContent? centerContent = FindCenterDocument(descriptor.Id);
            centerContent ??= FindAnchorable(descriptor.Id) is { } anchorable && IsHostedInDocumentPane(anchorable)
                ? anchorable
                : null;
            var centerPane = centerContent?.Parent as LayoutDocumentPane;
            placements[descriptor.Id] = new OrphanPlacement(
                state.Side ?? descriptor.DefaultSide,
                state.Ratio > 0 ? state.Ratio : _ratios.GetValueOrDefault(descriptor.Id, descriptor.DefaultRatio),
                !state.Visible,
                state.TabTarget,
                centerPane == null ? null : centerPane.Children.IndexOf(centerContent!),
                centerPane == null ? null : ReferenceEquals(centerPane.SelectedContent, centerContent));
        }
        _settings.Set(PlacementSettingsKey, JsonSerializer.Serialize(placements));
    }

    private OrphanPlacement? TakeOrphanPlacement(string id)
    {
        if (!_orphanPlacements.Remove(id, out var placement))
            return null;
        return placement;
    }

    private void ApplyStoredCenterVisibility()
    {
        foreach (var (id, placement) in _orphanPlacements.ToArray())
        {
            var document = FindCenterDocument(id);
            if (document == null)
                continue;

            _orphanPlacements.Remove(id);
            if (placement.Hidden && !IsPrimaryCommandDocument(id))
            {
                DetachDocument(document);
                _hiddenCenterIds.Add(id);
            }
        }
    }

    private sealed record OrphanPlacement(
        DockSide Side,
        double Ratio,
        bool Hidden,
        string? TabTarget,
        int? CenterIndex = null,
        bool? Selected = null);

    // ---------------------------------------------------------------- 布局树操作

    private void PlaceAtSide(LayoutAnchorable a, DockSide side, double ratio, string? targetId)
    {
        var root = _manager.Layout;
        ToolWindowDescriptor? descriptor = null;
        if (a.ContentId != null)
            _byId.TryGetValue(a.ContentId, out descriptor);
        var fallbackRatio = descriptor?.DefaultRatio ?? 0.25;
        ratio = NormalizeRatio(ratio, fallbackRatio);

        if (side == DockSide.Center)
        {
            if (descriptor == null)
                throw new InvalidOperationException("未注册的窗口不能进入中央主区");
            if (UsesDocumentIdentity(descriptor))
                ShowCenterDocument(MoveToCenterDocument(descriptor));
            else
                ShowAnchorableAsCenterPage(MoveToAnchorable(descriptor));
            return;
        }

        Detach(a);

        if (side == DockSide.Tab)
        {
            var target = targetId != null ? FindAnchorable(targetId) : null;
            if (target?.Parent is LayoutAnchorablePane targetPane)
            {
                a.CanAutoHide = true;
                targetPane.Children.Add(a);
                targetPane.SelectedContentIndex = targetPane.Children.Count - 1;
                root.CollectGarbage();
                return;
            }

            _log.Warn(LayoutSource, $"标签组目标 {targetId ?? "(空)"} 不可用,改为右侧停靠");
            side = DockSide.Right;
        }

        // 默认布局会把同一侧的窗口合并成一个标签组；运行期模块注册也必须遵守
        // 相同拓扑，避免每注册一个右侧窗口就额外切出一块嵌套侧栏。
        var existingPane = FindSidePane(side, a);
        if (existingPane != null)
        {
            a.CanAutoHide = true;
            existingPane.Children.Add(a);
            existingPane.SelectedContentIndex = existingPane.Children.Count - 1;
            if (a.ContentId != null)
                _ratios[a.ContentId] = ratio;
            root.CollectGarbage();
            return;
        }

        var pane = new LayoutAnchorablePane(a);
        a.CanAutoHide = true;

        switch (side)
        {
            case DockSide.Left:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Insert(0, pane);
                break;

            case DockSide.Right:
                pane.DockWidth = DockLengthFor(horizontal: true, ratio);
                EnsureSideRootPanel().Children.Add(pane);
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

    private LayoutAnchorablePane? FindSidePane(DockSide side, LayoutAnchorable excluded)
        => _manager.Layout.Descendents()
            .OfType<LayoutAnchorable>()
            .Where(item => !ReferenceEquals(item, excluded)
                           && item.Parent is LayoutAnchorablePane
                           && !item.IsHidden
                           && !IsFloating(item))
            .FirstOrDefault(item => DetectSide(item) == side)
            ?.Parent as LayoutAnchorablePane;

    /// <summary>找到包含主文档区的中央列;若中央区不是垂直面板,则就地包一层。</summary>
    private LayoutPanel EnsureCenterColumn()
    {
        var document = _manager.Layout.Descendents().OfType<LayoutDocumentPane>().SingleOrDefault()
                       ?? throw new InvalidOperationException("布局中找不到中央主文档区");
        for (ILayoutContainer? parent = document.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutPanel { Orientation: Orientation.Vertical } vertical)
                return vertical;
        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel)
                     ?? throw new InvalidOperationException("布局中找不到中央主文档区");

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

    private LayoutPanel EnsureSideRootPanel()
    {
        var column = EnsureCenterColumn();
        if (column.Parent is LayoutPanel { Orientation: Orientation.Horizontal } horizontal)
            return horizontal;

        var rootPanel = _manager.Layout.RootPanel;
        var center = ChildContaining(rootPanel, column)
                     ?? throw new InvalidOperationException("布局中找不到中央列");
        if (rootPanel.Orientation == Orientation.Horizontal)
            return rootPanel;

        var index = rootPanel.Children.IndexOf(center);
        rootPanel.Children.RemoveAt(index);
        var wrap = new LayoutPanel { Orientation = Orientation.Horizontal };
        wrap.DockHeight = GetDockLength(center, horizontal: false);
        wrap.Children.Add(center);
        rootPanel.Children.Insert(index, wrap);
        return wrap;
    }

    private bool NeedsCentralWorkspaceRepair()
    {
        if (_maximizedId != null || !_byId.ContainsKey(StandardWindowIds.Mcp))
            return false;

        var document = FindCenterDocument(StandardWindowIds.Mcp);
        return document == null || document.Parent is not LayoutDocumentPane || IsFloating(document);
    }

    /// <summary>
    /// Keep the command catalog as the fixed main document. This repairs the early 3.0 candidate
    /// topology that placed it in a narrow anchorable pane beside an empty document background.
    /// </summary>
    private bool EnsureCentralWorkspace()
    {
        if (_maximizedId != null)
            return false;

        if (!_byId.TryGetValue(StandardWindowIds.Mcp, out var descriptor))
            return false;

        var existing = FindCenterDocument(StandardWindowIds.Mcp);
        var repaired = existing == null || existing.Parent is not LayoutDocumentPane || IsFloating(existing);
        if (!repaired)
        {
            NormalizeMainDocumentSizing((LayoutDocumentPane)existing!.Parent!);
            ScheduleCenterDocumentPresentation();
            return false;
        }

        ShowCenterDocument(MoveToCenterDocument(descriptor));
        _log.Info(LayoutSource, "命令集已恢复为中央主窗口");
        return true;
    }

    private void ScheduleCentralWorkspaceRepair()
    {
        if (_centerRepairPending || _suppress > 0)
            return;
        _centerRepairPending = true;
        _manager.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _centerRepairPending = false;
            if (_suppress > 0 || !NeedsCentralWorkspaceRepair())
                return;
            using (Suppress())
                EnsureCentralWorkspace();
        });
    }

    private static ILayoutPanelElement? FindCenterChild(LayoutPanel rootPanel)
        => rootPanel.Children.FirstOrDefault(c =>
            c is LayoutDocumentPane ||
            c.Descendents().OfType<LayoutDocumentPane>().Any());

    private static bool IsHostedInDocumentPane(LayoutAnchorable anchorable)
    {
        for (ILayoutContainer? parent = anchorable.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is LayoutDocumentPane)
                return true;
        }

        return false;
    }

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
        if (side == DockSide.Center)
            return;
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
        foreach (var item in child.Descendents().OfType<LayoutAnchorable>())
        {
            if (item.ContentId != null && _byId.ContainsKey(item.ContentId))
                _ratios[item.ContentId] = ratio;
        }
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
        if (_maximizedId != null)
            return;
        if (_manager.ActualWidth <= 0 || _manager.ActualHeight <= 0)
            return;

        if (_seedRatiosFromLayout)
        {
            _seedRatiosFromLayout = false;
            var addedAfterSavedLayout = _preserveDefaultRatioOnSeed.ToArray();
            foreach (var d in _descriptors)
            {
                if (_preserveDefaultRatioOnSeed.Contains(d.Id))
                    continue;
                var s = ComputeState(d.Id);
                if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab, Ratio: > 0 })
                    _ratios[d.Id] = s.Ratio;
            }

            _preserveDefaultRatioOnSeed.Clear();

            // Restored pixel panes and newly inserted star panes otherwise share incompatible
            // sizing semantics. Reapply each new descriptor's declared ratio after layout has
            // an actual size so it cannot collapse to 1-3%.
            foreach (var id in addedAfterSavedLayout)
            {
                var anchorable = FindAnchorable(id);
                var side = anchorable == null ? null : DetectSide(anchorable);
                if (anchorable != null && side is not null and not DockSide.Tab and not DockSide.Center)
                    ApplyRatio(anchorable, side.Value, _ratios[id]);
            }

        }

        var rootPanel = _manager.Layout.RootPanel;
        var center = FindCenterChild(rootPanel);
        if (center == null)
            return;

        var rootHorizontal = rootPanel.Orientation == Orientation.Horizontal;
        ReapplyPanelRatios(rootPanel, center, rootHorizontal);

        if (center is LayoutPanel column)
        {
            var innerDoc = column.Children.FirstOrDefault(c =>
                c is LayoutDocumentPane || c.Descendents().OfType<LayoutDocumentPane>().Any());
            var columnHorizontal = column.Orientation == Orientation.Horizontal;
            if (innerDoc != null)
                ReapplyPanelRatios(column, innerDoc, columnHorizontal);
        }
    }

    private void ReapplyPanelRatios(
        LayoutPanel panel,
        ILayoutPanelElement center,
        bool horizontal)
    {
        var sides = panel.Children
            .Where(child => !ReferenceEquals(child, center))
            .Select(child => (Child: child, Ratio: RatioOfSubtree(child)))
            .Where(item => item.Ratio is > 0)
            .Select(item => (item.Child, Ratio: item.Ratio!.Value))
            .ToList();
        var requested = sides.Sum(item => item.Ratio);
        var scale = requested > MaximumSideAllocation
            ? MaximumSideAllocation / requested
            : 1d;

        foreach (var (child, ratio) in sides)
        {
            var effective = ratio * scale;
            SetDockLength(child, horizontal, DockLengthFor(horizontal, effective));
            foreach (var anchorable in child.Descendents().OfType<LayoutAnchorable>())
            {
                if (anchorable.ContentId != null && _byId.ContainsKey(anchorable.ContentId))
                    _ratios[anchorable.ContentId] = effective;
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
        var document = FindCenterDocument(id);
        if (document != null)
        {
            if (_hiddenCenterIds.Contains(id) || document.Parent == null)
                return new WinState(false, false, null, null, 0);
            if (IsFloating(document))
                return new WinState(true, true, null, null, 0);
            return new WinState(true, false, DockSide.Center, null, 0);
        }

        var a = FindAnchorable(id);
        if (a == null || a.IsHidden)
            return new WinState(false, false, null, null, 0);

        if (IsFloating(a))
            return new WinState(true, true, null, null, 0);

        if (IsHostedInDocumentPane(a))
            return new WinState(true, false, DockSide.Center, null, 0);

        var side = DetectSide(a);
        var tabLeader = side == null
            ? null
            : (a.Parent as LayoutAnchorablePane)?.Children
                .FirstOrDefault(c => c.ContentId != null && _byId.ContainsKey(c.ContentId));
        // Normalize a tab group as one leader plus followers. Returning an arbitrary sibling for
        // every member creates circular targets (A -> B and B -> A), which cannot be replayed or
        // used as a stable pre-gesture recovery snapshot.
        var tabTarget = tabLeader == null || ReferenceEquals(tabLeader, a)
            ? null
            : tabLeader.ContentId;

        return new WinState(true, false, side, tabTarget, DetectRatio(a, side));
    }

    private static bool IsFloating(LayoutContent content)
        => IsInsideFloatingWindow(content);

    private static bool IsInsideFloatingWindow(ILayoutElement element)
    {
        for (ILayoutContainer? p = element.Parent; p != null; p = (p as ILayoutElement)?.Parent)
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

        ILayoutElement? centerAnchor = _manager.Layout.RootPanel.Descendents()
            .OfType<LayoutDocumentPane>()
            .SingleOrDefault();
        if (centerAnchor == null)
            return null;

        for (ILayoutContainer? parent = centerAnchor.Parent;
             parent != null;
             parent = (parent as ILayoutElement)?.Parent)
        {
            if (parent is not LayoutPanel panel)
                continue;
            var windowChild = ChildContaining(panel, a);
            var centerChild = ChildContaining(panel, centerAnchor);
            if (windowChild == null || centerChild == null || ReferenceEquals(windowChild, centerChild))
                continue;

            var before = panel.Children.IndexOf(windowChild) < panel.Children.IndexOf(centerChild);
            return panel.Orientation == Orientation.Horizontal
                ? before ? DockSide.Left : DockSide.Right
                : before ? DockSide.Top : DockSide.Bottom;
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

        if (side == DockSide.Center)
            return 0;
        var ratio = side is DockSide.Left or DockSide.Right
            ? fe.ActualWidth / _manager.ActualWidth
            : fe.ActualHeight / _manager.ActualHeight;
        ratio = Math.Round(ratio, 2);
        return double.IsFinite(ratio) && ratio is > 0 and < 1 ? ratio : 0;
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

    private LayoutAnchorable FindRequiredAnchorable(string id)
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
        if (_suppress > 0 || _windowResizePending)
            return;

        ScheduleCentralWorkspaceRepair();
        ScheduleCenterDocumentPresentation();

        // 手势进行中持续触发 → 去抖,静默 500ms 后视为动作结束
        _debounce.Stop();
        _debounce.Start();
    }

    private void EmitLayoutDiffs()
    {
        if (_suppress > 0)
            return;

        if (NeedsCentralWorkspaceRepair())
        {
            using (Suppress())
                EnsureCentralWorkspace();
            return;
        }

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
                    : cur.Side == DockSide.Center
                        ? $"win.dock name={d.Id} pos=center"
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
            if (s is { Visible: true, Floating: false, Side: not null and not DockSide.Tab and not DockSide.Center, Ratio: > 0 })
                _ratios[id] = s.Ratio;
        }

        using (Suppress())
            ReapplyRatios();

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
        DockSide.Center => "center",
        _ => "tab",
    };

    private static double NormalizeRatio(double ratio, double fallback)
    {
        if (double.IsFinite(ratio) && ratio is > 0 and < 1)
            return ratio;
        return double.IsFinite(fallback) && fallback is > 0 and < 1 ? fallback : 0.25;
    }

    private static string FormatRatio(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
