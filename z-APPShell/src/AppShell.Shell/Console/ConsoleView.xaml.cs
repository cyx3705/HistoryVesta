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
    private readonly IShellLog _log;
    private readonly CommandBus _bus;
    private readonly CommandHistory _history;
    private readonly int _bufferLimit;

    private readonly ConcurrentQueue<ShellLogEntry> _incoming = new();
    private readonly RingCollection<ConsoleRow> _all = new();
    private RingCollection<ConsoleRow> _visible = new();
    private readonly DispatcherTimer _flushTimer;
    private ScrollViewer? _scroll;

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
            _incoming.Enqueue(e);
        log.EntryAdded += (_, e) => _incoming.Enqueue(e);

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
        LevelFilter.SelectedIndex = level == ShellLogLevel.Trace ? 0 : (int)level + 1;
    }

    /// <summary>cls 指令入口:清空显示(不清日志文件,C-06)。</summary>
    public void Cls()
    {
        _all.Clear();
        _visible = new RingCollection<ConsoleRow>();
        Output.ItemsSource = _visible;
    }

    /// <summary>聚焦输入框(C-15 全局快捷键落点)。</summary>
    public void FocusInput()
    {
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    /// <summary>把仅 Error 级别可见设为当前过滤(状态栏错误计数跳转用,S-03)。</summary>
    public void FilterErrorsOnly()
    {
        LevelFilter.SelectedIndex = 5; // Error
        SourceFilter.SelectedIndex = 0;
        MuteLayout.IsChecked = false;
    }

    // ---------------------------------------------------------------- 输出区

    private void FlushIncoming()
    {
        if (_incoming.IsEmpty)
            return;

        var appended = false;
        // 单次最多处理 8000 条,极端涌入时分帧消化,保 UI 响应(N-03)
        for (var i = 0; i < 8000 && _incoming.TryDequeue(out var entry); i++)
        {
            var row = ConsoleRow.From(entry);
            _all.Add(row);
            if (PassesFilter(row))
            {
                _visible.Add(row);
                appended = true;
            }
        }

        _all.TrimTo(_bufferLimit);
        _visible.TrimTo(_bufferLimit);

        if (appended && AutoScroll.IsChecked == true)
            _scroll?.ScrollToBottom();
    }

    private bool PassesFilter(ConsoleRow row)
    {
        if (row.Level < MinLevel)
            return false;

        if (MuteLayout.IsChecked == true && row.SourceKey == "layout")
            return false;

        var source = SourceFilter.SelectedItem as string ?? "全部";
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

        var keyword = KeywordFilter.Text;
        if (keyword.Length > 0
            && !row.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        MinLevel = LevelFilter.SelectedIndex <= 0
            ? ShellLogLevel.Trace
            : (ShellLogLevel)(LevelFilter.SelectedIndex - 1);

        // 重建可见列表(换新集合整体重绑,避免几万条增量通知)
        var next = new RingCollection<ConsoleRow>();
        foreach (var row in _all)
        {
            if (PassesFilter(row))
                next.Add(row);
        }

        _visible = next;
        Output.ItemsSource = _visible;
        if (AutoScroll.IsChecked == true)
            _scroll?.ScrollToBottom();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("cls", "UI");

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (Output.SelectedItems.Count == 0)
            return;

        var selected = new HashSet<object>(Output.SelectedItems.Cast<object>());
        var sb = new StringBuilder();
        foreach (var row in _visible)
        {
            if (selected.Contains(row))
                sb.AppendLine(row.Text);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception)
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出控制台可见内容",
            FileName = $"console-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "文本文件 (*.txt)|*.txt|全部文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllLines(dialog.FileName, _visible.ToList().Select(r => r.Text));
            _log.Info("console", $"已导出 {_visible.Count} 行到 {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _log.Error("console", $"导出失败: {ex.Message}");
        }
    }

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

        _history.Add(text);
        _historyIndex = -1;
        _draft = "";
        SetInputText("");
        _ = _bus.ExecuteAsync(text, "手动");
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
            _history.Add(line);
            await _bus.ExecuteAsync(line, "手动");
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
