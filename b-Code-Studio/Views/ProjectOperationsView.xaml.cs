using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

/// <summary>项目创建、提交推送和所选项目的三状态 Git 文件格式规则。</summary>
public partial class ProjectOperationsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly ProjectSelectionState _selection;
    private readonly ObservableCollection<RuleEditRow> _rules = [];
    private bool _suppressProjectSelection;
    private bool _projectOperationRunning;
    private bool _ruleOperationRunning;
    private string? _loadedRuleProject;
    private int _ruleLoadGeneration;

    public ProjectOperationsView(Func<CommandBus?> busAccessor, ProjectSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        SelectedCommitMessageBox.Text = "一键推送更新";
        RuleGrid.ItemsSource = _rules;
        InitializeRuleAutoSave();
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
            if (!result.Success || !ModuleResultData.TryRead(result.Data, out List<WorktreeInfo>? projects))
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
        var requested = CurrentProjectName();
        if (_loadedRuleProject is { Length: > 0 } loaded
            && !requested.Equals(loaded, StringComparison.OrdinalIgnoreCase)
            && !await EnsureDirtyRulesHandledAsync("切换项目"))
        {
            RestoreProjectSelection(loaded);
            return;
        }
        UpdateProjectActions();
        if (requested is { Length: > 0 } name)
        {
            _suppressProjectSelection = true;
            _selection.CurrentProjectName = name;
            _suppressProjectSelection = false;
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

        if (_loadedRuleProject is { Length: > 0 } loaded
            && !string.Equals(selected, loaded, StringComparison.OrdinalIgnoreCase)
            && !await EnsureDirtyRulesHandledAsync("切换项目"))
        {
            RestoreProjectSelection(loaded);
            return;
        }

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

    private void RestoreProjectSelection(string project)
    {
        _suppressProjectSelection = true;
        CurrentProjectBox.SelectedItem = project;
        CurrentProjectBox.Text = project;
        _selection.CurrentProjectName = project;
        _suppressProjectSelection = false;
        UpdateProjectActions();
    }

    private void OnNewProjectNameChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnCommitMessageChanged(object sender, TextChangedEventArgs e)
        => UpdateProjectActions();

    private void OnOperationModeChanged(object sender, System.Windows.RoutedEventArgs e)
        => UpdateProjectActions();

    private async void OnRefreshProjectsClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (await EnsureDirtyRulesHandledAsync("刷新项目列表"))
            await RefreshProjectsAsync();
    }

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

    private string NewProjectName() => NewProjectNameBox.Text.Trim();

    private string CurrentProjectName() => CurrentProjectBox.Text.Trim();

    private void UpdateProjectActions()
    {
        var hasCurrent = CurrentProjectName().Length > 0;
        var mode = CurrentOperationMode();
        var scopeReady = !ProjectOperationCommandBuilder.RequiresCurrentProject(mode) || hasCurrent;
        CreateProjectButton.IsEnabled = hasCurrent && NewProjectName().Length > 0;
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

}
