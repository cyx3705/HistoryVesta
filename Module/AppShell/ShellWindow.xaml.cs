using System.Windows;
using OneHistory.AppShell.Desktop.Themes;

namespace OneHistory.AppShell.Desktop;

public partial class ShellWindow : Window
{
    private readonly DesktopPageCatalog _catalog = new();
    private readonly DesktopPageHost _pageHost;
    private int _scratchPageNumber;
    private bool _syncingSelection;

    public ShellWindow()
    {
        InitializeComponent();
        DataContext = _catalog;
        DockManager.Theme = new AppShellTheme();
        _pageHost = new DesktopPageHost(DockManager, _catalog);
        _catalog.Changed += (_, _) => UpdatePageState();
        _pageHost.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdatePageState);

        _catalog.Register(new DesktopPageDefinition(
            "home",
            "首页",
            PageViewFactory.CreateWelcome,
            canClose: false));
        _catalog.Register(new DesktopPageDefinition(
            "shell",
            "Shell 状态",
            () => PageViewFactory.CreateInfo(
                "桌面 Shell",
                "页面承载层",
                "AppShell V4.0 负责页面注册、活动切换、拖出浮动、拖回停靠和页面生命周期。业务能力由外部模块自行提供。"),
            canClose: false));

        Loaded += (_, _) =>
        {
            ActivatePage("home");
            UpdatePageState();
        };
    }

    public void RegisterPage(DesktopPageDefinition page)
    {
        _catalog.Register(page);
        ActivatePage(page.Id);
    }

    public bool UnregisterPage(string id) => _pageHost.Unregister(id);

    public bool ActivatePage(string id) => _pageHost.Activate(id);

    public bool FloatPage(string id) => _pageHost.Float(id);

    public bool DockPage(string id) => _pageHost.Dock(id);

    public bool ClosePage(string id) => _pageHost.Close(id);

    public IReadOnlyList<DesktopPageInfo> ListPages() => _pageHost.ListPages();

    private void OnPageSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_syncingSelection && PageList.SelectedItem is DesktopPageDefinition page)
            ActivatePage(page.Id);
    }

    private void OnHomeClick(object sender, RoutedEventArgs e)
        => ActivatePage("home");

    private void OnOpenScratchPageClick(object sender, RoutedEventArgs e)
    {
        var number = ++_scratchPageNumber;
        var page = new DesktopPageDefinition(
            $"scratch-{number}",
            $"实验页面 {number}",
            () => PageViewFactory.CreateInfo(
                $"实验页面 {number}",
                "临时前端模块",
                "拖动上方页面标签即可拉出为独立浮动窗口，也可以拖回主工作区重新停靠。"),
            "Module/AppShell");
        RegisterPage(page);
    }

    private void OnFloatPageClick(object sender, RoutedEventArgs e)
    {
        if (_pageHost.ActivePage is { } page)
            FloatPage(page.Id);
    }

    private void OnDockPageClick(object sender, RoutedEventArgs e)
    {
        if (_pageHost.ActivePage is { } page)
            DockPage(page.Id);
    }

    private void OnClosePageClick(object sender, RoutedEventArgs e)
    {
        if (_pageHost.ActivePage is { } page && ClosePage(page.Id) && _pageHost.ActivePage is null)
            ActivatePage("home");
    }

    private void UpdatePageState()
    {
        var pages = ListPages();
        var active = pages.FirstOrDefault(page => page.IsActive);
        if (active != null)
        {
            _syncingSelection = true;
            PageList.SelectedItem = _catalog.Find(active.Id);
            _syncingSelection = false;
        }

        var openCount = pages.Count(page => page.IsOpen);
        var floatingCount = pages.Count(page => page.IsFloating);
        StatusText.Text = active == null
            ? $"已注册页面：{pages.Count} · 已打开：{openCount} · 浮动：{floatingCount}"
            : $"当前页面：{active.Title} · 所有者：{active.Owner} · 已打开：{openCount} · 浮动：{floatingCount}";
    }
}
