using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

public partial class ProjectOperationsView
{
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
        DetachRuleRows();
        if (scan.Success && scan.Data is InventoryReport report
            && list.Success && list.Data is IReadOnlyList<GitFileRuleInfo> declared)
        {
            foreach (var row in MergeRows(report, declared))
            {
                row.PropertyChanged += OnRuleRowChanged;
                _rules.Add(row);
            }
            _loadedRuleProject = project;
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
        UpdateRuleActions();
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
        draft.PropertyChanged += OnRuleRowChanged;
        _rules.Add(draft);
        RuleGrid.SelectedItem = draft;
        RuleGrid.ScrollIntoView(draft);
        StatusText.Text = "新规则尚未保存";
        UpdateRuleActions();
    }

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e)
        => UpdateRuleActions();

    private async void OnSaveRuleClick(object sender, System.Windows.RoutedEventArgs e)
        => _ = await SaveDirtyRulesAsync(_loadedRuleProject ?? CurrentProjectName(), refreshAfter: true);

    private async Task<bool> SaveDirtyRulesAsync(string project, bool refreshAfter)
    {
        if (_busAccessor() is not { } bus || string.IsNullOrWhiteSpace(project))
            return false;

        RuleGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var dirty = _rules.Where(row => row.CanEdit && row.IsDirty).ToList();
        if (dirty.Count == 0)
            return true;
        var invalid = dirty.Where(row => !row.IsValid).Select(row => row.Pattern).ToList();
        if (invalid.Count > 0)
        {
            StatusText.Text = $"请先明确这些规则的 Git、LFS 与 LF 状态：{string.Join("、", invalid)}";
            return false;
        }

        var changes = dirty.Select(row => new GitFileRuleChange(
            row.Pattern, row.Track!.Value, row.Lfs!.Value, row.Lf!.Value)).ToList();
        var json = JsonSerializer.Serialize(changes, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        var command = $"git.rule.batch-set name={CommandParser.QuoteArg(project)} " +
                      $"changes={CommandParser.QuoteArg(json)}";

        SetRuleOperationRunning(true);
        try
        {
            var preview = await bus.ExecuteAsync(command + " apply=false", "UI");
            if (!preview.Success || preview.Data is not GitFileRuleBatchPreview batch)
            {
                StatusText.Text = "批量规则预览失败，修改仍保留；详见控制台";
                return false;
            }
            if (!batch.Changed)
            {
                foreach (var row in dirty)
                    row.AcceptChanges();
                StatusText.Text = $"{dirty.Count} 条规则已与仓库一致";
                return true;
            }

            var applied = await bus.ExecuteAsync(command + " apply=true", "UI");
            if (!applied.Success)
            {
                StatusText.Text = $"批量保存失败，{dirty.Count} 条修改仍保留；详见控制台";
                return false;
            }
            foreach (var row in dirty)
                row.AcceptChanges();
            StatusText.Text = $"已保存 {dirty.Count} 条规则";
            if (refreshAfter)
                await LoadRulesAsync(project, refresh: true);
            return true;
        }
        finally
        {
            SetRuleOperationRunning(false);
        }
    }

    private async void OnDeleteRuleClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RuleGrid.SelectedItem is not RuleEditRow rule || CurrentProjectName() is not { Length: > 0 } project
            || _busAccessor() is not { } bus)
            return;
        if (rule.IsDraft)
        {
            rule.PropertyChanged -= OnRuleRowChanged;
            _rules.Remove(rule);
            UpdateRuleActions();
            return;
        }
        if (!rule.IsDeclared)
        {
            StatusText.Text = "该格式由扫描发现，当前没有可删除的规则";
            return;
        }
        if (!await EnsureDirtyRulesHandledAsync("删除规则"))
            return;
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

    /// <summary>强制重扫本项目，并把台账与声明规则原子替换进表格。</summary>
    private async void OnRefreshRulesClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is not { Length: > 0 } project)
            return;
        if (!await EnsureDirtyRulesHandledAsync("刷新规则"))
            return;
        await LoadRulesAsync(project, refresh: true);
    }

    private void OnReviewRulesClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            _ = _busAccessor()?.ExecuteAsync($"git.rule.review name={CommandParser.QuoteArg(project)}", "UI");
    }

    /// <summary>基线同步只发预览:写入需在控制台显式 apply=true(人在环上)。</summary>
    private void OnSyncBaselineClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (CurrentProjectName() is { Length: > 0 } project)
            _ = _busAccessor()?.ExecuteAsync(
                $"git.rule.sync name={CommandParser.QuoteArg(project)} apply=false", "UI");
    }

    private async Task<bool> EnsureDirtyRulesHandledAsync(string action)
    {
        RuleGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RuleGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var count = _rules.Count(row => row.CanEdit && row.IsDirty);
        if (count == 0)
            return true;

        var choice = MessageBox.Show(
            $"当前有 {count} 条文件规则尚未保存。\n\n是：保存后{action}\n否：放弃修改后{action}\n取消：留在当前页面",
            "未保存的文件规则",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Cancel)
            return false;
        if (choice == MessageBoxResult.Yes)
            return await SaveDirtyRulesAsync(_loadedRuleProject ?? CurrentProjectName(), refreshAfter: false);

        DiscardRuleChanges();
        return true;
    }

    private void DiscardRuleChanges()
    {
        foreach (var draft in _rules.Where(row => row.IsDraft).ToList())
        {
            draft.PropertyChanged -= OnRuleRowChanged;
            _rules.Remove(draft);
        }
        foreach (var row in _rules.Where(row => row.IsDirty))
            row.ResetChanges();
        UpdateRuleActions();
    }

    private void OnRuleRowChanged(object? sender, PropertyChangedEventArgs e)
        => UpdateRuleActions();

    private void UpdateRuleActions()
    {
        if (SaveRuleButton == null)
            return;
        var dirtyCount = _rules.Count(row => row.CanEdit && row.IsDirty);
        SaveRuleButton.Content = $"保存修改（{dirtyCount}）";
        SaveRuleButton.IsEnabled = !_ruleOperationRunning && dirtyCount > 0;
        var selected = RuleGrid.SelectedItem as RuleEditRow;
        DeleteRuleButton.IsEnabled = !_ruleOperationRunning && selected is { CanEdit: true }
                                     && (selected.IsDeclared || selected.IsDraft);
    }

    private void SetRuleOperationRunning(bool running)
    {
        _ruleOperationRunning = running;
        RuleGrid.IsEnabled = !running;
        PatternBox.IsEnabled = !running;
        AddRuleButton.IsEnabled = !running;
        RefreshRulesButton.IsEnabled = !running;
        ReviewRulesButton.IsEnabled = !running;
        SyncBaselineButton.IsEnabled = !running;
        CurrentProjectBox.IsEnabled = !running;
        RefreshProjectsButton.IsEnabled = !running;
        UpdateRuleActions();
    }

    private void DetachRuleRows()
    {
        foreach (var row in _rules)
            row.PropertyChanged -= OnRuleRowChanged;
        _rules.Clear();
    }

    private void ClearRules()
    {
        _ruleLoadGeneration++;
        DetachRuleRows();
        _loadedRuleProject = null;
        RulePanel.IsEnabled = false;
        RuleTitle.Text = "Git 文件规则";
        CoverageText.Text = "格式覆盖：选择项目后自动读取全部文件格式";
        UpdateRuleActions();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    public sealed class RuleEditRow : INotifyPropertyChanged
    {
        private bool? _track;
        private bool? _lfs;
        private bool? _lf;
        private bool? _originalTrack;
        private bool? _originalLfs;
        private bool? _originalLf;
        private bool _isDraft;
        private bool _isDeclared;

        public RuleEditRow(FormatRow? format, GitFileRuleInfo? rule)
        {
            if (format == null && rule == null)
                throw new ArgumentException("格式与规则不能同时为空");

            Pattern = format?.Format ?? rule!.Pattern;
            IsScanned = format != null;
            IsReadOnly = Pattern.Equals(FormatInventoryService.NoExtension, StringComparison.OrdinalIgnoreCase);
            _isDeclared = rule != null || format?.Track.HasValue == true;
            _track = rule?.Track ?? format?.Track;
            _lfs = rule?.Lfs ?? format?.Lfs;
            _lf = rule?.Lf ?? format?.Lf;
            _originalTrack = _track;
            _originalLfs = _lfs;
            _originalLf = _lf;
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
            _isDraft = true;
        }

        public string Pattern { get; }
        public string Source { get; }
        public int FileCount { get; }
        public int UndecidedCount { get; }
        public string Status { get; }
        public bool IsDraft => _isDraft;
        public bool IsDeclared => _isDeclared;
        public bool IsScanned { get; }
        public bool IsReadOnly { get; }
        public bool IsMixed { get; }
        public bool CanEdit => !IsReadOnly;
        public bool CanEditAttributes => CanEdit && Track == true;
        public bool IsValid => Track.HasValue && Lfs.HasValue && Lf.HasValue
                               && (Track.Value || !Lfs.Value && !Lf.Value)
                               && !(Lfs.Value && Lf.Value);
        public bool IsDirty => CanEdit && (_isDraft
            || Track != _originalTrack || Lfs != _originalLfs || Lf != _originalLf);
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

        public void AcceptChanges()
        {
            _originalTrack = Track;
            _originalLfs = Lfs;
            _originalLf = Lf;
            _isDraft = false;
            _isDeclared = true;
            NotifyState();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDraft)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDeclared)));
        }

        public void ResetChanges()
        {
            _track = _originalTrack;
            _lfs = _originalLfs;
            _lf = _originalLf;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Track)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lfs)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lf)));
            NotifyState();
        }

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsValid)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
        }
    }
}
