using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AppShell.Shell.Console;
using AppShell.Shell.Docking;
using AppShell.Shell.Themes;
using AvalonDock.Controls;
using AvalonDock.Layout;

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

    // 0.4.4 反哺能力:由 Shell 自行装配,派生应用经下方只读属性取用
    private readonly Services.Modules.ModuleHost? _modules;
    private readonly Modules.ShellUiRegistrar _shellUi;
    private readonly Services.Mcp.McpGateway? _mcp;
    private readonly Services.Mcp.PromptGovernanceStore? _prompts;

    // 命令集页与指令详情页的选中联动(0.4.4 上抛):优先用派生应用经 ShellConfig 传入的实例,
    // 未传则自建。由构造函数赋值——工具窗口内容工厂在 DockingHost 构建默认布局时即被调用,
    // 派生应用那时拿不到 window,故联动实例必须由派生侧创建并传入。
    private readonly CommandSelectionState _commandSelection;

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
        _commandSelection = config.CommandSelection ?? new CommandSelectionState();
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
            settings.GetInt(ConsoleView.KeyHistory, 500));
        _console = new ConsoleView(log, _bus, _history, settings.GetInt(ConsoleView.KeyBuffer, 50_000));

        // 控制台窗口内容由 Shell 接管(§4.4 标准窗口;描述符位置仍由派生应用决定)
        TakeOverDescriptor(StandardWindowIds.Console, "控制台", DockSide.Bottom, 0.25, () => _console);

        // 数据服务已配置时,表窗口(§4.3)同样由 Shell 提供(M3)
        if (config.DataService != null)
        {
            _tableView = new Table.TableView(config.DataService, _bus, log);
            TakeOverDescriptor(StandardWindowIds.Table, "表窗口", DockSide.Bottom, 0.28, () => _tableView);
        }

        // 工作区已配置时,资源窗口(§4.6)由 Shell 提供(M4)
        if (config.Workspace != null)
        {
            _resourceView = new Resource.ResourceView(
                config.Workspace, _bus, log, config.OnResourceOpen,
                Services.AppPaths.GetWorkspaceDir(dataDirectory));
            TakeOverDescriptor(StandardWindowIds.Resource, "资源窗口", DockSide.Left, 0.18, () => _resourceView);
        }

        // 0.4.4:反哺能力自带的管理窗口。窗口内容工厂只依赖总线与选中状态(指令实际执行在 command.list/
        // mcp.status/module.list),故可在此(DockingHost 构建前)接管;网关/模块宿主的创建与指令注册
        // 仍在 BuiltinCommands 之后完成。条件与指令注册一致:mcp 窗口还要求已配置数据服务。
        // 用 TakeOverDescriptor:派生应用若在 config.ToolWindows 声明了同 Id 窗口的停靠位/标签目标,
        // 一律保留其布局,框架只注入内容——因此派生侧既有布局不变。
        if ((config.EnableMcp && config.DataService != null) || config.EnableRemoteManagementViews)
        {
            TakeOverDescriptor(StandardWindowIds.Mcp, "命令集", DockSide.Right, 0.32,
                () => new Views.McpToolsView(() => _bus, _commandSelection));
            // 指令详情窗口:命令集选中项的详情(参数/来源/MCP 映射/提示词状态),与 mcp 窗口共享选中状态
            TakeOverDescriptor(StandardWindowIds.CommandDetail, "指令详情", DockSide.Right, 0.32,
                () => new Views.CommandDetailView(() => _bus, _commandSelection));
        }

        if (config.EnableModules || config.EnableRemoteManagementViews)
        {
            TakeOverDescriptor(StandardWindowIds.Modules, "模块管理", DockSide.Right, 0.32,
                () => new Views.ModulesView(() => _bus));
        }

        // 控制窗口群(§4.5,M4):JSON + C# 通道合并,每个面板一个可停靠窗口
        _panels = new Panels.PanelManager(
            Services.AppPaths.GetPanelsDir(dataDirectory), config.Panels, _bus, log);
        _panels.RegisterWindows(config.ToolWindows);

        _docking = new DockingHost(DockManager, config.ToolWindows, layoutStore, log, settings);
        _docking.CommandGenerated += (_, e) =>
            Dispatcher.BeginInvoke(() => StatusLeft.Text = $"[{e.Source}] {e.CommandText}");
        _docking.Initialize();
        _shellUi = new Modules.ShellUiRegistrar(_docking, Dispatcher, log);
        _docking.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            BuildMenus();
            UpdateStatusRight();
        });

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
        // ---- 0.4.4 反哺能力:模块托管与 MCP 服务(默认启用,ShellConfig 可关)
        //      注册次序在内置指令之后、派生自定义指令之前——派生应用因此可以在
        //      ConfigureCommands 里看到 module.*/mcp.* 已存在,并按需登记只读白名单。
        if (config.EnableModules || config.EnableUiModules)
        {
            _modules = new Services.Modules.ModuleHost(
                Services.AppPaths.GetModulesDir(dataDirectory), log)
            {
                // 此刻在 UI 线程,注册表换血据此编组(替代原先的 Application.Current.Dispatcher)
                UiContext = SynchronizationContext.Current,
                ShellUi = _shellUi,
                EnableCommands = config.EnableModules,
                EnableUiModules = config.EnableModules || config.EnableUiModules,
            };

            // MD-08:窗口成型前先做一次文件级面板同步,上一会话遗留的模块旁面板本次即成窗口
            Services.Modules.ModulePanelSync.SyncFiles(
                _modules.ModulesDirectory, Services.AppPaths.GetPanelsDir(dataDirectory), log);

            // 全限定:本类的 Modules / Mcp 只读属性会遮蔽同名命名空间
            if (config.EnableModules)
                AppShell.Shell.Modules.ModuleCommands.RegisterAll(registry, _modules, settings);
        }

        if (config.EnableMcp)
        {
            var identity = config.Identity ?? Core.AppIdentity.Current;
            _prompts = new Services.Mcp.PromptGovernanceStore(dataDirectory, log);
            var audit = config.McpAuditLog
                        ?? new Services.Mcp.McpAuditRecorder(dataDirectory, log);
                Func<string, string, int, bool?> remoteConfirm = config.McpRemoteConfirm
                    ?? ((_, prompt, timeout) =>
                        AppShell.Shell.Mcp.RemoteConfirmDialog.Ask(this, prompt, timeout));
                _mcp = new Services.Mcp.McpGateway(
                    () => _bus, settings, log, audit, _prompts, identity, remoteConfirm);

                // McpCommands.RegisterAll 是聚合入口:内部级联注册 prompt.*(提示词治理)与
                // command.*(命令目录),不可在此重复调用 CommandCatalogCommands/PromptGovernanceCommands,
                // 否则 command.list 等会二次注册,CommandRegistry 冲突即抛(§5.3)。
                // 全限定:本类的 Mcp 只读属性会遮蔽 AppShell.Shell.Mcp 命名空间。
                AppShell.Shell.Mcp.McpCommands.RegisterAll(registry, () => _bus, () => _mcp, settings, _prompts);

                // CX-03:MCP 中继预批准的执行直接放行,其余仍走 Shell 交互确认
                _bus.Confirmation = new Core.Mcp.GatewayAwareConfirmation(_bus.Confirmation);
        }

        config.ConfigureCommands?.Invoke(registry);

        // 模块宿主在全部指令注册完成后接入并首次装载(此刻仍在 UI 线程)
        _modules?.Attach(registry);
        _modules?.Start();

        // 指令与模块全部就绪后再启动网关，保证首次 tools/list 即为完整注册表。
        if (_mcp != null)
        {
            var (success, message) = _mcp.TryAutostart();
            if (success)
                log.Info("mcp", message);
            else
                log.Warn("mcp", message);
        }

        // S-03:状态栏左侧显示最近一条指令结果摘要;右侧错误计数
        _bus.Executed += (text, source, result) => Dispatcher.BeginInvoke(() =>
        {
            var summary = result.Message.Split('\n')[0];
            StatusLeft.Text = $"{(result.Success ? "✓" : "✗")} [{source}] {text} —— {summary}";
            SyncRemoteTableResult(text, result);
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

        if (config.EnableMaximizeOnDoubleClick)
        {
            DockManager.AddHandler(
                Control.MouseDoubleClickEvent,
                new MouseButtonEventHandler(OnDockDoubleClick),
                handledEventsToo: true);
        }

        BuildMenus();
        UpdateStatusRight();

        Closing += OnShellClosing;
    }

    /// <summary>停靠系统门面。</summary>
    public IDockingService Docking => _docking;

    /// <summary>指令总线(派生应用 / 启动参数经此执行指令)。</summary>
    public CommandBus Commands => _bus;

    /// <summary>模块托管宿主(0.4.4);EnableModules=false 时为 null。</summary>
    public Services.Modules.ModuleHost? Modules => _modules;

    /// <summary>MCP 网关(0.4.4);未启用或缺数据服务时为 null。默认随宿主启动自动监听。</summary>
    public Services.Mcp.McpGateway? Mcp => _mcp;

    /// <summary>提示词治理存储(0.4.4);与 <see cref="Mcp"/> 同生命周期。</summary>
    public Services.Mcp.PromptGovernanceStore? Prompts => _prompts;

    /// <summary>
    /// 命令集选中状态(0.4.4):框架的命令集窗口写入,派生应用的指令详情窗口读取。
    /// 派生应用应把自己的详情视图接到本实例,避免各建一个导致联动失效。
    /// </summary>
    public CommandSelectionState CommandSelection => _commandSelection;

    /// <summary>关于对话框文本(app.about)。</summary>
    public string AboutText =>
        $"{_config.AppName} v{_config.AppVersion}\n\n基于 AppShell 通用窗口框架模板\n.NET 8 + WPF + AvalonDock 4.72.1";

    /// <summary>
    /// 标准窗口内容接管:描述符的停靠位置仍由派生应用声明,内容工厂换成
    /// Shell 实现;未声明时按缺省位置强制注册(控制台是架构不变量 2 的落点)。
    /// 窗口 ID 是框架与派生应用之间的对接约定：双方各自声明并按 Id 合并，
    /// 不得仅在一侧改名；新增窗口时须同步核对派生应用的 ToolWindows 表。
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
        _docking.Show(StandardWindowIds.Console);
        _console.FocusInput();
    }

    private void OnDockDoubleClick(object sender, MouseButtonEventArgs e)
    {
        string? id = null;
        for (DependencyObject? current = e.OriginalSource as DependencyObject;
             current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is AnchorablePaneTitle { Model: LayoutAnchorable anchorable })
            {
                id = anchorable.ContentId;
                break;
            }
        }

        if (id == null)
            return;
        if (_docking.MaximizedId?.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
            _docking.RestoreLayoutFromMaximized();
        else
            _docking.MaximizeWindow(id);
        e.Handled = true;
    }

    private void SyncRemoteTableResult(string text, CommandResult result)
    {
        if (_tableView == null || !result.Success)
            return;
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException)
        {
            return;
        }

        if (parsed.Name is "db.insert" or "db.update" or "db.delete" or "db.sql"
            && _config.DataService is Services.Web.RemoteDataService remoteData)
        {
            remoteData.NotifyDataChanged(
                parsed.Named.GetValueOrDefault("conn"),
                parsed.Name == "db.sql"
                    ? null
                    : parsed.Named.GetValueOrDefault("table") ?? parsed.Positionals.FirstOrDefault());
        }

        if (result.Data is not Core.Data.QueryResult query)
            return;

        if (parsed.Name.Equals("db.query", StringComparison.OrdinalIgnoreCase))
        {
            var table = parsed.Named.GetValueOrDefault("table")
                        ?? parsed.Positionals.FirstOrDefault();
            if (table == null)
                return;
            _tableView.ShowResult(
                parsed.Named.GetValueOrDefault("conn") ?? Core.Data.IDataService.DefaultConnection,
                table,
                parsed.Named.GetValueOrDefault("where"),
                query);
            _docking.Show(StandardWindowIds.Table);
        }
        else if (parsed.Name.Equals("db.sql", StringComparison.OrdinalIgnoreCase))
        {
            _tableView.ShowAdhoc(query);
            _docking.Show(StandardWindowIds.Table);
        }
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

        // 0.4.4:Shell 自建的能力由 Shell 自己收尾——网关握着监听端口,
        // 模块宿主握着文件监听与防抖定时器,都必须在退出前释放。
        // 派生应用不再需要(也不应该)重复 Dispose 这两件。
        _mcp?.Dispose();
        _modules?.Dispose();
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
        var restore = Item("退出窗口最大化", "win.restore");
        restore.IsEnabled = _docking.MaximizedId != null;
        view.Items.Add(restore);
        view.Items.Add(Item("重置默认布局", "layout.reset"));
        MainMenu.Items.Add(view);

        // 工具
        var tools = new MenuItem { Header = "工具(_T)" };
        tools.Items.Add(Item("打开数据目录", "app.opendata"));
        if (_config.ToolMenuActions.Count > 0)
            tools.Items.Add(new Separator());
        foreach (var action in _config.ToolMenuActions)
            tools.Items.Add(Item(action.Header, action.CommandText));
        MainMenu.Items.Add(tools);

        // 帮助:指令手册 = help 的图形化版本(S-01)
        var help = new MenuItem { Header = "帮助(_H)" };
        var manual = new MenuItem { Header = "指令手册(_M)" };
        EnsureMenuCommandValid("help");
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
        EnsureMenuCommandValid(commandText);

        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => _ = _bus.ExecuteAsync(commandText, "UI");
        return mi;
    }

    private void EnsureMenuCommandValid(string commandText)
    {
        var validationError = _bus.Validate(commandText);
        if (validationError != null)
            throw new InvalidOperationException($"菜单引用了无效指令 [{commandText}]: {validationError}");
    }

    private void UpdateStatusRight()
        => StatusRight.Text = _docking.MaximizedId == null
            ? $"布局: {_docking.CurrentLayoutName}"
            : $"布局: {_docking.CurrentLayoutName} · 最大化: {_docking.MaximizedId}";
}
