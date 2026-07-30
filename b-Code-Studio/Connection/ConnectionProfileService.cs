using AppShell.Core;
using AppShell.Services.Web;

namespace OneHistoryStudio.Connection;

public sealed record ConnectionProfileStatus(
    NodeRole Role,
    string Endpoint,
    string DeviceId,
    string? ServerId,
    string? CertificateFingerprint,
    ShellConnectionState ConnectionState);

public sealed class ConnectionProfileService
{
    private readonly BootstrapProfileStore _profiles;
    private readonly DpapiSecretStore _secrets;
    private readonly ShellServiceClient _client;
    private readonly LanDiscoveryClient _discovery;

    public ConnectionProfileService(
        BootstrapProfileStore profiles,
        DpapiSecretStore secrets,
        ShellServiceClient client,
        LanDiscoveryClient? discovery = null)
    {
        _profiles = profiles;
        _secrets = secrets;
        _client = client;
        _discovery = discovery ?? new LanDiscoveryClient();
    }

    public ConnectionProfileStatus Status()
    {
        var profile = _profiles.Load();
        return new ConnectionProfileStatus(
            profile.Role,
            profile.ServerEndpoint ?? "http://127.0.0.1:8738/",
            profile.DeviceId,
            profile.ServerId,
            profile.CertificateFingerprint,
            _client.State);
    }

    public BootstrapProfile SetRole(NodeRole role, bool apply)
    {
        var before = _profiles.Load();
        var desired = Prepare(before, role, before.ServerEndpoint, before.CertificateFingerprint);
        if (apply)
            Apply(before, desired);
        return desired;
    }

    public BootstrapProfile SetEndpoint(
        string url,
        string fingerprint,
        bool apply)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("服务器地址无效");
        BootstrapProfileStore.ValidateEndpoint(endpoint);
        var normalized = ShellEndpointProfile.NormalizeFingerprint(fingerprint);
        if (!endpoint.IsLoopback && normalized.Length != 64)
            throw new ArgumentException("非回环服务器必须填写 64 位 SHA-256 证书指纹");
        var before = _profiles.Load();
        var desired = Prepare(before, before.Role, endpoint.AbsoluteUri, normalized);
        if (apply)
            Apply(before, desired);
        return desired;
    }

    public BootstrapProfile Prepare(
        NodeRole role,
        string? url,
        string? fingerprint)
        => Prepare(_profiles.Load(), role, url, fingerprint);

    public void Apply(BootstrapProfile desired)
        => Apply(_profiles.Load(), desired);

    public async Task<DevicePairingResult> PairAsync(
        string code,
        string fingerprint,
        string deviceName,
        CancellationToken cancellation = default)
    {
        var profile = _profiles.Load();
        if (profile.Role != NodeRole.Client)
            return Reject("pairing requires client role");
        var supplied = ShellEndpointProfile.NormalizeFingerprint(fingerprint);
        if (supplied.Length != 64
            || !supplied.Equals(
                ShellEndpointProfile.NormalizeFingerprint(profile.CertificateFingerprint),
                StringComparison.Ordinal))
        {
            return Reject("certificate fingerprint does not match bootstrap pin");
        }

        var result = await _client.PairAsync(code, deviceName, cancellation).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.ServerId)
            || string.IsNullOrWhiteSpace(result.AccessToken))
            return result;
        _secrets.Write(result.ServerId, result.AccessToken);
        _profiles.Save(profile with { ServerId = result.ServerId });
        return result with { AccessToken = null };
    }

    public async Task<bool> ReconnectAsync(CancellationToken cancellation = default)
        => await _client.ReconnectAsync(cancellation).ConfigureAwait(false);

    public Task<IReadOnlyList<LanDiscoveredServer>> DiscoverAsync(
        CancellationToken cancellation = default)
        => _discovery.DiscoverAsync(cancellation);

    public async Task<DevicePairingResult> PairAutomaticallyAsync(
        LanDiscoveredServer server,
        string? deviceName = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var before = _profiles.Load();
        deviceName = string.IsNullOrWhiteSpace(deviceName)
            ? Environment.MachineName
            : deviceName.Trim();
        try
        {
            if (before.Role == NodeRole.Client
                && string.Equals(before.ServerId, server.ServerId, StringComparison.Ordinal)
                && ShellEndpointProfile.NormalizeFingerprint(before.CertificateFingerprint)
                    .Equals(
                        ShellEndpointProfile.NormalizeFingerprint(server.CertificateFingerprint),
                        StringComparison.Ordinal)
                && _secrets.Read(server.ServerId) != null)
            {
                _profiles.Save(before with { ServerEndpoint = server.Endpoint.AbsoluteUri });
                return new DevicePairingResult(
                    true,
                    server.ServerId,
                    before.DeviceId,
                    null,
                    new HashSet<string>(["read"], StringComparer.OrdinalIgnoreCase));
            }
            var ticket = await _discovery.RequestTicketAsync(
                server, before.DeviceId, deviceName, cancellation).ConfigureAwait(false);
            using var pairingClient = new ShellServiceClient(
                new ShellEndpointProfile(
                    server.Endpoint,
                    before.DeviceId,
                    CertificateFingerprint: server.CertificateFingerprint,
                    ConnectTimeout: TimeSpan.FromSeconds(5),
                    ServerId: server.ServerId),
                AppIdentity.Current.Name);
            var result = await pairingClient.PairAsync(
                ticket.Code, deviceName, cancellation).ConfigureAwait(false);
            if (!result.Success
                || !string.Equals(result.ServerId, server.ServerId, StringComparison.Ordinal)
                || !string.Equals(result.DeviceId, before.DeviceId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(result.AccessToken))
            {
                return result.Success ? Reject("服务器配对身份不匹配") : result;
            }

            var desired = before with
            {
                Role = NodeRole.Client,
                ServerEndpoint = server.Endpoint.AbsoluteUri,
                ServerId = server.ServerId,
                CertificateFingerprint = ShellEndpointProfile.NormalizeFingerprint(
                    server.CertificateFingerprint),
            };
            var previousToken = _secrets.Read(server.ServerId);
            try
            {
                _secrets.Write(server.ServerId, result.AccessToken);
                _profiles.Save(desired);
            }
            catch
            {
                if (previousToken == null)
                    _secrets.Delete(server.ServerId);
                else
                    _secrets.Write(server.ServerId, previousToken);
                throw;
            }
            if (!string.IsNullOrWhiteSpace(before.ServerId)
                && !before.ServerId.Equals(server.ServerId, StringComparison.Ordinal))
            {
                _secrets.Delete(before.ServerId);
            }
            return result with { AccessToken = null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Reject(ex.Message);
        }
    }

    public static async Task<bool> RefreshSavedEndpointAsync(
        BootstrapProfileStore profiles,
        LanDiscoveryClient? discovery = null,
        CancellationToken cancellation = default)
    {
        var profile = profiles.Load();
        if (profile.Role != NodeRole.Client
            || string.IsNullOrWhiteSpace(profile.ServerId)
            || string.IsNullOrWhiteSpace(profile.CertificateFingerprint))
        {
            return false;
        }
        try
        {
            var servers = await (discovery ?? new LanDiscoveryClient())
                .DiscoverAsync(cancellation).ConfigureAwait(false);
            var expectedFingerprint = ShellEndpointProfile.NormalizeFingerprint(
                profile.CertificateFingerprint);
            var match = servers.SingleOrDefault(server =>
                server.ServerId.Equals(profile.ServerId, StringComparison.Ordinal)
                && ShellEndpointProfile.NormalizeFingerprint(server.CertificateFingerprint)
                    .Equals(expectedFingerprint, StringComparison.Ordinal));
            if (match == null)
                return false;
            if (!string.Equals(
                    profile.ServerEndpoint,
                    match.Endpoint.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase))
            {
                profiles.Save(profile with { ServerEndpoint = match.Endpoint.AbsoluteUri });
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public void Forget()
    {
        var profile = _profiles.Load();
        if (!string.IsNullOrWhiteSpace(profile.ServerId))
            _secrets.Delete(profile.ServerId);
        _profiles.Save(profile with
        {
            ServerId = null,
            CertificateFingerprint = null,
        });
    }

    private static DevicePairingResult Reject(string failure)
        => new(false, null, null, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), failure);

    private static BootstrapProfile Prepare(
        BootstrapProfile before,
        NodeRole role,
        string? url,
        string? fingerprint)
    {
        if (role == NodeRole.Server)
            return before with { Role = role };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("客户端模式必须配置有效的服务器地址");
        BootstrapProfileStore.ValidateEndpoint(endpoint);
        var normalized = ShellEndpointProfile.NormalizeFingerprint(fingerprint);
        if (!endpoint.IsLoopback && normalized.Length != 64)
            throw new ArgumentException("非回环服务器必须填写 64 位 SHA-256 证书指纹");

        var endpointChanged = !string.Equals(
            before.ServerEndpoint, endpoint.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        var pinChanged = !string.Equals(
            ShellEndpointProfile.NormalizeFingerprint(before.CertificateFingerprint),
            normalized,
            StringComparison.Ordinal);
        return before with
        {
            Role = role,
            ServerEndpoint = endpoint.AbsoluteUri,
            CertificateFingerprint = normalized.Length == 0 ? null : normalized,
            ServerId = endpointChanged || pinChanged ? null : before.ServerId,
        };
    }

    private void Apply(BootstrapProfile before, BootstrapProfile desired)
    {
        _profiles.Save(desired);
        if (!string.IsNullOrWhiteSpace(before.ServerId)
            && !string.Equals(before.ServerId, desired.ServerId, StringComparison.Ordinal))
        {
            _secrets.Delete(before.ServerId);
        }
    }
}
