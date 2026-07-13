using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AppShell.Shell.Console;
using AppShell.Shell.Docking;
using AppShell.Shell.Themes;

namespace AppShell.Shell;

/// <summary>
/// 主程序窗体(Main Frame,§2):菜单栏 + 停靠系统容器 + 状态栏。
/// M2 起指令总线为一切操作的汇聚点:菜单项点击同样是发指令(S-02),
/// 控制台手输、脚本、布局手势与派生应用共用同一张指令注册表。
/// </summary>
public partial class ShellWindow : Window
{
    private readonly ShellConfig _config;
    private readonly IShellLog _log;
    private readonly DockingHost _docking;
    private readonly string _dataDirectory;
    private readonly CommandBus _bus;
    private readonly CommandHistory _history;
    private readonly ConsoleView _console;
    private readonly Table.TableView? _tableView;
    private readonly Resource.ResourceView? _resourceView;
    private readonly Panels.PanelManager _panels;
    private int _errorCount;

    public ShellWindow(
        ShellConfig config,
        ILayoutStore layoutStore,
        IShellLog log,
        ISettingsService settings,
        string dataDirectory)
    {
        InitializeComponent();

        _config = config;
        _log = log;
        _dataDirectory = dataDirectory;
        Title = $"{config.AppName} v{config.AppVersion}";

        // 主窗体边界先于停靠布局恢复:布局像素尺寸相对窗体记录,
        // 窗体尺寸一致才能做到“重启后布局原样恢复”(验收 1 / 3)
        RestoreWindowBounds();

        DockManager.Theme = new AppShellTheme();
        ApplyTabsTopStyle();

        // ---- 指令核心(§5):注册表 + 总线 + 历史 + 控制台
        var registry = new CommandRegistry();
        _bus = new CommandBus(registry, log)
        {
            UiContext = SynchronizationContext.Current,
            Confirmation = new MessageBoxConfirmation(this),
        };
        _history = new CommandHistory(
            Path.Combine(dataDirectory, "history.txt"),
            settings.GetInt("console.history", 500));
        _console = new ConsoleView(log, _bus, _history, settings.GetInt("console.buffer", 50_000));

        // 控制台窗口内容由 Shell 接管(§4.4 标准窗口;描述符位置仍由派生应用决定)
        TakeOverDescriptor("console", "控制台", DockSide.Bottom, 0.25, () => _console);

        // 数据服务已配置时,表窗口(§4.3)同样由 Shell 提供(M3)
        if (config.DataService != null)
        {
            _tableView = new Table.TableView(config.DataService, _bus, log);
            TakeOverDescriptor("table", "表窗口", DockSide.Bottom, 0.28, () => _tableView);
        }

        // 工作区已配置时,资源窗口(§4.6)由 Shell 提供(M4)
        if (config.Workspace != null)
        {
            _resourceView = new Resource.ResourceView(
                config.Workspace, _bus, log, config.OnResourceOpen,
                Path.Combine(dataDirectory, "workspace"));
            TakeOverDescriptor("resource", "资源窗口", DockSide.Left, 0.18, () => _resourceView);
        }

        // 控制窗口群(§4.5,M4):JSON + C# 通道合并,每个面板一个可停靠窗口
        _panels = new Panels.PanelManager(
            Path.Combine(dataDirectory, "panels"), config.Panels, _bus, log);
        _panels.RegisterWindows(config.ToolWindows);

        var mainContent = config.MainContent
                          ?? new PlaceholderPage(config.AppName, config.AppVersion, log);
        _docking = new DockingHost(DockManager, config.ToolWindows, mainContent, layoutStore, log);
        _docking.CommandGenerated += (_, e) =>
            Dispatcher.BeginInvoke(() => StatusLeft.Text = $"[{e.Source}] {e.CommandText}");
        _docking.Initialize();

        // ---- 内置指令组 + 派生应用自定义指令(冲突此时报错,§5.3)
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = this,
            Docking = _docking,
            Console = _console,
            History = _history,
            Settings = settings,
            Log = log,
            Bus = _bus,
            DataDirectory = dataDirectory,
            Data = config.DataService,
            Table = _tableView,
            Workspace = config.Workspace,
            Panels = _panels,
        });
        config.ConfigureCommands?.Invoke(registry);

        // S-03:状态栏左侧显示最近一条指令结果摘要;右侧错误计数
        _bus.Executed += (text, source, result) => Dispatcher.BeginInvoke(() =>
        {
            var summary = result.Message.Split('\n')[0];
            StatusLeft.Text = $"{(result.Success ? "✓" : "✗")} [{source}] {text} —— {summary}";
            UpdateStatusRight();
        });
        log.EntryAdded += (_, entry) =>
        {
            if (entry.Level >= ShellLogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
                Dispatcher.BeginInvoke(UpdateErrorBadge);
            }
        };

        // C-15:Ctrl + ` 全局聚焦控制台输入框
        var focusConsole = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(focusConsole, (_, _) => FocusConsole()));
        InputBindings.Add(new KeyBinding(focusConsole, Key.Oem3, ModifierKeys.Control));

        BuildMenus();
        UpdateStatusRight();

        Closing += OnShellClosing;
    }

    /// <summary>停靠系统门面。</summary>
    public IDockingService Docking => _docking;

    /// <summary>指令总线(派生应用 / 启动参数经此执行指令)。</summary>
    public CommandBus Commands => _bus;

    /// <summary>关于对话框文本(app.about)。</summary>
    public string AboutText =>
        $"{_config.AppName} v{_config.AppVersion}\n\n基于 AppShell 通用窗口框架模板\n.NET 8 + WPF + AvalonDock 4.72.1";

    /// <summary>
    /// 标准窗口内容接管:描述符的停靠位置仍由派生应用声明,内容工厂换成
    /// Shell 实现;未声明时按缺省位置强制注册(控制台是架构不变量 2 的落点)。
    /// </summary>
    private void TakeOverDescriptor(
        string id, string fallbackTitle, DockSide fallbackSide, double fallbackRatio, Func<object> factory)
    {
        var index = _config.ToolWindows.FindIndex(
            d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = id,
                Title = fallbackTitle,
                DefaultSide = fallbackSide,
                DefaultRatio = fallbackRatio,
                ContentFactory = factory,
            });
            return;
        }

        var d0 = _config.ToolWindows[index];
        _config.ToolWindows[index] = new ToolWindowDescriptor
        {
            Id = d0.Id,
            Title = d0.Title,
            DefaultSide = d0.DefaultSide,
            DefaultRatio = d0.DefaultRatio,
            DefaultTabTarget = d0.DefaultTabTarget,
            DefaultVisible = d0.DefaultVisible,
            IsSingleton = d0.IsSingleton,
            ContentFactory = factory,
        };
    }

    private void FocusConsole()
    {
        _docking.Show("console");
        _console.FocusInput();
    }

    private void UpdateErrorBadge()
    {
        StatusErrors.Text = $"错误: {_errorCount}";
        StatusErrors.Visibility = _errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStatusErrorsClick(object sender, MouseButtonEventArgs e)
    {
        FocusConsole();
        _console.FilterErrorsOnly();
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        _history.Save();
        SaveWindowBounds();
        _docking.SaveCurrentLayout();
    }

    // ---------------------------------------------------------------- 主窗体边界持久化

    private sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

    private string WindowBoundsPath => Path.Combine(_dataDirectory, "window.json");

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowBoundsPath))
                return;

            var b = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(WindowBoundsPath));
            if (b == null || b.Width < 200 || b.Height < 150)
                return;

            // 粗校验落点仍在虚拟屏幕范围内(多显示器拔除后不至于跑到屏外)
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;
            if (b.Left + b.Width < vLeft + 50 || b.Left > vRight - 50 ||
                b.Top < vTop - 10 || b.Top > vBottom - 50)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
            if (b.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"恢复主窗体位置失败: {ex.Message}");
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var r = RestoreBounds; // 最大化时记录还原后的边界
            var b = new WindowBounds(r.Left, r.Top, r.Width, r.Height,
                WindowState == WindowState.Maximized);
            File.WriteAllText(WindowBoundsPath, JsonSerializer.Serialize(b));
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"保存主窗体位置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 标签条置顶(W-04):以主题内置的窗格样式为基底(继承 ItemContainerStyle、
    /// 背景等全部视觉),仅覆盖 TabStripPlacement 与模板(标签行移到顶部)。
    /// 经 DockingManager.AnchorablePaneControlStyle 下发,主停靠区与浮动窗一并生效。
    /// v5 迁移注意:基底样式的资源键随主题版本变化,需同步调整。
    /// </summary>
    private void ApplyTabsTopStyle()
    {
        Style? baseStyle = null;
        try
        {
            var themeDict = new ResourceDictionary { Source = DockManager.Theme.GetResourceUri() };
            baseStyle = FindInDictionary(themeDict, "AvalonDockThemeVs2013AnchorablePaneControlStyle") as Style;
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"读取主题窗格样式失败: {ex.Message}");
        }

        if (baseStyle == null)
        {
            // 找不到基底样式时保持主题默认(标签在底部),不破坏可用性
            _log.Warn("shell", "未找到主题窗格基底样式,标签条置顶改造未生效");
            return;
        }

        var style = new Style(typeof(AvalonDock.Controls.LayoutAnchorablePaneControl), baseStyle);
        style.Setters.Add(new Setter(TabControl.TabStripPlacementProperty, Dock.Top));
        style.Setters.Add(new Setter(
            Control.TemplateProperty,
            (ControlTemplate)Resources["TabsTopAnchorablePaneTemplate"]));
        DockManager.AnchorablePaneControlStyle = style;
    }

    private static object? FindInDictionary(ResourceDictionary dict, object key)
    {
        if (dict.Contains(key))
            return dict[key];
        foreach (var merged in dict.MergedDictionaries)
        {
            if (FindInDictionary(merged, key) is { } found)
                return found;
        }

        return null;
    }

    // ---------------------------------------------------------------- 菜单(S-01)

    private void BuildMenus()
    {
        MainMenu.Items.Clear();

        // 文件
        var file = new MenuItem { Header = "文件(_F)" };
        file.Items.Add(Item("退出(_X)", "app.exit"));
        MainMenu.Items.Add(file);

        // 编辑(预留)
        var edit = new MenuItem { Header = "编辑(_E)", IsEnabled = false };
        MainMenu.Items.Add(edit);

        // 视图:全部窗口开关 + 重置布局(W-02)
        var view = new MenuItem { Header = "视图(_V)" };
        foreach (var d in _docking.Descriptors)
        {
            var sub = new MenuItem { Header = d.Title };
            sub.Items.Add(Item("显示", $"win.show name={d.Id}"));
            sub.Items.Add(Item("隐藏", $"win.hide name={d.Id}"));
            sub.Items.Add(Item("浮动", $"win.float name={d.Id}"));
            sub.Items.Add(Item("复位到默认位置", $"win.reset name={d.Id}"));
            view.Items.Add(sub);
        }

        view.Items.Add(new Separator());
        view.Items.Add(Item("重置默认布局", "layout.reset"));
        MainMenu.Items.Add(view);

        // 工具
        var tools = new MenuItem { Header = "工具(_T)" };
        tools.Items.Add(Item("打开数据目录", "app.opendata"));
        MainMenu.Items.Add(tools);

        // 帮助:指令手册 = help 的图形化版本(S-01)
        var help = new MenuItem { Header = "帮助(_H)" };
        var manual = new MenuItem { Header = "指令手册(_M)" };
        manual.Click += async (_, _) =>
        {
            FocusConsole();
            await _bus.ExecuteAsync("help", "UI");
        };
        help.Items.Add(manual);
        help.Items.Add(Item("关于(_A)", "app.about"));
        MainMenu.Items.Add(help);
    }

    /// <summary>菜单项点击同样是发指令(S-02):统一经总线分发、回显、留痕。</summary>
    private MenuItem Item(string header, string commandText)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => _ = _bus.ExecuteAsync(commandText, "UI");
        return mi;
    }

    private void UpdateStatusRight()
        => StatusRight.Text = $"布局: {_docking.CurrentLayoutName}";
}
