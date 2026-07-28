using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AppShell.Core.Commands;
using AppShell.Core.Files;
using AppShell.Core.Logging;

namespace AppShell.Shell.Resource;

/// <summary>
/// 资源窗口(§4.6):内嵌式资源管理器,浏览与管理工作区根目录。
/// 单棵懒加载树(R-01);全部写操作生成 res.* 等价指令经总线执行(R-05),
/// 边界校验与回收站语义由 IWorkspaceService 统一保证(R-06)。
/// 双击文件先走派生应用接管挂点(R-03),未接管则 res.open。
/// </summary>
public partial class ResourceView : UserControl
{
    private readonly IWorkspaceService _workspace;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly Func<string, bool>? _openHandler;
    private readonly string? _defaultRoot;
    private readonly DispatcherTimer _refreshDebounce;

    public ResourceView(
        IWorkspaceService workspace,
        CommandBus bus,
        IShellLog log,
        Func<string, bool>? openHandler,
        string? defaultRoot = null)
    {
        _defaultRoot = defaultRoot;
        InitializeComponent();

        _workspace = workspace;
        _bus = bus;
        _log = log;
        _openHandler = openHandler;
        OpenFolderButton.IsEnabled = workspace.CanSelectLocalRoot;
        OpenFolderMenuItem.IsEnabled = workspace.CanSelectLocalRoot;
        ResetRootMenuItem.IsEnabled = workspace.CanSelectLocalRoot;

        // R-04:外部变更(或 res.* 指令改动)→ 去抖后整树刷新
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            ReloadTree();
        };
        _workspace.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });

        Loaded += (_, _) =>
        {
            if (Tree.Items.Count == 0)
                ReloadTree();
        };
    }

    /// <summary>res.root 切换根目录后的刷新入口。</summary>
    public void ReloadTree()
    {
        // 记住已展开的目录,刷新后尽量还原
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpanded(Tree.Items, expanded);

        Tree.Items.Clear();
        try
        {
            foreach (var entry in _workspace.List())
                Tree.Items.Add(CreateNode(entry));
            RootText.Text = _workspace.Root;
        }
        catch (Exception ex)
        {
            _log.Error("res", $"读取工作区失败: {ex.Message}");
            return;
        }

        RestoreExpanded(Tree.Items, expanded);
    }

    // ---------------------------------------------------------------- 树构建(懒加载)

    private TreeViewItem CreateNode(WorkspaceEntry entry)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = entry.IsDirectory ? "📁 " : "📄 " });
        header.Children.Add(new TextBlock { Text = entry.Name });
        if (!entry.IsDirectory)
        {
            header.Children.Add(new TextBlock
            {
                Text = $"  {FormatSize(entry.Size)} · {entry.Modified:yyyy-MM-dd HH:mm}",
                Foreground = Brushes.Gray,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var node = new TreeViewItem { Header = header, Tag = entry };
        if (entry.IsDirectory)
            node.Items.Add(new TreeViewItem { Tag = LazyPlaceholder }); // 展开时再加载
        return node;
    }

    private static readonly object LazyPlaceholder = new();

    private void OnNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem node
            || node.Tag is not WorkspaceEntry { IsDirectory: true } entry)
        {
            return;
        }

        if (node.Items.Count != 1 || (node.Items[0] as TreeViewItem)?.Tag != LazyPlaceholder)
            return;

        node.Items.Clear();
        try
        {
            foreach (var child in _workspace.List(entry.RelativePath))
                node.Items.Add(CreateNode(child));
        }
        catch (Exception ex)
        {
            _log.Error("res", $"读取目录失败: {ex.Message}");
        }
    }

    private WorkspaceEntry? SelectedEntry
        => (Tree.SelectedItem as TreeViewItem)?.Tag as WorkspaceEntry;

    /// <summary>新建文件夹的落点目录:选中目录本身 / 选中文件的所在目录 / 根。</summary>
    private string SelectedDirectory
    {
        get
        {
            var entry = SelectedEntry;
            if (entry == null)
                return "";
            if (entry.IsDirectory)
                return entry.RelativePath;
            var parent = System.IO.Path.GetDirectoryName(entry.RelativePath);
            return parent ?? "";
        }
    }

    // ---------------------------------------------------------------- 操作 → res.* 指令(R-05)

    private void Send(string command, bool reload = false) => _ = SendAsync(command, reload);

    private async Task SendAsync(string command, bool reload)
    {
        var result = await _bus.ExecuteAsync(command, "UI");
        if (reload && result.Success)
            ReloadTree();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ReloadTree();

    /// <summary>打开其他文件夹作为工作区根(等价 res.root path=…,含持久化)。</summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "打开文件夹作为工作区根目录",
            InitialDirectory = _workspace.Root,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        Send($"res.root path={CommandParser.QuoteArg(dialog.FolderName)}", reload: true);
    }

    private void OnResetRootClick(object sender, RoutedEventArgs e)
    {
        if (_defaultRoot == null)
            return;
        Send($"res.root path={CommandParser.QuoteArg(_defaultRoot)}", reload: true);
    }

    private void OnMkdirClick(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(Window.GetWindow(this)!, "新建文件夹", "文件夹名:", "新建文件夹");
        if (string.IsNullOrEmpty(name))
            return;
        var path = System.IO.Path.Combine(SelectedDirectory, name);
        Send($"res.mkdir path={CommandParser.QuoteArg(path)}", reload: true);
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry == null)
            return;
        var name = InputDialog.Show(Window.GetWindow(this)!, "重命名", $"把 {entry.Name} 改名为:", entry.Name);
        if (string.IsNullOrEmpty(name) || name == entry.Name)
            return;
        Send($"res.rename path={CommandParser.QuoteArg(entry.RelativePath)} to={CommandParser.QuoteArg(name)}", reload: true);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry == null)
            return;
        // res.delete 自带总线二次确认(R-06);此处直接发指令,确认框由拦截器弹出
        Send($"res.delete path={CommandParser.QuoteArg(entry.RelativePath)}", reload: true);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is { } entry)
            OpenEntry(entry);
    }

    private void OnRevealClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is { } entry)
            Send($"res.reveal path={CommandParser.QuoteArg(entry.RelativePath)}");
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not { } entry)
            return;
        try
        {
            Clipboard.SetText(_workspace.ResolveFull(entry.RelativePath));
        }
        catch (Exception)
        {
            // 剪贴板占用时忽略
        }
    }

    private void OnTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedEntry is { IsDirectory: false } entry)
            OpenEntry(entry);
    }

    private void OpenEntry(WorkspaceEntry entry)
    {
        // R-03:双击打开可被派生应用接管(例:.map 在主窗口打开)
        if (!entry.IsDirectory && _openHandler != null)
        {
            try
            {
                if (_openHandler(_workspace.ResolveFull(entry.RelativePath)))
                    return;
            }
            catch (Exception ex)
            {
                _log.Error("res", $"打开接管器异常: {ex.Message}");
            }
        }

        Send($"res.open path={CommandParser.QuoteArg(entry.RelativePath)}");
    }

    // ---------------------------------------------------------------- 工具

    private static void CollectExpanded(ItemCollection items, HashSet<string> expanded)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item is { IsExpanded: true, Tag: WorkspaceEntry entry })
            {
                expanded.Add(entry.RelativePath);
                CollectExpanded(item.Items, expanded);
            }
        }
    }

    private void RestoreExpanded(ItemCollection items, HashSet<string> expanded)
    {
        foreach (var item in items.OfType<TreeViewItem>())
        {
            if (item.Tag is WorkspaceEntry entry && expanded.Contains(entry.RelativePath))
            {
                item.IsExpanded = true; // 触发懒加载
                item.UpdateLayout();
                RestoreExpanded(item.Items, expanded);
            }
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024 * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };
}
