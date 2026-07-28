using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using AppShell.Core.Commands;
using OneHistoryStudio.Connection;

namespace OneHistoryStudio.Views;

public partial class ConnectionSettingsView : UserControl
{
    private readonly ConnectionProfileService _profiles;
    private readonly Func<CommandBus?> _busAccessor;
    private bool _loaded;

    public ConnectionSettingsView(
        ConnectionProfileService profiles,
        Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _profiles = profiles;
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
        var status = _profiles.Status();
        ServerRole.IsChecked = status.Role == NodeRole.Server;
        ClientRole.IsChecked = status.Role == NodeRole.Client;
        EndpointBox.Text = status.Endpoint;
        FingerprintBox.Text = status.CertificateFingerprint ?? "";
        DeviceNameBox.Text = Environment.MachineName;
        LoadPrivateAddresses();
        ConnectionStateText.Text = $"{status.Role} · {status.ConnectionState} · {status.Endpoint}";
        ClientIdentityText.Text =
            $"Device ID: {status.DeviceId}\nServer ID: {status.ServerId ?? "(尚未配对)"}";
        UpdateRolePanels();

        if (status.Role == NodeRole.Server && _busAccessor() is { } bus)
        {
            var devices = await bus.ExecuteAsync("lan.device.list", "UI");
            if (devices.Data is IReadOnlyList<LanDeviceInfo> values)
                DevicesGrid.ItemsSource = values;
            var lan = await bus.ExecuteAsync("lan.status", "UI");
            if (lan.Data is LanServerStatus server)
            {
                ServerStatusText.Text = server.Running
                    ? $"运行中 · 客户端 {server.Clients} · Shell {server.Shells} · Server ID {server.ServerId}"
                    : $"未运行 · Server ID {server.ServerId}";
                LanEnabledCheck.IsChecked = server.Configuration.Enabled;
                LanBindBox.Text = server.Configuration.BindAddress;
                LanPortBox.Text = server.Configuration.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                CertificateStatusText.Text =
                    $"证书 SHA-256: {server.Configuration.CertificateThumbprint ?? "(未配置)"}\n" +
                    $"到期: {server.Configuration.CertificateExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(未知)"} · " +
                    $"防火墙: {server.Configuration.FirewallStatus}";
            }
            else
            {
                ServerStatusText.Text = lan.Message;
            }
        }
    }

    private void OnRoleChanged(object sender, RoutedEventArgs e) => UpdateRolePanels();

    private void UpdateRolePanels()
    {
        if (ClientPanel == null || ServerPanel == null)
            return;
        var client = ClientRole.IsChecked == true;
        ClientPanel.Visibility = client ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = client ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var role = ClientRole.IsChecked == true ? NodeRole.Client : NodeRole.Server;
            var desired = _profiles.Prepare(role, EndpointBox.Text, FingerprintBox.Text);
            var preview = role == NodeRole.Client
                ? $"当前设备将切换为客户端。\n服务器：{desired.ServerEndpoint}\n" +
                  $"证书 pin：{desired.CertificateFingerprint}\n\n保存后立即重启 OneHistoryStudio。"
                : "当前设备将切换为服务器端。\n\n保存后立即重启 OneHistoryStudio。";
            if (MessageBox.Show(preview, "角色配置预览",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                StatusText.Text = "角色配置未保存";
                return;
            }

            _profiles.Apply(desired);
            StatusText.Text = "本机角色配置已保存，正在重启 OneHistoryStudio...";
            RestartApplication();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static void RestartApplication()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("无法确定 OneHistoryStudio.exe 路径，配置已保存，请手动重启");
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        var code = PairCodeBox.Password;
        PairCodeBox.Clear();
        var result = await _profiles.PairAsync(
            code, FingerprintBox.Text, DeviceNameBox.Text);
        StatusText.Text = result.Success
            ? "设备配对成功；token 已用当前用户 DPAPI 保存"
            : result.Failure ?? "设备配对失败";
        await RefreshAsync();
    }

    private async void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = await _profiles.ReconnectAsync()
            ? "服务器连接已恢复"
            : "服务器仍未连接";
        await RefreshAsync();
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确认断开并清除本机设备 token 与服务器证书 pin？",
                "连接与端口",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _profiles.Forget();
        StatusText.Text = "本机服务器配对已清除";
        await RefreshAsync();
    }

    private async void OnGeneratePairCodeClick(object sender, RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        var result = await bus.ExecuteAsync("lan.paircode", "UI");
        if (result.Data is LanPairCode code)
            PairCodeText.Text = $"配对码：{code.Code}  ·  {code.ExpiresAt.ToLocalTime():HH:mm:ss} 前有效";
        StatusText.Text = result.Message;
    }

    private async void OnApplyLanClick(object sender, RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        if (!int.TryParse(LanPortBox.Text, out var port))
        {
            StatusText.Text = "HTTPS 端口必须是整数";
            return;
        }
        var enabled = LanEnabledCheck.IsChecked == true;
        var preview = await bus.ExecuteAsync(
            $"lan.configure enabled={enabled.ToString().ToLowerInvariant()} bind={LanBindBox.Text} port={port}",
            "UI");
        if (!preview.Success)
        {
            StatusText.Text = preview.Message;
            return;
        }
        if (MessageBox.Show(
                preview.Message + "\n\n继续后 Windows 将显示 UAC 确认。",
                "连接与端口",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        StatusText.Text = "正在等待 UAC 与机器配置结果...";
        var result = await bus.ExecuteAsync(
            $"lan.configure enabled={enabled.ToString().ToLowerInvariant()} bind={LanBindBox.Text} port={port} apply=true",
            "UI");
        StatusText.Text = result.Message;
        await RefreshAsync();
    }

    private async void OnRotateCertificateClick(object sender, RoutedEventArgs e)
    {
        var bus = _busAccessor();
        if (bus == null)
            return;
        if (MessageBox.Show(
                "轮换证书后所有客户端都必须重新核对 SHA-256 指纹。继续？",
                "连接与端口",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        StatusText.Text = "正在等待 UAC 与证书轮换结果...";
        var result = await bus.ExecuteAsync("lan.cert.rotate apply=true", "UI");
        StatusText.Text = result.Message;
        await RefreshAsync();
    }

    private void LoadPrivateAddresses()
    {
        var current = LanBindBox.Text;
        var values = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                              && !IPAddress.IsLoopback(address)
                              && IsPrivate(address))
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToList();
        LanBindBox.ItemsSource = values;
        if (!string.IsNullOrWhiteSpace(current))
            LanBindBox.Text = current;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168;
    }
}
