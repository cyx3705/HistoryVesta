using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Mcp;

namespace OneHistoryStudio.Views;

/// <summary>所选指令的 Help、MCP 映射与提示词治理详情。</summary>
public partial class CommandDetailView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly CommandSelectionState _selection;
    private CommandCatalogRow? _current;

    public CommandDetailView(Func<CommandBus?> busAccessor, CommandSelectionState selection)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selection.Changed -= OnSelectionChanged;
        _selection.Changed += OnSelectionChanged;
        await LoadSelectionAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _selection.Changed -= OnSelectionChanged;

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var unloaded = _current != null && _selection.CurrentCommandName == null;
        Dispatcher.BeginInvoke(async () =>
        {
            if (unloaded && _selection.CurrentCommandName == null)
                ClearDetails("指令已卸载");
            else
                await LoadSelectionAsync();
        });
    }

    private bool IsCurrent(string commandName)
        => string.Equals(_selection.CurrentCommandName, commandName, StringComparison.OrdinalIgnoreCase);

    private async Task LoadSelectionAsync()
    {
        var commandName = _selection.CurrentCommandName;
        if (commandName == null)
        {
            ClearDetails("(在命令集窗口选择指令)");
            return;
        }

        if (_busAccessor() is not { } bus)
            return;

        var name = CommandParser.QuoteArg(commandName);
        var commandResult = await bus.ExecuteAsync($"command.show name={name}", "UI");
        if (!IsCurrent(commandName))
            return;
        if (!commandResult.Success || commandResult.Data is not CommandCatalogDetail detail)
        {
            _selection.CurrentCommandName = null;
            ClearDetails("指令已卸载");
            return;
        }

        _current = detail.Command;
        DetailTabs.IsEnabled = true;
        DetailTitle.Text = detail.Command.SourceDetail == null
            ? $"{detail.Command.CommandName}  [{detail.Command.Source} / {detail.Command.McpState}]"
            : $"{detail.Command.CommandName}  [module:{detail.Command.SourceDetail} / {detail.Command.McpState}]";
        SummaryBox.Text = detail.Command.Summary;
        ExampleBox.Text = detail.Command.Example ?? "(无示例)";
        ParameterList.ItemsSource = detail.Parameters;
        CopyExampleButton.IsEnabled = !string.IsNullOrWhiteSpace(detail.Command.Example);
        var reason = detail.Command.HardExclusionReason == null
            ? string.Empty
            : $"\n硬排除原因: {detail.Command.HardExclusionReason}";
        McpStatusText.Text = $"状态: {detail.Command.McpState} | 当前策略可见: " +
                             $"{(detail.Command.PolicyVisible ? "是" : "否")} | " +
                             $"工具名: {detail.Command.McpToolName ?? "(无)"}{reason}";
        SchemaBox.Text = detail.McpInputSchema ?? "该指令没有 MCP 工具形态";
        ShowSchemaButton.IsEnabled = detail.Command.McpToolName != null;

        if (detail.Command.McpToolName == null)
        {
            RevisionStatusText.Text = "该指令被 MCP 硬排除，不参与提示词治理";
            DefaultDescBox.Text = detail.Command.Summary +
                                  (detail.Command.Example == null ? "" : $"\n示例: {detail.Command.Example}");
            DescBox.Text = DefaultDescBox.Text;
            SetGovernanceEnabled(false);
            ClearGovernanceCollections();
            return;
        }

        SetGovernanceEnabled(true);
        var statusResult = await bus.ExecuteAsync($"prompt.get name={name}", "UI");
        var historyResult = await bus.ExecuteAsync($"prompt.history name={name} limit=50", "UI");
        var proposalsResult = await bus.ExecuteAsync($"mcp.pending name={name} limit=100", "UI");
        var correctionsResult = await bus.ExecuteAsync($"correction.list name={name} limit=100", "UI");
        var incidentsResult = await bus.ExecuteAsync($"incident.list name={name} limit=100", "UI");
        if (!IsCurrent(commandName))
            return;

        if (statusResult.Data is PromptStatus status)
        {
            RevisionStatusText.Text =
                $"当前修订: {status.CurrentRevision?.Id ?? "(默认)"} | 待处理提案: {status.OpenProposals}";
            DefaultDescBox.Text = status.DefaultDescription;
            DescBox.Text = status.EffectiveDescription;
            ResetButton.IsEnabled = status.CurrentRevision?.Description != null;
        }
        RevisionList.ItemsSource = historyResult.Data as IReadOnlyList<PromptRevision> ?? [];
        ProposalList.ItemsSource = proposalsResult.Data as IReadOnlyList<PromptProposal> ?? [];
        CorrectionList.ItemsSource = correctionsResult.Data as IReadOnlyList<PromptCorrection> ?? [];
        IncidentList.ItemsSource = incidentsResult.Data as IReadOnlyList<PromptIncident> ?? [];
        RevisionList.SelectedItem = null;
        ProposalList.SelectedItem = null;
        RevertButton.IsEnabled = false;
        ClearProposalDetails();
    }

    private async void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (_current is { } row)
            await (_busAccessor()?.ExecuteAsync($"help {CommandParser.QuoteArg(row.CommandName)}", "UI")
                   ?? Task.FromResult(CommandResult.Fail("总线未就绪")));
    }

    private void OnCopyExampleClick(object sender, RoutedEventArgs e)
    {
        if (_current?.Example is not { Length: > 0 } example)
            return;
        Clipboard.SetText(example);
        StatusText.Text = "示例已复制";
    }

    private async void OnSchemaClick(object sender, RoutedEventArgs e)
    {
        if (_current is { McpToolName: not null } row)
            await (_busAccessor()?.ExecuteAsync(
                       $"mcp.schema name={CommandParser.QuoteArg(row.CommandName)}", "UI")
                   ?? Task.FromResult(CommandResult.Fail("总线未就绪")));
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { McpToolName: not null } row || _busAccessor() is not { } bus)
            return;
        var text = DescBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "描述不能为空；恢复默认请使用对应按钮";
            return;
        }
        var command = $"mcp.desc name={CommandParser.QuoteArg(row.CommandName)} " +
                      $"text={CommandParser.QuoteArg(text)} reason={CommandParser.QuoteArg("管理页本地修订")}";
        var result = await bus.ExecuteAsync(command, "UI");
        StatusText.Text = result.Success ? "本地修订已应用" : "应用失败，详见控制台";
        if (result.Success)
            await LoadSelectionAsync();
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { McpToolName: not null } row || _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"mcp.desc name={CommandParser.QuoteArg(row.CommandName)} reset=true " +
            $"reason={CommandParser.QuoteArg("管理页恢复默认")}", "UI");
        StatusText.Text = result.Success ? "已恢复默认描述" : "恢复失败，详见控制台";
        if (result.Success)
            await LoadSelectionAsync();
    }

    private void OnRevisionSelected(object sender, SelectionChangedEventArgs e)
        => RevertButton.IsEnabled = RevisionList.SelectedItem is PromptRevision;

    private async void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (RevisionList.SelectedItem is not PromptRevision revision || _busAccessor() is not { } bus)
            return;
        var reason = string.IsNullOrWhiteSpace(RevertReasonBox.Text)
            ? "管理页回滚历史修订"
            : RevertReasonBox.Text.Trim();
        var result = await bus.ExecuteAsync(
            $"mcp.revert revision={CommandParser.QuoteArg(revision.Id)} reason={CommandParser.QuoteArg(reason)}", "UI");
        StatusText.Text = result.Success ? "已生成回滚修订" : "回滚失败，详见控制台";
        if (result.Success)
            await LoadSelectionAsync();
    }

    private void OnProposalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProposalList.SelectedItem is not PromptProposal proposal)
        {
            ClearProposalDetails();
            return;
        }
        ProposalReasonBox.Text = proposal.Reason +
                                 (proposal.Evidence.Length > 0 ? $"\n\n证据:\n{proposal.Evidence}" : "");
        ProposalDiffBox.Text = "- " + proposal.OldText.Replace("\n", "\n- ") +
                               "\n+ " + proposal.ProposedText.Replace("\n", "\n+ ");
        ApproveButton.IsEnabled = proposal.Status == "pending";
        RejectButton.IsEnabled = proposal.Status is "pending" or "approved";
        ApplyButton.IsEnabled = proposal.Status == "approved";
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
        => await RunProposalActionAsync("mcp.approve", false);

    private async void OnRejectClick(object sender, RoutedEventArgs e)
        => await RunProposalActionAsync("mcp.reject", true);

    private async void OnApplyClick(object sender, RoutedEventArgs e)
        => await RunProposalActionAsync("mcp.apply", false);

    private async Task RunProposalActionAsync(string action, bool requireReason)
    {
        if (ProposalList.SelectedItem is not PromptProposal proposal || _busAccessor() is not { } bus)
            return;
        var command = $"{action} id={CommandParser.QuoteArg(proposal.Id)}";
        if (requireReason)
        {
            var reason = ReviewReasonBox.Text.Trim();
            if (reason.Length == 0)
            {
                StatusText.Text = "拒绝提案必须填写理由";
                return;
            }
            command += $" reason={CommandParser.QuoteArg(reason)}";
        }
        var result = await bus.ExecuteAsync(command, "UI");
        StatusText.Text = result.Success ? "提案状态已更新" : "操作失败，详见控制台";
        if (result.Success)
            await LoadSelectionAsync();
    }

    private void SetGovernanceEnabled(bool enabled)
    {
        DescBox.IsReadOnly = !enabled;
        SaveButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        RevertButton.IsEnabled = false;
    }

    private void ClearGovernanceCollections()
    {
        RevisionList.ItemsSource = null;
        ProposalList.ItemsSource = null;
        CorrectionList.ItemsSource = null;
        IncidentList.ItemsSource = null;
        ClearProposalDetails();
    }

    private void ClearDetails(string title)
    {
        _current = null;
        DetailTabs.IsEnabled = false;
        DetailTitle.Text = title;
        StatusText.Text = "";
        SummaryBox.Text = "";
        ParameterList.ItemsSource = null;
        ExampleBox.Text = "";
        McpStatusText.Text = "";
        SchemaBox.Text = "";
        RevisionStatusText.Text = "当前修订: (默认)";
        DefaultDescBox.Text = "";
        DescBox.Text = "";
        ClearGovernanceCollections();
    }

    private void ClearProposalDetails()
    {
        ProposalReasonBox.Text = "";
        ProposalDiffBox.Text = "";
        ApproveButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
    }
}
