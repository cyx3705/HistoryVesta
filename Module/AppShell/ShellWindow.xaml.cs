using System.Windows;
using System.Windows.Input;

namespace OneHistory.AppShell.Desktop;

public partial class ShellWindow : Window
{
    private readonly DesktopPageCatalog _catalog = new();
    private Point _dragOrigin;
    private int _scratchPageNumber;

    public ShellWindow()
    {
        InitializeComponent();
        DataContext = _catalog;
        _catalog.Changed += (_, _) => UpdateStatus();

        _catalog.Register(new DesktopPageDefinition(
            "home",
            "首页",
            PageViewFactory.CreateWelcome));
        _catalog.Register(new DesktopPageDefinition(
            "shell",
            "Shell 状态",
            () => PageViewFactory.CreateInfo(
                "桌面 Shell",
                "页面承载层",
                "AppShell V4.0 只负责桌面窗口、页面选择、页面嵌入和布局承载。业务能力由外部模块自行提供。")));

        Loaded += (_, _) =>
        {
            PageList.SelectedIndex = 0;
            UpdateStatus();
        };
    }

    public void RegisterPage(DesktopPageDefinition page)
    {
        _catalog.Register(page);
        PageList.SelectedItem = page;
    }

    private void OnPageSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is DesktopPageDefinition page)
            ShowPage(page);
    }

    private void ShowPage(DesktopPageDefinition page)
    {
        try
        {
            PageHost.Content = page.CreateView();
            PageTitle.Text = page.Title;
            StatusText.Text = $"当前页面：{page.Title} · 所有者：{page.Owner}";
        }
        catch (Exception ex)
        {
            PageHost.Content = PageViewFactory.CreateInfo(
                "页面加载失败",
                page.Title,
                ex.Message);
            StatusText.Text = $"页面加载失败：{page.Id}";
        }
    }

    private void OnHomeClick(object sender, RoutedEventArgs e)
        => PageList.SelectedItem = _catalog.Find("home");

    private void OnOpenScratchPageClick(object sender, RoutedEventArgs e)
    {
        var number = ++_scratchPageNumber;
        var page = new DesktopPageDefinition(
            $"scratch-{number}",
            $"实验页面 {number}",
            () => PageViewFactory.CreateInfo(
                $"实验页面 {number}",
                "临时前端模块",
                "这是一个仅由桌面 Shell 承载的页面实验。它没有命令总线、后端服务或隐式监听。"),
            "Module/AppShell");
        RegisterPage(page);
    }

    private void OnClosePageClick(object sender, RoutedEventArgs e)
    {
        if (PageList.SelectedItem is not DesktopPageDefinition page || page.Id == "home")
            return;

        _catalog.Unregister(page.Id);
        PageList.SelectedItem = _catalog.Find("home");
    }

    private void OnPageListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragOrigin = e.GetPosition(PageList);

    private void OnPageListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || PageList.SelectedItem is not DesktopPageDefinition page)
            return;

        var current = e.GetPosition(PageList);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop(PageList, page, DragDropEffects.Copy);
    }

    private void OnPageListDragOver(object sender, DragEventArgs e)
        => SetPageDragEffect(e);

    private void OnPageListDrop(object sender, DragEventArgs e)
        => SelectDroppedPage(e);

    private void OnWindowDragOver(object sender, DragEventArgs e)
        => SetPageDragEffect(e);

    private void OnWindowDrop(object sender, DragEventArgs e)
        => SelectDroppedPage(e);

    private void OnPageHostDragOver(object sender, DragEventArgs e)
        => SetPageDragEffect(e);

    private void OnPageHostDrop(object sender, DragEventArgs e)
        => SelectDroppedPage(e);

    private static void SetPageDragEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DesktopPageDefinition))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SelectDroppedPage(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DesktopPageDefinition)) is DesktopPageDefinition page)
            PageList.SelectedItem = page;
        e.Handled = true;
    }

    private void UpdateStatus()
        => StatusText.Text = $"已注册页面：{_catalog.Pages.Count} · V4.0 纯前端 Shell";
}
