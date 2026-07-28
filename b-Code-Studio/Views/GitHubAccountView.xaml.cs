using System.Windows;
using System.Windows.Controls;
using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Views;

public partial class GitHubAccountView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private bool _loaded;
    private bool _busy;

    public GitHubAccountView(Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshAsync();
        };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        var result = await ExecuteAsync("github.status refresh=true");
        if (result?.Success != true || result.Data is not GitHubAccountOverview overview)
            return;
        ConnectionText.Text = $"{overview.Origin.Transport} · {overview.Connection.State} · Git {overview.GitVersion}";
        OriginText.Text = overview.Origin.FetchUrl;
        CheckedText.Text = overview.LastCheckedAt.ToString("yyyy-MM-dd HH:mm:ss");
        AccountsGrid.ItemsSource = overview.CredentialAccounts;
        KeysGrid.ItemsSource = overview.Ssh.PublicKeys;
        DiagnosticsGrid.ItemsSource = overview.Connection.Steps;
        IdentityName.Text = overview.EffectiveIdentity.Name;
        IdentityEmail.Text = overview.EffectiveIdentity.Email;
        IdentitySourceText.Text = $"当前来源：{overview.EffectiveIdentity.Source}";
        FetchUrl.Text = overview.Origin.FetchUrl;
        PushUrl.Text = overview.Origin.PushUrl;
        RepositoryText.Text = $"{overview.Origin.Owner}/{overview.Origin.Repository}";
    }

    private async Task<CommandResult?> ExecuteAsync(string command)
    {
        var bus = _busAccessor();
        if (bus == null)
        {
            StatusText.Text = "命令总线尚未就绪";
            return null;
        }
        SetBusy(true);
        try
        {
            var result = await bus.ExecuteAsync(command, "UI");
            StatusText.Text = result.Message;
            return result;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        IsEnabled = !value;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var result = await ExecuteAsync("github.test transport=auto timeout=15");
        if (result?.Data is GitHubConnectionStatus connection)
        {
            ConnectionText.Text = $"{connection.Transport} · {connection.State}";
            DiagnosticsGrid.ItemsSource = connection.Steps;
        }
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync("github.login");
        await RefreshAsync();
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not GitCredentialAccount account)
        {
            StatusText.Text = "请先选择要注销的 GCM 账号";
            return;
        }
        await ExecuteAsync($"github.logout account={CommandParser.QuoteArg(account.Account)} apply=true");
        await RefreshAsync();
    }

    private string IdentityCommand(bool apply)
    {
        var scope = (IdentityScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "repository";
        return $"github.identity name={CommandParser.QuoteArg(IdentityName.Text)} " +
               $"email={CommandParser.QuoteArg(IdentityEmail.Text)} scope={scope} apply={apply.ToString().ToLowerInvariant()}";
    }

    private async void OnIdentityPreviewClick(object sender, RoutedEventArgs e)
        => await ExecuteAsync(IdentityCommand(false));

    private async void OnIdentityApplyClick(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(IdentityCommand(true));
        await RefreshAsync();
    }

    private string RemoteCommand(bool apply)
        => $"github.remote fetch={CommandParser.QuoteArg(FetchUrl.Text)} " +
           $"push={CommandParser.QuoteArg(PushUrl.Text)} apply={apply.ToString().ToLowerInvariant()}";

    private async void OnRemotePreviewClick(object sender, RoutedEventArgs e)
        => await ExecuteAsync(RemoteCommand(false));

    private async void OnRemoteApplyClick(object sender, RoutedEventArgs e)
    {
        await ExecuteAsync(RemoteCommand(true));
        await RefreshAsync();
    }
}
