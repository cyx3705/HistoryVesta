using System.Windows;
using System.Windows.Controls;

namespace GitHubConnection.Views;

public partial class GitHubConnectionView : UserControl
{
    private readonly GitHubConnectionService _service;
    private bool _loaded;
    private bool _busy;

    public GitHubConnectionView(GitHubConnectionService service)
    {
        InitializeComponent();
        _service = service;
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
        await RunAsync(async () =>
        {
            var overview = await _service.GetOverviewAsync();
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
            StatusText.Text = "已刷新服务器 GitHub 事实";
        });
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var connection = await _service.TestAsync("auto", 15);
            ConnectionText.Text = $"{connection.Transport} · {connection.State}";
            DiagnosticsGrid.ItemsSource = connection.Steps;
            StatusText.Text = connection.State == "ok"
                ? "GitHub 连接检测通过"
                : $"GitHub 连接检测：{connection.State}";
        });
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("确认在服务器本机启动 Git Credential Manager 登录？"))
            return;
        var succeeded = false;
        await RunAsync(async () =>
        {
            var result = await _service.LoginAsync(null);
            if (!result.Success)
                throw new InvalidOperationException(result.CombinedOutput);
            StatusText.Text = "GCM 登录流程已完成";
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (AccountsGrid.SelectedItem is not GitCredentialAccount account)
        {
            StatusText.Text = "请先选择要注销的 GCM 账号";
            return;
        }
        if (!Confirm($"确认注销服务器 GCM 账号 {account.Account}？"))
            return;
        var succeeded = false;
        await RunAsync(async () =>
        {
            var result = await _service.LogoutAsync(account.Account, apply: true);
            StatusText.Text = result.Action;
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private string IdentityScopeValue()
        => (IdentityScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "repository";

    private async void OnIdentityPreviewClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var result = await _service.SetIdentityAsync(
                IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: false);
            StatusText.Text = $"{result.Action}：{result.Before.Name} <{result.Before.Email}> → " +
                              $"{result.After.Name} <{result.After.Email}> [{result.After.Source}]";
        });
    }

    private async void OnIdentityApplyClick(object sender, RoutedEventArgs e)
    {
        GitHubMutationResult<GitIdentityInfo>? preview = null;
        await RunAsync(async () => preview = await _service.SetIdentityAsync(
            IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: false));
        if (preview == null || !Confirm(
                $"确认修改服务器 Git 提交身份？\n\n" +
                $"{preview.Before.Name} <{preview.Before.Email}>\n→\n" +
                $"{preview.After.Name} <{preview.After.Email}> [{preview.After.Source}]"))
            return;
        var succeeded = false;
        await RunAsync(async () =>
        {
            var result = await _service.SetIdentityAsync(
                IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: true);
            StatusText.Text = result.Action;
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async void OnRemotePreviewClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var result = await _service.SetRemoteAsync(FetchUrl.Text, PushUrl.Text, apply: false);
            StatusText.Text = $"{result.Action}：{result.Before.FetchUrl} → {result.After.FetchUrl}";
        });
    }

    private async void OnRemoteApplyClick(object sender, RoutedEventArgs e)
    {
        GitHubMutationResult<GitRemoteInfo>? preview = null;
        await RunAsync(async () => preview = await _service.SetRemoteAsync(
            FetchUrl.Text, PushUrl.Text, apply: false));
        if (preview == null || !Confirm(
                $"确认修改服务器 origin？\n\nFetch: {preview.Before.FetchUrl}\n→ {preview.After.FetchUrl}\n\n" +
                $"Push: {preview.Before.PushUrl}\n→ {preview.After.PushUrl}"))
            return;
        var succeeded = false;
        await RunAsync(async () =>
        {
            var result = await _service.SetRemoteAsync(FetchUrl.Text, PushUrl.Text, apply: true);
            StatusText.Text = result.Action;
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            StatusText.Text = GitHubRedactor.Redact(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        IsEnabled = !value;
    }

    private static bool Confirm(string message)
        => MessageBox.Show(
            message,
            "GitHub 连接",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
