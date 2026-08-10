using System.Windows;
using System.Windows.Controls;
using HistoryJanus.GitHub;

namespace HistoryJanus.Views;

public partial class GitHubConnectionView : UserControl
{
    private readonly Func<GitHubConnectionService?> _serviceAccessor;
    private bool _busy;
    private IReadOnlyList<GitHubDiagnosticStep> _diagnosticSteps = [];

    public GitHubConnectionView(Func<GitHubConnectionService?> serviceAccessor)
    {
        InitializeComponent();
        _serviceAccessor = serviceAccessor;
        ViewKit.RunOnceOnLoaded(this, RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        await RunAsync(async service =>
        {
            var overview = await service.GetOverviewAsync();
            ConnectionText.Text = $"{overview.Origin.Transport} · {overview.Connection.State} · Git {overview.GitVersion}";
            OriginText.Text = overview.Origin.FetchUrl;
            CheckedText.Text = overview.LastCheckedAt.ToString("yyyy-MM-dd HH:mm:ss");
            AccountsBox.ItemsSource = overview.CredentialAccounts;
            if (overview.CredentialAccounts.Count > 0)
                AccountsBox.SelectedIndex = 0;
            KeysBox.ItemsSource = overview.Ssh.PublicKeys;
            if (overview.Ssh.PublicKeys.Count > 0)
                KeysBox.SelectedIndex = 0;
            UpdateKeyFingerprint();
            _diagnosticSteps = overview.Connection.Steps;
            IdentityName.Text = overview.EffectiveIdentity.Name;
            IdentityEmail.Text = overview.EffectiveIdentity.Email;
            IdentitySourceText.Text = overview.EffectiveIdentity.Source;
            FetchUrl.Text = overview.Origin.FetchUrl;
            PushUrl.Text = overview.Origin.PushUrl;
            RepositoryText.Text = $"{overview.Origin.Owner}/{overview.Origin.Repository}";
        });
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async service =>
        {
            var connection = await service.TestAsync("auto", 15);
            ConnectionText.Text = $"{connection.Transport} · {connection.State}";
            _diagnosticSteps = connection.Steps;
            Notify($"检测完成：{connection.Transport} · {connection.State}");
        });
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_diagnosticSteps.Count == 0)
        {
            Notify("尚无诊断步骤，请先点击检测。");
            return;
        }

        var lines = _diagnosticSteps.Select(step =>
            $"{step.Step}\t{step.State}\t{step.DurationMs}ms\t{step.Detail}");
        Notify("诊断步骤\n\n" + string.Join("\n", lines));
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (!Confirm("确认在服务器本机启动 Git Credential Manager 登录？"))
            return;
        var succeeded = false;
        await RunAsync(async service =>
        {
            var result = await service.LoginAsync(null);
            if (!result.Success)
                throw new InvalidOperationException(result.CombinedOutput);
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (AccountsBox.SelectedItem is not GitCredentialAccount account)
        {
            Notify("请先选择要注销的 GCM 账号");
            return;
        }
        if (!Confirm($"确认注销服务器 GCM 账号 {account.Account}？"))
            return;
        var succeeded = false;
        await RunAsync(async service =>
        {
            await service.LogoutAsync(account.Account, apply: true);
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private void OnKeySelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateKeyFingerprint();

    private void UpdateKeyFingerprint()
    {
        KeyFingerprintText.Text = KeysBox.SelectedItem is SshPublicKeyInfo key
            ? key.Fingerprint
            : "-";
    }

    private string IdentityScopeValue()
        => (IdentityScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "repository";

    private async void OnIdentityPreviewClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async service =>
        {
            var result = await service.SetIdentityAsync(
                IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: false);
            Notify($"{result.Action}：{result.Before.Name} <{result.Before.Email}> → " +
                   $"{result.After.Name} <{result.After.Email}> [{result.After.Source}]");
        });
    }

    private async void OnIdentityApplyClick(object sender, RoutedEventArgs e)
    {
        GitHubMutationResult<GitIdentityInfo>? preview = null;
        await RunAsync(async service => preview = await service.SetIdentityAsync(
            IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: false));
        if (preview == null || !Confirm(
                $"确认修改服务器 Git 提交身份？\n\n" +
                $"{preview.Before.Name} <{preview.Before.Email}>\n→\n" +
                $"{preview.After.Name} <{preview.After.Email}> [{preview.After.Source}]"))
            return;
        var succeeded = false;
        await RunAsync(async service =>
        {
            await service.SetIdentityAsync(
                IdentityName.Text, IdentityEmail.Text, IdentityScopeValue(), apply: true);
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async void OnRemotePreviewClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async service =>
        {
            var result = await service.SetRemoteAsync(FetchUrl.Text, PushUrl.Text, apply: false);
            Notify($"{result.Action}：{result.Before.FetchUrl} → {result.After.FetchUrl}");
        });
    }

    private async void OnRemoteApplyClick(object sender, RoutedEventArgs e)
    {
        GitHubMutationResult<GitRemoteInfo>? preview = null;
        await RunAsync(async service => preview = await service.SetRemoteAsync(
            FetchUrl.Text, PushUrl.Text, apply: false));
        if (preview == null || !Confirm(
                $"确认修改服务器 origin？\n\nFetch: {preview.Before.FetchUrl}\n→ {preview.After.FetchUrl}\n\n" +
                $"Push: {preview.Before.PushUrl}\n→ {preview.After.PushUrl}"))
            return;
        var succeeded = false;
        await RunAsync(async service =>
        {
            await service.SetRemoteAsync(FetchUrl.Text, PushUrl.Text, apply: true);
            succeeded = true;
        });
        if (succeeded) await RefreshAsync();
    }

    private async Task RunAsync(Func<GitHubConnectionService, Task> operation)
    {
        if (_busy || _serviceAccessor() is not { } service)
            return;
        SetBusy(true);
        try
        {
            await operation(service);
        }
        catch (Exception ex)
        {
            Notify(GitHubRedactor.Redact(ex.Message), MessageBoxImage.Warning);
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

    private static void Notify(string message, MessageBoxImage image = MessageBoxImage.Information)
        => MessageBox.Show(message, "github", MessageBoxButton.OK, image);

    private static bool Confirm(string message)
        => MessageBox.Show(
            message,
            "github",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
