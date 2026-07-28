using System.Text.Json;
using System.IO;

namespace OneHistoryStudio.Connection;

public enum NodeRole
{
    Server,
    Client,
}

public sealed record BootstrapProfile
{
    public int SchemaVersion { get; init; } = 1;
    public NodeRole Role { get; init; } = NodeRole.Server;
    public string? ServerEndpoint { get; init; }
    public string? ServerId { get; init; }
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string SelectedProfile { get; init; } = "default";
    public string? CertificateFingerprint { get; init; }
}

/// <summary>当前设备在连接服务前读取的本机角色配置；不存放任何 secret。</summary>
public sealed class BootstrapProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public BootstrapProfileStore(string applicationName, string? root = null)
    {
        var directory = root ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), applicationName);
        _path = System.IO.Path.Combine(directory, "bootstrap.json");
    }

    public string Path => _path;

    public BootstrapProfile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var profile = JsonSerializer.Deserialize<BootstrapProfile>(
                    File.ReadAllText(_path), JsonOptions);
                if (profile != null)
                {
                    Validate(profile);
                    return profile;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (ArgumentException)
        {
        }

        var fallback = new BootstrapProfile();
        Save(fallback);
        return fallback;
    }

    public void Save(BootstrapProfile profile)
    {
        Validate(profile);
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, JsonOptions),
            new System.Text.UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    public static Uri ResolveEndpoint(BootstrapProfile profile, int loopbackPort)
    {
        if (profile.Role == NodeRole.Server)
            return new Uri($"http://127.0.0.1:{loopbackPort}/");
        if (!Uri.TryCreate(profile.ServerEndpoint, UriKind.Absolute, out var endpoint))
            throw new ArgumentException("客户端模式必须配置服务器地址");
        ValidateEndpoint(endpoint);
        return endpoint;
    }

    public static void ValidateEndpoint(Uri endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("服务器地址不得包含 userinfo、query 或 fragment");
        if (!endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !(endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 && endpoint.IsLoopback))
            throw new ArgumentException("非回环服务器地址必须使用 HTTPS");
    }

    private static void Validate(BootstrapProfile profile)
    {
        if (profile.SchemaVersion != 1)
            throw new ArgumentException($"不支持的 bootstrap schema: {profile.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(profile.DeviceId) || profile.DeviceId.Length > 128)
            throw new ArgumentException("deviceId 无效");
        if (profile.Role == NodeRole.Client)
        {
            if (!Uri.TryCreate(profile.ServerEndpoint, UriKind.Absolute, out var endpoint))
                throw new ArgumentException("客户端模式必须配置服务器地址");
            ValidateEndpoint(endpoint);
        }
    }
}
