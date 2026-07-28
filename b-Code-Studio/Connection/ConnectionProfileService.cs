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

    public ConnectionProfileService(
        BootstrapProfileStore profiles,
        DpapiSecretStore secrets,
        ShellServiceClient client)
    {
        _profiles = profiles;
        _secrets = secrets;
        _client = client;
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
