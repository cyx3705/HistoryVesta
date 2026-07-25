using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

/// <summary>项目创建、打开和所选项目的三状态 Git 文件格式规则。</summary>
public partial class ProjectOperationsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private readonly ObservableCollection<RuleEditRow> _rules = [];
    private bool _suppressProjectSelection;
    private bool _projectOperationRunning;
    private int _ruleLoadGeneration;

    public ProjectOperationsView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        SelectedCommitMessageBox.Text = "一键推送更新";
        RuleGrid.ItemsSource = _rules;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewKit.RunOnceOnLoaded(this, () => RefreshProjectsAsync(_selection.CurrentProjectName));
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _selection.Changed -= OnSharedSelectionChanged;
        _selection.Changed += OnSharedSelectionChanged;
        Dispatcher.BeginInvoke(async () => await ApplySharedSelectionAsync());
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        => _selection.Changed -= OnSharedSelectionChanged;

    private async Task RefreshProjectsAsync(string? select = null)
    {
        if (_busAccessor() is not { } bus)
            return;
        var current = select ?? _selection.CurrentProjectName ?? CurrentProjectName();
        RefreshProjectsButton.IsEnabled = false;
        try
        {
            var result = await bus.ExecuteAsync("proj.list", "UI");
            if (!result.Success || result.Data is not List<WorktreeInfo> projects)
            {
                StatusText.Text = "项目加载失败，详见控制台";
                return;
            }
            var names = projects.Select(project => project.BranchName).ToList();
            _suppressProjectSelection = true;
            CurrentProjectBox.ItemsSource = names;
            var selected = names.FirstOrDefault(name =>
                name.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            CurrentProjectBox.SelectedItem = selected;
            _selection.CurrentProjectName = selected;
            _suppressProjectSelection = false;
            UpdateProjectActions();
            if (CurrentProjectName() is { Length: > 0 } name)
                await LoadRulesAsync(name);
            else
                ClearRules();
        }
        finally
        {
            RefreshProjectsButton.IsEnabled = true;
        }
    }

    private async void OnProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectSelection)
            return;
        UpdateProjectActions();
        if (CurrentProjectName() is { Length: > 0 } name)
        {
            _selection.CurrentProjectName = name;
            await LoadRulesAsync(name);
        }
        else
            ClearRules();
    }

    private void OnSharedSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressProjectSelection)
            return;
        Dispatcher.BeginInvoke(async () => await ApplySharedSelectionAsync());
    }

    private async Task ApplySharedSelectionAsync()
    {
        var names = CurrentProjectBox.ItemsSource as IEnumerable<string> ?? [];
        var selected = _selection.CurrentProjectName is { } current
            ? names.FirstOrDefault(name => name.Equals(current, StringComparison.OrdinalIgnoreCase))
            : null;

        _suppressProjectSelection = true;
        CurrentProjectBox.SelectedItem = selected;
        if (selected == null)
            CurrentProjectBox.Text = "";
        _suppressProjectSelection = false;
        UpdateProjectActions();

        if (selected != null)
            await LoadRulesAsync(selected);
        else
            ClearRules();
    }

    private void OnNewProjectNameChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnCommitMessageChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnOperationModeChanged(object sender, System.Windows.RoutedEventArgs e)
        => UpdateProjectActions();

    private async void OnRefreshProjectsClick(object sender, System.Windows.RoutedEventArgs e)
        => await RefreshProjectsAsync();

    private async void OnCreateClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_busAccessor() is not { } bus
            || NewProjectName() is not { Length: > 0 } name
            || CurrentProjectName() is not { Length: > 0 } baseProject)
            return;
        var command = $"proj.create name={CommandParser.QuoteArg(name)} " +
                      $"base={CommandParser.QuoteArg(baseProject)}";
        var result = await bus.ExecuteAsync(command, "UI");
        StatusText.Text = result.Success ? "项目已创建" : "项目创建失败，详见控制台";
        if (result.Success)
        {
            NewProjectNameBox.Clear();
            await RefreshProjectsAsync(name);
        }
    }

    private void OnOpenClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } name)
            _ = _busAccessor()?.ExecuteAsync($"proj.open name={CommandParser.QuoteArg(name)}", "UI");
    }

    private async void OnCommitSelectedClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var mode = CurrentOperationMode();
        var project = CurrentProjectName();
        if (_busAccessor() is not { } bus
            || SelectedCommitMessageBox.Text.Trim() is not { Length: > 0 } message
            || ProjectOperationCommandBuilder.RequiresCurrentProject(mode) && project.Length == 0)
            return;

        SetProjectOperationRunning(true);
        try
        {
            var result = await bus.ExecuteAsync(ProjectOperationCommandBuilder.BuildCommit(
                mode, project, message), "UI");
            StatusText.Text = ViewKit.ResultSummary(result);
        }
        finally
        {
            SetProjectOperationRunning(false);
        }
    }

    private async void OnPushSelectedClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var mode = CurrentOperationMode();
        var project = CurrentProjectName();
        if (_busAccessor() is not { } bus
            || ProjectOperationCommandBuilder.RequiresCurrentProject(mode) && project.Length == 0)
            return;

        SetProjectOperationRunning(true);
        try
        {
            var result = await bus.ExecuteAsync(ProjectOperationCommandBuilder.BuildPush(
                mode, project), "UI");
            StatusText.Text = ViewKit.ResultSummary(result);
        }
        finally
        {
            SetProjectOperationRunning(false);
        }
    }

    private async Task LoadRulesAsync(string project, bool refresh = false)
    {
        if (_busAccessor() is not { } bus)
            return;

        var generation = ++_ruleLoadGeneration;
        RulePanel.IsEnabled = false;
        RuleTitle.Text = $"Git 文件规则 · {project}";
        CoverageText.Text = $"正在读取 {project} 的格式台账…";

        var quotedProject = CommandParser.QuoteArg(project);
        var scanTask = bus.ExecuteAsync(
            $"git.rule.scan name={quotedProject} refresh={Bool(refresh)}", "UI");
        var listTask = bus.ExecuteAsync($"git.rule.list name={quotedProject}", "UI");
        await Task.WhenAll(scanTask, listTask);

        if (generation != _ruleLoadGeneration
            || !CurrentProjectName().Equals(project, StringComparison.OrdinalIgnoreCase))
            return;

        var scan = await scanTask;
        var list = await listTask;
        _rules.Clear();
        if (scan.Success && scan.Data is InventoryReport report
            && list.Success && list.Data is IReadOnlyList<GitFileRuleInfo> declared)
        {
            foreach (var row in MergeRows(report, declared))
                _rules.Add(row);
            RulePanel.IsEnabled = true;
            CoverageText.Text =
                $"格式覆盖 · {project}：覆盖率 {report.CoverageRate:P1}，未决 {report.UndecidedCount} 个" +
                $"（{report.Formats.Count} 种格式 / {report.FileCount} 个文件）";
            StatusText.Text = $"{project}: {_rules.Count} 行（扫描格式 + 已声明规则，已去重）";
        }
        else
        {
            CoverageText.Text = "格式台账加载失败，详见控制台";
            StatusText.Text = !scan.Success ? ViewKit.ResultSummary(scan) : ViewKit.ResultSummary(list);
        }
    }

    private static IEnumerable<RuleEditRow> MergeRows(
        InventoryReport report,
        IReadOnlyList<GitFileRuleInfo> declared)
    {
        var declaredByPattern = declared.ToDictionary(
            row => row.Pattern, StringComparer.OrdinalIgnoreCase);
        var rows = new List<RuleEditRow>();

        foreach (var format in report.Formats)
        {
            declaredByPattern.Remove(format.Format, out var rule);
            rows.Add(new RuleEditRow(format, rule));
        }

        rows.AddRange(declaredByPattern.Values.Select(rule => new RuleEditRow(null, rule)));
        return rows
            .OrderBy(row => row.SortGroup)
            .ThenByDescending(row => row.UndecidedCount)
            .ThenByDescending(row => row.FileCount)
            .ThenBy(row => row.Pattern, StringComparer.OrdinalIgnoreCase);
    }

    private void OnAddRuleClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var pattern = PatternBox.Text.Trim();
        if (pattern.Length == 0)
            return;
        var existing = _rules.FirstOrDefault(rule =>
            rule.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            RuleGrid.SelectedItem = existing;
            StatusText.Text = $"规则已存在: {pattern}";
            return;
        }
        var draft = new RuleEditRow(pattern);
        _rules.Add(draft);
        RuleGrid.SelectedItem = draft;
        RuleGrid.ScrollIntoView(draft);
        StatusText.Text = "新规则尚未保存";
    }

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e)
    {
        var selected = RuleGrid.SelectedItem as RuleEditRow;
        SaveRuleButton.IsEnabled = selected is { CanEdit: true };
        DeleteRuleButton.IsEnabled = selected is { CanEdit: true }
                                     && (selected.IsDeclared || selected.IsDraft);
    }

    private async void OnSaveRuleClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not RuleEditRow rule || CurrentProjectName() is not { Length: > 0 } project
            || _busAccessor() is not { } bus)
            return;
        if (!rule.CanEdit || rule.Track is not bool track
            || rule.Lfs is not bool lfs || rule.Lf is not bool lf)
        {
            StatusText.Text = "请先明确选择 Git、LFS 指针与 LF 文本状态";
            return;
        }
        var command = $"git.rule.set name={CommandParser.QuoteArg(project)} " +
                      $"pattern={CommandParser.QuoteArg(rule.Pattern)} track={Bool(track)} " +
                      $"lfs={Bool(lfs)} lf={Bool(lf)}";
        var preview = await bus.ExecuteAsync(command + " apply=false", "UI");
        if (!preview.Success || preview.Data is not GitFileRulePreview { Changed: true })
        {
            StatusText.Text = preview.Success ? "规则无需变化" : "规则预览失败，详见控制台";
            return;
        }
        var applied = await bus.ExecuteAsync(command + " apply=true", "UI");
        StatusText.Text = applied.Success ? "规则与 Git 索引已更新" : "规则应用失败，详见控制台";
        if (applied.Success)
            await LoadRulesAsync(project, refresh: true);
    }

    private async void OnDeleteRuleClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not RuleEditRow rule || CurrentProjectName() is not { Length: > 0 } project
            || _busAccessor() is not { } bus)
            return;
        if (rule.IsDraft)
        {
            _rules.Remove(rule);
            return;
        }
        if (!rule.IsDeclared)
        {
            StatusText.Text = "该格式由扫描发现，当前没有可删除的规则";
            return;
        }
        var command = $"git.rule.remove name={CommandParser.QuoteArg(project)} " +
                      $"pattern={CommandParser.QuoteArg(rule.Pattern)}";
        var preview = await bus.ExecuteAsync(command + " apply=false", "UI");
        if (!preview.Success || preview.Data is not GitFileRulePreview { Changed: true })
        {
            StatusText.Text = preview.Success ? "规则不存在或无需删除" : "删除预览失败，详见控制台";
            return;
        }
        var applied = await bus.ExecuteAsync(command + " apply=true", "UI");
        StatusText.Text = applied.Success ? "托管规则已删除" : "规则删除失败，详见控制台";
        if (applied.Success)
            await LoadRulesAsync(project, refresh: true);
    }

    private async void OnReloadRulesClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            await LoadRulesAsync(project);
    }

    // ---------------------------------------------------------------- V2.2.1 全覆盖扫描入口

    /// <summary>强制重扫本项目并把完整格式台账重新合并进规则表。</summary>
    private async void OnScanCoverageClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_busAccessor() is not { } bus || CurrentProjectName() is not { Length: > 0 } project)
            return;

        ScanCoverageButton.IsEnabled = false;
        CoverageText.Text = "扫描中…";
        try
        {
            await LoadRulesAsync(project, refresh: true);
        }
        finally
        {
            ScanCoverageButton.IsEnabled = true;
        }
    }

    private void OnShowGapsClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            _ = _busAccessor()?.ExecuteAsync($"git.rule.gaps name={CommandParser.QuoteArg(project)}", "UI");
    }

    private void OnSuggestClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            _ = _busAccessor()?.ExecuteAsync($"git.rule.suggest name={CommandParser.QuoteArg(project)}", "UI");
    }

    /// <summary>基线同步只发预览:写入需在控制台显式 apply=true(人在环上)。</summary>
    private void OnSyncBaselineClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            _ = _busAccessor()?.ExecuteAsync(
                $"git.rule.sync name={CommandParser.QuoteArg(project)} apply=false", "UI");
    }

    private string NewProjectName() => NewProjectNameBox.Text.Trim();

    private string CurrentProjectName() => CurrentProjectBox.Text.Trim();

    private void UpdateProjectActions()
    {
        var hasCurrent = CurrentProjectName().Length > 0;
        var mode = CurrentOperationMode();
        var scopeReady = !ProjectOperationCommandBuilder.RequiresCurrentProject(mode) || hasCurrent;
        CreateProjectButton.IsEnabled = hasCurrent && NewProjectName().Length > 0;
        OpenProjectButton.IsEnabled = hasCurrent;
        SelectedActionTitle.Text = !ProjectOperationCommandBuilder.RequiresCurrentProject(mode)
            ? "全部工作树"
            : hasCurrent
            ? $"当前项目 · {CurrentProjectName()}"
            : "当前未选择项目";
        SelectedCommitButton.IsEnabled = !_projectOperationRunning && scopeReady
                                         && SelectedCommitMessageBox.Text.Trim().Length > 0;
        SelectedPushButton.IsEnabled = !_projectOperationRunning && scopeReady;
        SelectedCommitMessageBox.IsEnabled = !_projectOperationRunning;
        CurrentSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        CurrentBothModeButton.IsEnabled = !_projectOperationRunning;
        AllSubmodulesModeButton.IsEnabled = !_projectOperationRunning;
        AllBothModeButton.IsEnabled = !_projectOperationRunning;
    }

    private void SetProjectOperationRunning(bool running)
    {
        _projectOperationRunning = running;
        UpdateProjectActions();
    }

    private void ClearRules()
    {
        _ruleLoadGeneration++;
        _rules.Clear();
        RulePanel.IsEnabled = false;
        RuleTitle.Text = "Git 文件规则";
        CoverageText.Text = "格式覆盖：选择项目后自动读取全部文件格式";
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private ProjectOperationMode CurrentOperationMode()
    {
        if (CurrentSubmodulesModeButton.IsChecked == true)
            return ProjectOperationMode.CurrentSubmodules;
        if (AllSubmodulesModeButton.IsChecked == true)
            return ProjectOperationMode.AllSubmodules;
        if (AllBothModeButton.IsChecked == true)
            return ProjectOperationMode.AllBoth;
        return ProjectOperationMode.CurrentBoth;
    }

    public sealed class RuleEditRow : INotifyPropertyChanged
    {
        private bool? _track;
        private bool? _lfs;
        private bool? _lf;

        public RuleEditRow(FormatRow? format, GitFileRuleInfo? rule)
        {
            if (format == null && rule == null)
                throw new ArgumentException("格式与规则不能同时为空");

            Pattern = format?.Format ?? rule!.Pattern;
            IsScanned = format != null;
            IsReadOnly = Pattern.Equals(FormatInventoryService.NoExtension, StringComparison.OrdinalIgnoreCase);
            IsDeclared = rule != null || format?.Track.HasValue == true;
            _track = rule?.Track ?? format?.Track;
            _lfs = rule?.Lfs ?? format?.Lfs;
            _lf = rule?.Lf ?? format?.Lf;
            FileCount = format?.FileCount ?? rule?.FileCount ?? 0;
            UndecidedCount = format?.UndecidedCount ?? 0;
            IsMixed = format?.RuleState.StartsWith("混合", StringComparison.Ordinal) == true;

            Source = rule != null
                ? (rule.Managed ? "托管规则" : "手写规则") + (FileCount == 0 ? "（0 文件）" : "")
                : IsDeclared ? "扫描规则" : IsReadOnly ? "扫描汇总" : "扫描发现";

            var inventoryStatus = format == null
                ? null
                : $"跟踪 {format.TrackedCount} / 忽略 {format.IgnoredCount} / 未决 {format.UndecidedCount}";
            Status = string.Join("；", new[] { inventoryStatus, rule?.Status ?? format?.RuleState }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        public RuleEditRow(string pattern)
        {
            Pattern = pattern;
            _track = true;
            _lfs = false;
            _lf = false;
            Source = "手动新增";
            Status = "新规则，尚未保存";
            IsDraft = true;
        }

        public string Pattern { get; }
        public string Source { get; }
        public int FileCount { get; }
        public int UndecidedCount { get; }
        public string Status { get; }
        public bool IsDraft { get; }
        public bool IsDeclared { get; }
        public bool IsScanned { get; }
        public bool IsReadOnly { get; }
        public bool IsMixed { get; }
        public bool CanEdit => !IsReadOnly;
        public bool CanEditAttributes => CanEdit && Track == true;
        public int SortGroup => !IsDeclared && IsScanned ? 0
            : IsMixed ? 1
            : IsDeclared && FileCount > 0 ? 2
            : IsDeclared ? 3
            : 4;

        public string StorageResult => IsReadOnly
            ? "仅统计（无扩展名，按目录规则管理）"
            : IsMixed
                ? "混合状态（受路径规则或多条属性影响）"
                : Track switch
                {
                    false => "忽略（不进入 Git）",
                    true when Lfs == true => "Git + LFS（仓库存指针）",
                    true when Lf == true => "Git + LF（文本，非 LFS 指针）",
                    true when Lfs == false && Lf == false => "普通 Git（非 LFS，未强制 LF）",
                    _ => "未决（扫描发现，尚未声明规则）",
                };

        public bool? Track
        {
            get => _track;
            set
            {
                if (!Set(ref _track, value))
                    return;
                if (value == true)
                {
                    if (_lfs == null)
                        Lfs = false;
                    if (_lf == null)
                        Lf = false;
                }
                else if (value == false)
                {
                    Lfs = false;
                    Lf = false;
                }
                NotifyState();
            }
        }

        public bool? Lfs
        {
            get => _lfs;
            set
            {
                if (!Set(ref _lfs, value) || value != true)
                {
                    NotifyState();
                    return;
                }
                Track = true;
                Lf = false;
                NotifyState();
            }
        }

        public bool? Lf
        {
            get => _lf;
            set
            {
                if (!Set(ref _lf, value) || value != true)
                {
                    NotifyState();
                    return;
                }
                Track = true;
                Lfs = false;
                NotifyState();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        private void NotifyState()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditAttributes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StorageResult)));
        }
    }
}
