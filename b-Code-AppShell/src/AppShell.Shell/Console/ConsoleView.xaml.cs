using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AppShell.Core.Commands;
using AppShell.Core.Logging;

namespace AppShell.Shell.Console;

/// <summary>
/// 控制台窗口(§4.4):程序的“素颜”。
/// 输出区 = 指令回显 + 执行结果 + 运行日志(共用 IShellLog 管道);
/// 输入区 = 手动指令入口。仅靠本窗口即可驱动整个程序(架构不变量 2)。
/// 承压设计(N-03):日志事件先进并发队列,UI 以 100ms 批量合并刷新;
/// 列表虚拟化 + 环形缓冲上限(C-05)。
/// </summary>
public partial class ConsoleView : UserControl
{
    public const string KeyHistory = "console.history";
    public const string KeyBuffer = "console.buffer";

    private readonly IShellLog _log;
    private readonly CommandBus _bus;
    private readonly CommandHistory _history;
    private readonly int _bufferLimit;

    private readonly ConcurrentQueue<ShellLogEntry> _incoming = new();
    private int _incomingCount;
    private readonly RingCollection<ConsoleRow> _all = new();
    private RingCollection<ConsoleRow> _visible = new();
    private readonly DispatcherTimer _flushTimer;
    private ScrollViewer? _scroll;
    private string _source = "全部";
    private string _keyword = "";
    private bool _muteLayout;
    private bool _autoScroll = true;
    private bool _suppressFilterEvents;

    // 输入区状态
    private int _historyIndex = -1;
    private string _draft = "";
    private List<string>? _completions;
    private int _completionIndex;
    private bool _suppressTextChanged;

    public ConsoleView(IShellLog log, CommandBus bus, CommandHistory history, int bufferLimit = 50_000)
    {
        InitializeComponent();

        _log = log;
        _bus = bus;
        _history = history;
        _bufferLimit = Math.Max(1000, bufferLimit);

        LevelFilter.ItemsSource = new[] { "全部", "Trace", "Debug", "Info", "Warn", "Error", "Fatal" };
        LevelFilter.SelectedIndex = 0;
        SourceFilter.ItemsSource = new[] { "全部", "UI", "手动", "脚本", "layout", "日志" };
        SourceFilter.SelectedIndex = 0;

        Output.ItemsSource = _visible;

        // 已有历史补读 + 增量订阅
        foreach (var e in log.Snapshot())
            EnqueueIncoming(e);
        log.EntryAdded += (_, e) =>
        {
            if (e.Category.Equals(CommandBus.EchoCategoryPrefix + "手动", StringComparison.OrdinalIgnoreCase))
                _history.Add(e.Message);
            EnqueueIncoming(e);
        };

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => FlushIncoming();
        _flushTimer.Start();

        // C-14:粘贴多行文本 → 按行拆分为多条指令顺序执行
        DataObject.AddPastingHandler(Input, OnInputPasting);

        Loaded += (_, _) => _scroll ??= FindScrollViewer(Output);
    }

    /// <summary>当前控制台显示级别(log.level,L-04;文件始终全量)。</summary>
    public ShellLogLevel MinLevel { get; private set; } = ShellLogLevel.Trace;

    /// <summary>log.level 指令入口:调整显示级别过滤。</summary>
    public void SetMinLevel(ShellLogLevel level)
    {
        MinLevel = level;
        _suppressFilterEvents = true;
        try
        {
            LevelFilter.SelectedIndex = level == ShellLogLevel.Trace ? 0 : (int)level + 1;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        RebuildVisible();
    }

    internal string SourceFilterValue => _source;
    internal string KeywordFilterValue => _keyword;
    internal bool MuteLayoutEnabled => _muteLayout;
    internal bool AutoScrollEnabled => _autoScroll;

    internal void SetSource(string source)
    {
        _source = string.IsNullOrWhiteSpace(source) ? "全部" : source;
        _suppressFilterEvents = true;
        try
        {
            SourceFilter.SelectedItem = _source;
        }
        finally
        {
            _suppressFilterEvents = false;
        }
        RebuildVisible();
    }

    internal void SetKeyword(string keyword)
    {
        _keyword = keyword ?? "";
        _suppressFilterEvents = true;
        try { KeywordFilter.Text = _keyword; }
        finally { _suppressFilterEvents = false; }
        RebuildVisible();
    }

    internal void SetMuteLayout(bool enabled)
    {
        _muteLayout = enabled;
        _suppressFilterEvents = true;
        try { MuteLayout.IsChecked = enabled; }
        finally { _suppressFilterEvents = false; }
        RebuildVisible();
    }

    internal void SetAutoScroll(bool enabled)
    {
        _autoScroll = enabled;
        _suppressFilterEvents = true;
        try { AutoScroll.IsChecked = enabled; }
        finally { _suppressFilterEvents = false; }
    }

    /// <summary>cls 指令入口:清空显示(不清日志文件,C-06)。</summary>
    public void Cls()
    {
        _all.Clear();
        _visible = new RingCollection<ConsoleRow>();
        Output.ItemsSource = _visible;
    }

    internal void AddTransientEntry(ShellLogEntry entry) => EnqueueIncoming(entry);

    internal string CopySelected()
    {
        if (Output.SelectedItems.Count == 0)
            return "没有选中的控制台行";

        var selected = new HashSet<object>(Output.SelectedItems.Cast<object>());
        var text = string.Join(Environment.NewLine, _visible.Where(selected.Contains).Select(row => row.Text));
        try
        {
            Clipboard.SetText(text);
            return $"已复制 {selected.Count} 行";
        }
        catch (Exception ex)
        {
            _log.Error("console", $"复制失败: {ex.Message}");
            return "复制失败";
        }
    }

    internal string ExportVisible(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出控制台可见内容",
                FileName = $"console-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "文本文件 (*.txt)|*.txt|全部文件 (*.*)|*.*",
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return "已取消导出";
            path = dialog.FileName;
        }

        try
        {
            File.WriteAllLines(path, _visible.ToList().Select(row => row.Text));
            _log.Info("console", $"已导出 {_visible.Count} 行到 {path}");
            return $"已导出 {_visible.Count} 行到 {path}";
        }
        catch (Exception ex)
        {
            _log.Error("console", $"导出失败: {ex.Message}");
            return $"导出失败: {ex.Message}";
        }
    }

    /// <summary>聚焦输入框(C-15 全局快捷键落点)。</summary>
    public void FocusInput()
    {
        // AvalonDock may finish showing the anchorable after this method returns.
        // Queue the focus operation so the real TextBox, rather than its host, receives keys.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible)
                return;

            Input.Focusable = true;
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
        }));
    }

    /// <summary>把仅 Error 级别可见设为当前过滤(状态栏错误计数跳转用,S-03)。</summary>
    public void FilterErrorsOnly()
    {
        SetMinLevel(ShellLogLevel.Error);
        SetSource("全部");
        SetKeyword("");
        SetMuteLayout(false);
    }

    /// <summary>错误自动跳转使用：清除过滤，保证输入回显与错误结果同时可见。</summary>
    internal void ResetFilters()
    {
        SetMinLevel(ShellLogLevel.Trace);
        SetSource("全部");
        SetKeyword("");
        SetMuteLayout(false);
    }

    // ---------------------------------------------------------------- 输出区

    private void EnqueueIncoming(ShellLogEntry entry)
    {
        _incoming.Enqueue(entry);
        var count = Interlocked.Increment(ref _incomingCount);
        while (count > _bufferLimit && _incoming.TryDequeue(out _))
            count = Interlocked.Decrement(ref _incomingCount);
    }

    private void FlushIncoming()
    {
        if (_incoming.IsEmpty)
            return;

        var appended = false;
        // 单次最多处理 8000 条,极端涌入时分帧消化,保 UI 响应(N-03)
        for (var i = 0; i < 8000 && _incoming.TryDequeue(out var entry); i++)
        {
            Interlocked.Decrement(ref _incomingCount);
            foreach (var row in ConsoleRow.From(entry))
            {
                _all.Add(row);
                if (PassesFilter(row))
                {
                    _visible.Add(row);
                    appended = true;
                }
            }
        }

        _all.TrimTo(_bufferLimit);
        _visible.TrimTo(_bufferLimit);

        if (appended && _autoScroll)
            _scroll?.ScrollToBottom();
    }

    private bool PassesFilter(ConsoleRow row)
    {
        if (row.Level < MinLevel)
            return false;

        if (_muteLayout && row.SourceKey == "layout")
            return false;

        var source = _source;
        if (source != "全部")
        {
            var match = source switch
            {
                "日志" => row.SourceKey is not ("UI" or "手动" or "脚本" or "layout" or "result"),
                _ => row.SourceKey == source,
            };
            if (!match)
                return false;
        }

        var keyword = _keyword;
        if (keyword.Length > 0
            && !row.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void RebuildVisible()
    {
        // 重建可见列表(换新集合整体重绑,避免几万条增量通知)
        var next = new RingCollection<ConsoleRow>();
        foreach (var row in _all)
        {
            if (PassesFilter(row))
                next.Add(row);
        }

        _visible = next;
        Output.ItemsSource = _visible;
        if (_autoScroll)
            _scroll?.ScrollToBottom();
    }

    private void OnLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents || LevelFilter.SelectedIndex < 0)
            return;
        var level = LevelFilter.SelectedIndex == 0 ? "trace" : ((ShellLogLevel)(LevelFilter.SelectedIndex - 1)).ToString().ToLowerInvariant();
        _ = _bus.ExecuteAsync($"log.level level={level}", "UI");
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressFilterEvents || SourceFilter.SelectedItem is not string source)
            return;
        _ = _bus.ExecuteAsync($"log.source source={CommandParser.QuoteArg(source)}", "UI");
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("log.clear", "UI");

    private void OnCopyClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("log.copy", "UI");

    private void OnExportClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("log.export", "UI");

    // ---------------------------------------------------------------- 输入区

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Submit(Input.Text);
                e.Handled = true;
                break;

            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;

            case Key.Down:
                NavigateHistory(+1);
                e.Handled = true;
                break;

            case Key.Tab:
                CycleCompletion();
                e.Handled = true;
                break;

            case Key.Escape:
                SetInputText("");
                _historyIndex = -1;
                e.Handled = true;
                break;
        }
    }

    private void Submit(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        _historyIndex = -1;
        _draft = "";
        SetInputText("");
        _ = ExecuteAndFlushAsync(text, "手动");
    }

    private async Task ExecuteAndFlushAsync(string text, string source)
    {
        var execution = _bus.ExecuteAsync(text, source);
        FlushIncoming();
        try
        {
            await execution.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("console", $"指令执行异常: {ex.GetType().Name}");
        }

        FlushIncoming();
    }

    private void NavigateHistory(int direction)
    {
        var items = _history.Snapshot();
        if (items.Count == 0)
            return;

        if (_historyIndex == -1)
        {
            if (direction > 0)
                return;
            _draft = Input.Text;
            _historyIndex = items.Count - 1;
        }
        else
        {
            _historyIndex += direction;
        }

        if (_historyIndex >= items.Count)
        {
            _historyIndex = -1;
            SetInputText(_draft);
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex);
        SetInputText(items[_historyIndex]);
    }

    /// <summary>C-13:Tab 补全指令名(输入首词)与参数名(后续词),重复 Tab 轮换候选。</summary>
    private void CycleCompletion()
    {
        if (_completions == null)
        {
            _completions = BuildCompletions(Input.Text);
            _completionIndex = 0;
        }
        else
        {
            _completionIndex = (_completionIndex + 1) % Math.Max(1, _completions.Count);
        }

        if (_completions.Count == 0)
            return;

        _suppressTextChanged = true;
        Input.Text = _completions[_completionIndex];
        Input.CaretIndex = Input.Text.Length;
        _suppressTextChanged = false;
    }

    private List<string> BuildCompletions(string text)
    {
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            // 指令名补全
            return _bus.Registry.All()
                .Where(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
        }

        // 参数名补全:head = "win.dock ",tail = 正在输入的参数前缀
        var head = text[..(lastSpace + 1)];
        var tail = text[(lastSpace + 1)..];
        var cmdName = text[..text.IndexOf(' ')];
        if (tail.Contains('=') || !_bus.Registry.TryGet(cmdName, out var descriptor))
            return [];

        return descriptor.Parameters
            .Where(p => p.Name.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
            .Select(p => head + p.Name + "=")
            .ToList();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressTextChanged)
            _completions = null; // 用户改动输入后重算补全候选
    }

    private void SetInputText(string text)
    {
        _suppressTextChanged = true;
        Input.Text = text;
        Input.CaretIndex = text.Length;
        _suppressTextChanged = false;
        _completions = null;
    }

    private void OnInputPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            return;

        var text = (string)e.DataObject.GetData(DataFormats.UnicodeText)!;
        if (!text.Contains('\n'))
            return;

        e.CancelCommand();
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !CommandParser.IsBlankOrComment(l))
            .ToList();
        _ = RunLinesAsync(lines);
    }

    private async Task RunLinesAsync(List<string> lines)
    {
        foreach (var line in lines)
        {
            await ExecuteAndFlushAsync(line, "手动");
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } nested)
                return nested;
        }

        return null;
    }
}
