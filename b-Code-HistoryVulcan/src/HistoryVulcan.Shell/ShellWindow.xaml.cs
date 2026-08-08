using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Docking;
using HistoryVulcan.Shell.Themes;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryVulcan.Shell;

/// <summary>
/// 主程序窗体(Main Frame,§2)。3.1 起为自绘顶栏 + 停靠系统容器两段结构:
/// 菜单折叠进顶栏右上角的菜单按钮(UI-03),常驻菜单行与底部状态栏均已取消
/// (UI-05,原状态栏信息改由顶栏徽章、布局文本和瞬时回执承担)。
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
    private readonly Views.CommandCatalogSession _commandCatalogSession;
    private readonly Views.McpToolsView _commandCatalog;

    // UI-03:折叠后的菜单挂在顶栏菜单按钮上(挂上去才能继承窗体资源与样式)
    private readonly ContextMenu _menu = new();

    // UI-05.2:指令结果瞬时回执的收起计时器
    private readonly DispatcherTimer _toastTimer;

    private int _errorCount;
    private bool _menusInitialized;

    // UI-09.1:true 表示已接管窗体非客户区;false 表示宿主改过 WindowStyle,
    // 只降级边框接管方式,顶栏四个按钮仍然保留并可用。
    private bool _customChrome;

    // Alt 单独按下(未与其他键组合)才呼出菜单,避免抢走 Alt+Tab 等组合
    private bool _altPressedAlone;

    // 主题字典副本:窗格基底样式与 3.1 卡片/专注模板都从这里取
    private ResourceDictionary? _themeResources;

    // UI-08:浅色/深色令牌整份切换(app.theme),设置项持久化
    private readonly ISettingsService _settings;
    private string _theme = ThemeLight;

    // 右上角按钮组要在最上一排页签里占位,避免页签跑到按钮底下
    private readonly DispatcherTimer _chromeUpkeep;
    private bool _reservePending;
    private bool _closing;
    private bool _allowClose;
    private ContentControl? _chromeHost;
    private FrameworkElement? _chromeDragSurface;
    private readonly ShellTopBarCoordinator _topBar;

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
        _settings = settings;
        Title = $"{config.AppName} v{config.AppVersion}";

        // UI-03:菜单按钮的弹出层;挂到按钮上才能继承窗体资源(菜单项样式)
        MenuButton.ContextMenu = _menu;

        // UI-08:上次选择的主题先于任何界面成型生效,避免启动瞬间闪一下浅色
        ApplyTheme(settings.Get(ThemeSettingsKey) ?? ThemeLight, persist: false);

        // UI-05.2:成功 2.5s、失败 6s 后收起,间隔在 ShowToast 里按结果设定
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };

        StateChanged += (_, _) => ApplyWindowStateChrome();
        SourceInitialized += OnShellSourceInitialized;
        PreviewKeyDown += OnShellPreviewKeyDown;
        PreviewKeyUp += OnShellPreviewKeyUp;

        // 主窗体边界先于停靠布局恢复:布局像素尺寸相对窗体记录,
        // 窗体尺寸一致才能做到“重启后布局原样恢复”(验收 1 / 3)
        RestoreWindowBounds();

        DockManager.Theme = new HistoryVulcanTheme();
        LoadThemeResources();
        ApplyPaneStyles(chromeless: false);

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
        _commandCatalogSession = new Views.CommandCatalogSession(_bus, _commandSelection);
        _console = new ConsoleView(
            log,
            _bus,
            _history,
            _commandCatalogSession,
            settings.GetInt(ConsoleView.KeyBuffer, 50_000));
        _commandCatalog = new Views.McpToolsView(
            () => _bus,
            _commandSelection,
            _commandCatalogSession);

        // 控制台窗口内容由 Shell 接管(§4.4 标准窗口;描述符位置仍由派生应用决定)
        TakeOverDescriptor(StandardWindowIds.Console, "控制台", DockSide.Bottom, 0.25, () => _console);

        // 0.4.4:反哺能力自带的管理窗口。窗口内容工厂只依赖总线与选中状态(指令实际执行在 command.list/
        // mcp.status/module.list),故可在此(DockingHost 构建前)接管;网关/模块宿主的创建与指令注册
        // 仍在 BuiltinCommands 之后完成。条件与指令注册一致。
        // 用 TakeOverDescriptor:派生应用若在 config.ToolWindows 声明了同 Id 窗口的停靠位/标签目标,
        // 一律保留其布局,框架只注入内容——因此派生侧既有布局不变。
        // 本地命令目录是 Shell 核心能力，不以启动 MCP 网络服务为前提。
        TakeOverDescriptor(StandardWindowIds.Mcp, "命令集", DockSide.Center, 1,
            () => _commandCatalog, forcePlacement: true);
        // 指令详情窗口:命令集选中项的详情(参数/来源/MCP 映射/提示词状态),与命令集共享选中状态
        TakeOverDescriptor(StandardWindowIds.CommandDetail, "指令详情", DockSide.Right, 0.32,
            () => new Views.CommandDetailView(
                () => _bus,
                _commandSelection,
                _commandCatalogSession));

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
            Dispatcher.BeginInvoke(() => ShowToast($"[{e.Source}] {e.CommandText}", success: true));
        _docking.Initialize();
        _console.ConfigureCompletionRouting(
            () => string.Equals(
                _docking.MaximizedId,
                StandardWindowIds.Console,
                StringComparison.OrdinalIgnoreCase),
            () =>
            {
                _ = ShowCommandCatalogForCompletionAsync();
            });
        _topBar = new ShellTopBarCoordinator(
            this,
            DockManager,
            _docking,
            _bus,
            _log,
            (RoutedCommand)Resources["Shell.Command.PageAction"]);
        // 按钮组占位与浮动窗口主题需要在「布局稳定之后」才算得准,但不能挂
        // LayoutUpdated:那个事件每帧都发,回调里任何写操作都会再触发一次布局,
        // 直接转成 100% CPU 的死循环(实测)。改为低频巡检 + 幂等写入。
        _chromeUpkeep = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _chromeUpkeep.Tick += (_, _) =>
        {
            if (_closing)
                return;
            AttachChromeBarToMainDocumentPane();
            ReserveSpaceForChromeBar();
            ApplyThemeToFloatingWindows();
        };
        Loaded += (_, _) => _chromeUpkeep.Start();
        _shellUi = new Modules.ShellUiRegistrar(_docking, Dispatcher, log);
        _docking.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                BuildMenus();
            }
            catch (Exception ex)
            {
                _log.Error("menu", $"菜单重建失败，已保留上一版菜单: {ex.Message}");
            }
            UpdateLayoutIndicator();

            // UI-04:页面最大化态与常规态的窗格外观、顶栏形态在此切换。
            // WindowsChanged 是 MaximizeWindow / RestoreLayoutFromMaximized 的共同出口。
            ApplyFocusChrome();

            // R4-3:刚拖出来的浮动窗口带的是自己那份浅色令牌,补一次主题
            ApplyThemeToFloatingWindows();
            _topBar.Refresh();
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
            Panels = _panels,
        });

        RegisterFrontendLifecycleCommands(registry);

        // UI-08:主题切换也是一条指令(S-02),菜单项与控制台走同一条路径
        registry.Register(new CommandDescriptor
        {
            Name = "app.theme",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "切换界面主题(浅色 / 深色)",
            Example = "app.theme mode=dark",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "light、dark 或 toggle",
                    Position = 0,
                    Default = "toggle",
                    AllowedValues = [ThemeLight, ThemeDark, "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode") ?? "toggle";
                if (mode.Equals("toggle", StringComparison.OrdinalIgnoreCase))
                    mode = _theme == ThemeDark ? ThemeLight : ThemeDark;
                ApplyTheme(mode, persist: true);
                return CommandResult.Ok($"界面主题已切换为 {_theme}");
            }),
        });
        // ---- 0.4.4 反哺能力:模块托管与 MCP 服务(默认关闭,ShellConfig 显式开启)
        //      注册次序在内置指令之后、派生自定义指令之前——派生应用因此可以在
        //      ConfigureCommands 里看到 module.*/mcp.* 已存在,并按需登记只读白名单。
        if (config.EnableModules || config.EnableUiModules)
        {
            _modules = new Services.Modules.ModuleHost(
                config.ModuleDirectory ?? Services.AppPaths.GetModulesDir(dataDirectory), log)
            {
                // 此刻在 UI 线程,注册表换血据此编组(替代原先的 Application.Current.Dispatcher)
                UiContext = SynchronizationContext.Current,
                ShellUi = _shellUi,
                EnableCommands = config.EnableModules,
                EnableUiModules = config.EnableModules || config.EnableUiModules,
                EnableFileWatching = config.EnableModules || !config.EnableRemoteManagementViews,
            };

            // MD-08:窗口成型前先做一次文件级面板同步,上一会话遗留的模块旁面板本次即成窗口
            Services.Modules.ModulePanelSync.SyncFiles(
                _modules.ModulesDirectory, Services.AppPaths.GetPanelsDir(dataDirectory), log);

            // 全限定:本类的 Modules / Mcp 只读属性会遮蔽同名命名空间
            if (config.EnableModules)
                HistoryVulcan.Shell.Modules.ModuleCommands.RegisterAll(registry, _modules, settings);
        }

        if (config.EnableMcp)
        {
            var identity = config.Identity ?? Core.AppIdentity.Current;
            _prompts = new Services.Mcp.PromptGovernanceStore(dataDirectory, log);
            var audit = config.McpAuditLog
                        ?? new Services.Mcp.McpAuditRecorder(dataDirectory, log);
            Func<string, string, int, bool?> remoteConfirm = config.McpRemoteConfirm
                ?? ((_, prompt, timeout) =>
                    HistoryVulcan.Shell.Mcp.RemoteConfirmDialog.Ask(this, prompt, timeout));
            _mcp = new Services.Mcp.McpGateway(
                () => _bus, settings, log, audit, _prompts, identity, remoteConfirm);

            // McpCommands.RegisterAll 是聚合入口:内部级联注册 prompt.*(提示词治理)与
            // command.*(命令目录),不可在此重复调用 CommandCatalogCommands/PromptGovernanceCommands,
            // 否则 command.list 等会二次注册,CommandRegistry 冲突即抛(§5.3)。
            // 全限定:本类的 Mcp 只读属性会遮蔽 HistoryVulcan.Shell.Mcp 命名空间。
            HistoryVulcan.Shell.Mcp.McpCommands.RegisterAll(registry, () => _bus, () => _mcp, settings, _prompts);

            // CX-03:MCP 中继预批准的执行直接放行,其余仍走 Shell 交互确认
            _bus.Confirmation = new Core.Mcp.GatewayAwareConfirmation(_bus.Confirmation);
        }
        else
        {
            // command.* 与中央命令集不需要网关、提示词存储或审计器。
            HistoryVulcan.Shell.Mcp.CommandCatalogCommands.RegisterCore(registry);
        }

        config.ConfigureCommands?.Invoke(registry);

        // 模块宿主在全部指令注册完成后接入并首次装载(此刻仍在 UI 线程)
        _modules?.Attach(registry, _bus, settings, dataDirectory);
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

        // 成功仍给瞬时回执；失败直接打开控制台，避免错误浮层遮挡工作区。
        _bus.Executed += (text, source, result) => Dispatcher.BeginInvoke(() =>
        {
            var summary = result.Message.Split('\n')[0];
            if (result.Success)
            {
                ShowToast($"✓ [{source}] {text} —— {summary}", success: true);
            }
            else
            {
                Toast.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
                FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
            }
            UpdateLayoutIndicator();
        });
        log.EntryAdded += (_, entry) =>
        {
            if (entry.Level >= ShellLogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateErrorBadge();
                    if (entry.Level >= ShellLogLevel.Fatal)
                        FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
                });
            }
        };

        // C-15:Ctrl + ` 全局聚焦控制台输入框
        var focusConsole = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(
            focusConsole,
            (_, _) => _ = _bus.ExecuteAsync("log.focus", "UI")));
        InputBindings.Add(new KeyBinding(focusConsole, Key.Oem3, ModifierKeys.Control));

        if (config.EnableMaximizeOnDoubleClick)
        {
            DockManager.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnDockDoubleClick),
                handledEventsToo: true);
        }
        BuildMenus();
        UpdateLayoutIndicator();
        ApplyFocusChrome();

        Closing += OnShellClosing;
        Closed += OnShellClosed;
    }

    /// <summary>
    /// UI-02 / UI-09.1:非客户区接管必须推迟到窗体句柄就绪 —— 派生应用常在对象
    /// 初始化器里(即构造函数返回之后)设置 WindowStyle,构造期判定会误判成标准窗体。
    /// 用事件而非 override，避免仅为内部窗体时序扩大公开面。
    /// </summary>
    private void OnShellSourceInitialized(object? sender, EventArgs e)
    {
        if (WindowStyle == WindowStyle.SingleBorderWindow)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                // 不再让隐藏标题区横跨整个窗体顶部：它会吞掉工具窗格的 ▼/×。
                // 窗体拖动只由中央文档页签行的空白区域处理。
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                // Q-4:Win10 IoT 无系统窗口圆角,窗体外缘一律直角
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            _customChrome = true;
        }
        else
        {
            // 只降级边框接管方式;顶栏的菜单与三个窗口按钮保持不变(UI-09.1)
            _customChrome = false;
            _log.Info("shell", $"宿主使用 WindowStyle={WindowStyle},已跳过非客户区接管,顶部按钮组仍然可用");
        }

        ApplyWindowStateChrome();
        ScheduleChromeReserve();
    }

    /// <summary>停靠系统门面。</summary>
    public IDockingService Docking => _docking;

    /// <summary>指令总线(派生应用 / 启动参数经此执行指令)。</summary>
    public CommandBus Commands => _bus;

    /// <summary>Adds a backend log entry to the in-memory console without writing a second log file.</summary>
    public void AddTransientLog(ShellLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The frontend command bus already records its local echo/result. Do not show
        // the same command categories again when the backend event stream arrives.
        if (entry.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Equals(CommandBus.ResultCategory, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddTransientLog(entry));
            return;
        }

        _console.AddTransientEntry(entry);
        if (entry.Level < ShellLogLevel.Error)
            return;
        Interlocked.Increment(ref _errorCount);
        UpdateErrorBadge();
    }

    /// <summary>模块托管宿主(0.4.4);EnableModules=false 时为 null。</summary>
    public Services.Modules.ModuleHost? Modules => _modules;

    /// <summary>MCP 网关；仅在消费方显式启用 <see cref="ShellConfig.EnableMcp"/> 时创建。</summary>
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
        $"{_config.AppName} v{_config.AppVersion}\n\n基于 HistoryVulcan 通用窗口框架模板\n.NET 8 + WPF + AvalonDock 4.72.1";

    /// <summary>
    /// 标准窗口内容接管:描述符的停靠位置仍由派生应用声明,内容工厂换成
    /// Shell 实现;未声明时按缺省位置强制注册(控制台是架构不变量 2 的落点)。
    /// 窗口 ID 是框架与派生应用之间的对接约定：双方各自声明并按 Id 合并，
    /// 不得仅在一侧改名；新增窗口时须同步核对派生应用的 ToolWindows 表。
    /// </summary>
    private void TakeOverDescriptor(
        string id,
        string fallbackTitle,
        DockSide fallbackSide,
        double fallbackRatio,
        Func<object> factory,
        bool forcePlacement = false)
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
            DefaultSide = forcePlacement ? fallbackSide : d0.DefaultSide,
            DefaultRatio = forcePlacement ? fallbackRatio : d0.DefaultRatio,
            DefaultTabTarget = forcePlacement ? null : d0.DefaultTabTarget,
            DefaultVisible = d0.DefaultVisible,
            IsSingleton = d0.IsSingleton,
            ContentFactory = factory,
        };
    }

    private void FocusConsole(bool resetFilters = false, bool preserveMaximizedLayout = false)
    {
        if (resetFilters)
            _console.ResetFilters();
        if (preserveMaximizedLayout && _docking.MaximizedId != null)
        {
            if (_docking.MaximizedId.Equals(StandardWindowIds.Console, StringComparison.OrdinalIgnoreCase))
                _console.FocusInput();
            return;
        }
        _docking.Show(StandardWindowIds.Console);
        _console.FocusInput();
    }

    private async Task ShowCommandCatalogForCompletionAsync()
    {
        try
        {
            var result = await _bus.ExecuteAsync(
                $"win.show name={StandardWindowIds.Mcp}",
                "UI");
            if (!result.Success)
                _log.Error("console", $"切换命令集失败: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("console", $"切换命令集异常: {ex.GetType().Name}");
        }
    }

    private void OnDockDoubleClick(object sender, MouseButtonEventArgs e)
        => _topBar.HandleDockTabMouseLeftButtonDown(e);

    // ---------------------------------------------------------------- 顶栏状态(UI-05)

    /// <summary>UI-05.3:原状态栏错误计数,点击行为不变(聚焦控制台并只看错误)。</summary>
    private void UpdateErrorBadge()
    {
        ErrorBadge.Content = $"错误 {_errorCount}";
        ErrorBadge.Visibility = _errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnErrorBadgeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("log.focus errors=true", "UI");

    /// <summary>UI-05.2:指令结果瞬时回执;失败停留更久,点击跳控制台看全文。</summary>
    private void ShowToast(string text, bool success)
    {
        ToastText.Text = text;
        if (TryFindResource(success ? "Shell.Brush.TextPrimary" : "Shell.Brush.Danger") is Brush brush)
            ToastText.Foreground = brush;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(success ? 2.5 : 6);
        _toastTimer.Start();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e)
    {
        Toast.Visibility = Visibility.Collapsed;
        _toastTimer.Stop();
        _ = _bus.ExecuteAsync("log.focus", "UI");
    }

    // ---------------------------------------------------------------- 顶栏窗口控件(UI-02)

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("app.window state=minimized", "UI");

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("app.window state=toggle", "UI");

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("app.frontend.hide", "UI");

    internal CommandResult SetFloatingWindowState(string id, string state)
        => _topBar.SetFloatingWindowState(id, state);

    /// <summary>窗体最大化图标切换 + WindowChrome 溢出补偿(UI-02.5)。</summary>
    private void ApplyWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (TryFindResource(maximized ? "Shell.Icon.Restore" : "Shell.Icon.Maximize") is Geometry icon)
            MaximizeIcon.Data = icon;
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";

        // 接管非客户区后,最大化的窗体会按可调整边框宽度溢出工作区,
        // 不补偿则顶栏被裁掉一截。
        RootBorder.Padding = _customChrome && maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        if (_config.CloseBehavior == ShellCloseBehavior.Hide && !_allowClose)
        {
            e.Cancel = true;
            _ = _bus.ExecuteAsync("app.frontend.hide", "UI");
            _closing = false;
            return;
        }

        _closing = true;
        _chromeUpkeep.Stop();
        _history.Save();
        SaveWindowBounds();
        _docking.SaveCurrentLayout();

        // 0.4.4:Shell 自建的能力由 Shell 自己收尾——网关握着监听端口,
        // 模块宿主握着文件监听与防抖定时器,都必须在退出前释放。
        // 派生应用不再需要(也不应该)重复 Dispose 这两件。
        _mcp?.Dispose();
        _modules?.Dispose();
        _commandCatalogSession.Dispose();
    }

    private void OnShellClosed(object? sender, EventArgs e)
        => _topBar.Dispose();

    private void RegisterFrontendLifecycleCommands(CommandRegistry registry)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.hide",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "隐藏 HistoryVulcan 前端窗口并保持后台连接",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Hide();
                return CommandResult.Ok("前端窗口已隐藏");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.show",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "显示并激活 HistoryVulcan 前端窗口",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                return CommandResult.Ok("前端窗口已显示");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.focus-console",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "显示并聚焦控制台",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                var consoleIsFocused = string.Equals(
                    _docking.MaximizedId,
                    StandardWindowIds.Console,
                    StringComparison.OrdinalIgnoreCase);
                if (!consoleIsFocused)
                {
                    if (_docking.MaximizedId != null)
                        _docking.RestoreLayoutFromMaximized();
                    _docking.Show(StandardWindowIds.Console);
                    _docking.MaximizeWindow(StandardWindowIds.Console);
                }
                WindowForegroundActivator.Activate(this);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    WindowForegroundActivator.Activate(this);
                    _console.FocusInput();
                });
                return CommandResult.Ok("控制台已聚焦");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "app.frontend.exit",
            Domain = "HistoryVulcan",
            CommandClass = "app",
            Summary = "退出 HistoryVulcan 前端进程",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                _allowClose = true;
                Close();
                return CommandResult.Ok("前端正在退出");
            }),
        }, FrontendCommandCatalog.Source);
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

    // ---------------------------------------------------------------- 主题(UI-08)

    private const string ThemeSettingsKey = "ui.theme";
    private const string ThemeLight = "light";
    private const string ThemeDark = "dark";

    private static readonly Uri LightTokensUri =
        new("/HistoryVulcan.Shell;component/Themes/ShellTokens.xaml", UriKind.Relative);

    private static readonly Uri DarkTokensUri =
        new("/HistoryVulcan.Shell;component/Themes/ShellTokens.Dark.xaml", UriKind.Relative);

    /// <summary>
    /// 令牌字典整份替换:应用色与 AvalonDock 主题画刷键都在同一份令牌里,
    /// 因此不会出现「界面已深色、页签仍浅色」。三处都要换:
    /// 窗体(视图)、DockingManager(停靠区,压过 VS2013 主题)、应用级(浮动窗口是独立 Window)。
    /// </summary>
    private void ApplyFocusChrome()
    {
        var focused = _docking.MaximizedId != null;

        ExitFocusButton.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
        ExitFocusButton.ToolTip = focused ? "退出聚焦" : null;
        ChromeBar.Visibility = Visibility.Visible;

        // CaptionHeight 永久为零，避免隐藏命中区覆盖任意工具窗格顶部。
        // 文档页头显式拖动主窗口；工具页头由协调器显式拖出工具浮窗。
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.CaptionHeight = 0;

        ApplyPaneStyles(chromeless: focused);
        ScheduleChromeReserve();
        _console.RefreshCompletionFocus();
        _topBar.Refresh();
    }

    private string FindTitle(string id)
        => _docking.Descriptors
               .FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Title
           ?? id;

    private void OnExitFocusClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("win.restore", "UI");

    /// <summary>F11:在当前活动页的专注态与常规态之间切换。</summary>
    private void ToggleFocusMode()
    {
        if (_docking.MaximizedId != null)
        {
            _ = _bus.ExecuteAsync("win.restore", "UI");
            return;
        }

        var id = DockManager.Layout?.ActiveContent?.ContentId;
        if (!string.IsNullOrWhiteSpace(id))
            _ = _bus.ExecuteAsync($"win.max name={id}", "UI");
    }

    // ---------------------------------------------------------------- 键盘(UI-03.4 / UI-04.4)

    private void OnShellPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var alt = e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt;
        _altPressedAlone = alt && !_menu.IsOpen;

        if (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.None)
        {
            OpenShellMenu();
            e.Handled = true;
        }
        else if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleFocusMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _docking.MaximizedId != null && !IsTextInputFocused())
        {
            _ = _bus.ExecuteAsync("win.restore", "UI");
            e.Handled = true;
        }
    }

    private void OnShellPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Alt 单独按下抬起才呼出菜单;与其他键组合(Alt+Tab、Alt+F4、助记符)一律放行
        if (_altPressedAlone && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt)
        {
            _altPressedAlone = false;
            OpenShellMenu();
            e.Handled = true;
        }
    }

    /// <summary>Esc 退出专注不得抢走控制台等输入控件的 Esc(UI-04.4)。</summary>
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox;

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

    // ---------------------------------------------------------------- 菜单(S-01,3.1 折叠为顶栏弹出层 UI-03)

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => OpenShellMenu();

    private void OpenShellMenu()
    {
        if (_menu.Items.Count == 0)
            return;
        _menu.PlacementTarget = MenuButton;
        _menu.Placement = PlacementMode.Bottom;
        _menu.IsOpen = true;
    }

    private void BuildMenus()
    {
        var rebuilt = new List<object>();

        // 文件
        var file = new MenuItem { Header = "文件(_F)" };
        file.Items.Add(Item("退出(_X)", "app.exit"));
        rebuilt.Add(file);

        // 编辑(预留)
        var edit = new MenuItem { Header = "编辑(_E)", IsEnabled = false };
        rebuilt.Add(edit);

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
        // UI-08:主题切换(S-02,同样是发指令)
        view.Items.Add(new Separator());
        view.Items.Add(_theme == ThemeDark
            ? Item("切换到浅色模式", $"app.theme mode={ThemeLight}")
            : Item("切换到深色模式", $"app.theme mode={ThemeDark}"));
        rebuilt.Add(view);

        // 工具
        var tools = new MenuItem { Header = "工具(_T)" };
        tools.Items.Add(Item("打开数据目录", "app.opendata"));
        if (_config.ToolMenuActions.Count > 0)
            tools.Items.Add(new Separator());
        foreach (var action in _config.ToolMenuActions)
            tools.Items.Add(Item(action.Header, action.CommandText));
        rebuilt.Add(tools);

        // 帮助:指令手册 = help 的图形化版本(S-01)
        var help = new MenuItem { Header = "帮助(_H)" };
        var manual = new MenuItem { Header = "指令手册(_M)" };
        var helpError = _bus.Validate("help");
        if (helpError != null && !_menusInitialized)
            throw InvalidMenuCommand("help", helpError);
        manual.IsEnabled = helpError == null;
        manual.ToolTip = helpError == null ? null : $"指令当前不可用: {helpError}";
        manual.Click += async (_, _) =>
        {
            await _bus.ExecuteAsync("log.focus", "UI");
            await _bus.ExecuteAsync("help", "UI");
        };
        help.Items.Add(manual);
        help.Items.Add(Item("关于(_A)", "app.about"));
        rebuilt.Add(help);

        _menu.Items.Clear();
        foreach (var item in rebuilt)
            _menu.Items.Add(item);
        _menusInitialized = true;
    }

    /// <summary>菜单项点击同样是发指令(S-02):统一经总线分发、回显、留痕。</summary>
    private MenuItem Item(string header, string commandText)
    {
        var mi = new MenuItem { Header = header };
        var validationError = _bus.Validate(commandText);
        if (validationError != null)
        {
            if (!_menusInitialized)
                throw InvalidMenuCommand(commandText, validationError);
            mi.IsEnabled = false;
            mi.ToolTip = $"指令当前不可用: {validationError}";
            return mi;
        }
        mi.Click += (_, _) => _ = _bus.ExecuteAsync(commandText, "UI");
        return mi;
    }

    private static InvalidOperationException InvalidMenuCommand(string commandText, string validationError)
        => new($"菜单引用了无效指令 [{commandText}]: {validationError}");

    /// <summary>
    /// UI-05.4:原状态栏右侧的布局名。3.1 取消独立标题栏后没有常驻文本位,
    /// 改挂菜单按钮提示 —— 需要时一悬停就能看到,不占任何常驻像素。
    /// </summary>
    private void UpdateLayoutIndicator()
        => MenuButton.ToolTip = $"菜单 (Alt) · 布局 {_docking.CurrentLayoutName}";
}
