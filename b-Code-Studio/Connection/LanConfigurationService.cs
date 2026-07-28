using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppShell.Core.Storage;
using AppShell.Services.Web;

namespace OneHistoryStudio.Connection;

public sealed record LanConfigurationStatus(
    bool Enabled,
    string BindAddress,
    int Port,
    string? CertificateThumbprint,
    [property: JsonIgnore] string? CertificateStoreThumbprint,
    DateTimeOffset? CertificateExpiresAt,
    string FirewallStatus,
    bool RequiresRestart);

public sealed record LanConfigurationRequest(bool Enabled, string BindAddress, int Port);

public sealed record LanMachineOperation(
    string Action,
    string BindAddress,
    int Port,
    string Nonce,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string ProductVersion,
    string? PreviousBindAddress,
    int? PreviousPort,
    string? PreviousCertificateThumbprint,
    string? PreviousCertificateStoreThumbprint);

public sealed record LanMachineResult(
    bool Success,
    string Message,
    string? CertificateThumbprint = null,
    DateTimeOffset? CertificateExpiresAt = null,
    string FirewallStatus = "unknown",
    [property: JsonIgnore] string? CertificateStoreThumbprint = null);

public interface ILanMachineManager
{
    Task<LanMachineResult> ApplyAsync(
        LanMachineOperation operation,
        CancellationToken cancellation = default);
}

/// <summary>服务端 LAN 设置的唯一业务入口；机器级变更委托给受控提权进程。</summary>
public sealed class LanConfigurationService
{
    public const string KeyEnabled = "lan.enabled";
    public const string KeyBind = "lan.bind";
    public const string KeyPort = "lan.port";
    public const string KeyCertificateThumbprint = "lan.certthumbprint";
    public const string KeyCertificateStoreThumbprint = "lan.certstorethumbprint";
    public const string KeyCertificateExpires = "lan.certexpires";
    public const string KeyFirewallStatus = "lan.firewall";

    private readonly ISettingsService _settings;
    private readonly WebGateway _web;
    private readonly ILanMachineManager _machine;
    private readonly string _productVersion;

    public LanConfigurationService(
        ISettingsService settings,
        WebGateway web,
        ILanMachineManager machine,
        string productVersion)
    {
        _settings = settings;
        _web = web;
        _machine = machine;
        _productVersion = productVersion;
    }

    public LanConfigurationStatus Status()
    {
        var port = ReadPort();
        DateTimeOffset? expires = DateTimeOffset.TryParse(
            _settings.Get(KeyCertificateExpires),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
        return new LanConfigurationStatus(
            ReadEnabled(),
            ReadBind(),
            port,
            EmptyToNull(_settings.Get(KeyCertificateThumbprint)),
            EmptyToNull(_settings.Get(KeyCertificateStoreThumbprint)),
            expires,
            _settings.Get(KeyFirewallStatus) ?? "not-configured",
            _web.IsRunning
            && (_web.Port != port
                || !_web.ActiveBindAddress.Equals(ReadBind(), StringComparison.OrdinalIgnoreCase)));
    }

    public LanConfigurationRequest Preview(bool enabled, string bindAddress, int port)
    {
        var normalized = NormalizeBind(enabled, bindAddress);
        ValidatePort(port);
        return new LanConfigurationRequest(enabled, normalized, port);
    }

    public async Task<LanMachineResult> ConfigureAsync(
        bool enabled,
        string bindAddress,
        int port,
        bool apply,
        CancellationToken cancellation = default)
    {
        var request = Preview(enabled, bindAddress, port);
        if (!apply)
            return new LanMachineResult(
                true,
                enabled
                    ? $"预览：在 {request.BindAddress}:{request.Port} 启用 HTTPS LAN；需要 UAC"
                    : "预览：关闭 LAN 并移除本版本的 HTTPS、URLACL 和 Private 防火墙规则");

        var before = Status();
        var operation = CreateOperation(
            enabled ? "install" : "remove",
            request.BindAddress,
            request.Port,
            before);
        var result = await _machine.ApplyAsync(operation, cancellation).ConfigureAwait(false);
        if (!result.Success)
            return result;

        Persist(request, result);
        return result with { Message = result.Message + "；重启后台服务后生效" };
    }

    public async Task<LanMachineResult> RotateCertificateAsync(
        bool apply,
        CancellationToken cancellation = default)
    {
        var current = Status();
        if (!current.Enabled)
            return new LanMachineResult(false, "LAN 尚未启用，不能轮换证书");
        if (!apply)
            return new LanMachineResult(
                true,
                $"预览：轮换 {current.BindAddress}:{current.Port} 的证书；所有客户端必须重新核对指纹");

        var operation = CreateOperation(
            "rotate", current.BindAddress, current.Port, current);
        var result = await _machine.ApplyAsync(operation, cancellation).ConfigureAwait(false);
        if (!result.Success)
            return result;
        Persist(
            new LanConfigurationRequest(true, current.BindAddress, current.Port),
            result);
        return result with { Message = result.Message + "；客户端需更新证书指纹，重启后台服务后生效" };
    }

    private LanMachineOperation CreateOperation(
        string action,
        string bind,
        int port,
        LanConfigurationStatus before)
    {
        var now = DateTimeOffset.UtcNow;
        return new LanMachineOperation(
            action,
            bind,
            port,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            now,
            now.AddMinutes(3),
            _productVersion,
            before.Enabled ? before.BindAddress : null,
            before.Enabled ? before.Port : null,
            before.CertificateThumbprint,
            before.CertificateStoreThumbprint);
    }

    private void Persist(LanConfigurationRequest request, LanMachineResult result)
    {
        _settings.Set(KeyEnabled, request.Enabled ? "true" : "false");
        _settings.Set(KeyBind, request.BindAddress);
        _settings.Set(KeyPort, request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _settings.Set(WebGateway.KeyBind, request.Enabled ? request.BindAddress : "127.0.0.1");
        _settings.Set(WebGateway.KeyPort, request.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _settings.Set(KeyCertificateThumbprint, result.CertificateThumbprint ?? "");
        _settings.Set(KeyCertificateStoreThumbprint, result.CertificateStoreThumbprint ?? "");
        _settings.Set(KeyCertificateExpires,
            result.CertificateExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "");
        _settings.Set(KeyFirewallStatus, result.FirewallStatus);
    }

    private bool ReadEnabled()
        => bool.TryParse(_settings.Get(KeyEnabled), out var enabled) && enabled;

    private string ReadBind()
        => _settings.Get(KeyBind) ?? _settings.Get(WebGateway.KeyBind) ?? "127.0.0.1";

    private int ReadPort()
    {
        var configured = _settings.GetInt(KeyPort, _settings.GetInt(WebGateway.KeyPort, 8738));
        return configured is >= 1024 and <= 65535 ? configured : 8738;
    }

    internal static string NormalizeBind(bool enabled, string value)
    {
        if (!enabled)
            return "127.0.0.1";
        if (!IPAddress.TryParse(value?.Trim(), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any)
            || IPAddress.IsLoopback(address)
            || !IsPrivate(address))
        {
            throw new ArgumentException("LAN 监听地址必须是本机 Private IPv4，不能使用 0.0.0.0 或回环地址");
        }
        return address.ToString();
    }

    internal static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "LAN 端口必须在 1024~65535");
    }

    internal static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168;
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>以当前用户 DPAPI 保护 HMAC 密钥；payload 短时效且 nonce 只能消费一次。</summary>
public sealed class LanMachinePayloadStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OneHistoryStudio.LanHelper.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly string _version;

    public LanMachinePayloadStore(string root, string productVersion)
    {
        _root = root;
        _version = productVersion;
    }

    public string Encode(LanMachineOperation operation)
    {
        ValidateOperation(operation, operation.Action);
        var payload = JsonSerializer.SerializeToUtf8Bytes(operation, JsonOptions);
        var key = LoadOrCreateKey();
        try
        {
            var signature = HMACSHA256.HashData(key, payload);
            return Base64Url(JsonSerializer.SerializeToUtf8Bytes(
                new SignedEnvelope(Base64Url(payload), Base64Url(signature)), JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public LanMachineOperation Decode(string encoded, string expectedAction)
    {
        byte[] envelopeBytes;
        try { envelopeBytes = FromBase64Url(encoded); }
        catch (FormatException ex) { throw new InvalidDataException("LAN helper payload 编码无效", ex); }
        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(envelopeBytes, JsonOptions)
                       ?? throw new InvalidDataException("LAN helper payload 为空");
        var payload = FromBase64Url(envelope.Payload);
        var supplied = FromBase64Url(envelope.Signature);
        var key = LoadOrCreateKey();
        try
        {
            var expected = HMACSHA256.HashData(key, payload);
            if (supplied.Length != expected.Length
                || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                throw new InvalidDataException("LAN helper payload 签名无效");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var operation = JsonSerializer.Deserialize<LanMachineOperation>(payload, JsonOptions)
                        ?? throw new InvalidDataException("LAN helper operation 为空");
        ValidateOperation(operation, expectedAction);
        return operation;
    }

    public void ConsumeNonce(LanMachineOperation operation)
    {
        var directory = Path.Combine(_root, "lan-helper", "used");
        Directory.CreateDirectory(directory);
        try
        {
            using var marker = new FileStream(
                Path.Combine(directory, operation.Nonce + ".nonce"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            marker.WriteByte(1);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("LAN helper payload 已使用", ex);
        }
    }

    public string ResultPath(string nonce)
    {
        ValidateNonce(nonce);
        return Path.Combine(_root, "lan-helper", "results", nonce + ".json");
    }

    public void WriteResult(string nonce, LanMachineResult result)
    {
        var path = ResultPath(nonce);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public LanMachineResult? ReadAndDeleteResult(string nonce)
    {
        var path = ResultPath(nonce);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LanMachineResult>(File.ReadAllText(path), JsonOptions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private byte[] LoadOrCreateKey()
    {
        var path = Path.Combine(_root, "secrets", "lan-helper-key.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedKey = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(protectedKey);
            }
            catch (IOException) when (File.Exists(path))
            {
                return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            }
            return key.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void ValidateOperation(LanMachineOperation operation, string expectedAction)
    {
        var actionMatches = operation.Action.Equals(expectedAction, StringComparison.Ordinal)
                            || expectedAction == "install" && operation.Action == "rotate";
        if (operation.Action is not ("install" or "remove" or "rotate") || !actionMatches)
            throw new InvalidDataException("LAN helper action 不匹配");
        if (!operation.ProductVersion.Equals(_version, StringComparison.Ordinal))
            throw new InvalidDataException("LAN helper 版本不匹配");
        ValidateNonce(operation.Nonce);
        var now = DateTimeOffset.UtcNow;
        if (operation.IssuedAt > now.AddMinutes(1)
            || operation.ExpiresAt <= now
            || operation.ExpiresAt - operation.IssuedAt > TimeSpan.FromMinutes(5))
            throw new InvalidDataException("LAN helper payload 已过期或时间范围无效");
        LanConfigurationService.ValidatePort(operation.Port);
        if (operation.Action is "install" or "rotate")
            _ = LanConfigurationService.NormalizeBind(true, operation.BindAddress);
        if (operation.PreviousPort is { } previousPort)
            LanConfigurationService.ValidatePort(previousPort);
        if (operation.PreviousBindAddress is { } previousBind)
            _ = LanConfigurationService.NormalizeBind(true, previousBind);
        if (operation.PreviousCertificateThumbprint is { } thumbprint
            && (!thumbprint.All(Uri.IsHexDigit) || thumbprint.Length != 64))
            throw new InvalidDataException("旧证书指纹无效");
        if (operation.PreviousCertificateStoreThumbprint is { } storeThumbprint
            && (!storeThumbprint.All(Uri.IsHexDigit) || storeThumbprint.Length != 40))
            throw new InvalidDataException("旧证书库 thumbprint 无效");
    }

    private static void ValidateNonce(string nonce)
    {
        if (nonce.Length != 48 || !nonce.All(Uri.IsHexDigit))
            throw new InvalidDataException("LAN helper nonce 无效");
    }

    private static string Base64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record SignedEnvelope(string Payload, string Signature);
}

/// <summary>生产实现只负责启动同一 EXE 的受控 UAC 模式并读取结构化结果。</summary>
public sealed class ElevatedLanMachineManager : ILanMachineManager
{
    private readonly string _executablePath;
    private readonly LanMachinePayloadStore _payloads;

    public ElevatedLanMachineManager(
        string executablePath,
        LanMachinePayloadStore payloads)
    {
        _executablePath = executablePath;
        _payloads = payloads;
    }

    public async Task<LanMachineResult> ApplyAsync(
        LanMachineOperation operation,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var encoded = _payloads.Encode(operation);
        var mode = operation.Action == "remove" ? "--lan-remove" : "--lan-install";
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(encoded);
        try
        {
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("无法启动 LAN 配置辅助进程");
            // 提权事务开始后必须等待结构化结果，不能让调用方取消造成机器状态与设置分叉。
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var result = _payloads.ReadAndDeleteResult(operation.Nonce);
            return result
                   ?? new LanMachineResult(false, $"LAN 配置辅助进程未返回结果（退出码 {process.ExitCode}）");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LanMachineResult(false, "用户取消了 UAC 授权");
        }
        catch (OperationCanceledException)
        {
            return new LanMachineResult(false, "LAN 机器配置已取消");
        }
        catch (Exception ex)
        {
            return new LanMachineResult(false, $"启动 LAN 配置辅助进程失败: {ex.Message}");
        }
    }
}
