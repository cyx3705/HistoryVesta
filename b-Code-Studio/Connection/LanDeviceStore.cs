using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Net;
using AppShell.Services.Web;

namespace OneHistoryStudio.Connection;

public sealed record LanDeviceInfo(
    string DeviceId,
    string Name,
    string Scope,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record LanPairCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>服务器设备配对与 token hash 的持久化入口；明文 token/配对码不落盘。</summary>
public sealed class LanDeviceStore : IDeviceAuthenticationProvider, IDevicePairingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, PairTicket> _pairCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FailureWindow> _pairFailures = new(StringComparer.OrdinalIgnoreCase);
    private LanDeviceState _state;

    public LanDeviceStore(string root)
    {
        _path = Path.Combine(root, "lan-devices.json");
        _state = Load();
    }

    public string ServerId
    {
        get { lock (_gate) return _state.ServerId; }
    }

    public LanPairCode CreatePairCode(TimeSpan? lifetime = null)
        => CreatePairCodeCore(null, null, null, lifetime ?? TimeSpan.FromMinutes(2));

    public LanPairCode CreateAutomaticPairCode(
        string deviceId,
        string remoteAddress,
        string nonce,
        TimeSpan? lifetime = null)
    {
        if (!ValidDeviceId(deviceId))
            throw new ArgumentException("deviceId 无效", nameof(deviceId));
        if (!IPAddress.TryParse(remoteAddress, out var address)
            || (!LanConfigurationService.IsPrivate(address) && !IPAddress.IsLoopback(address)))
            throw new ArgumentException("自动配对来源必须是 Private IPv4", nameof(remoteAddress));
        if (!LanDiscoveryProtocol.ValidId(nonce))
            throw new ArgumentException("自动配对 nonce 无效", nameof(nonce));
        return CreatePairCodeCore(
            deviceId,
            address.ToString(),
            nonce,
            lifetime ?? LanDiscoveryProtocol.TicketLifetime);
    }

    private LanPairCode CreatePairCodeCore(
        string? deviceId,
        string? remoteAddress,
        string? nonce,
        TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            RemoveExpiredCodes(now);
            string code;
            do
            {
                code = RandomNumberGenerator.GetInt32(0, 100_000_000)
                    .ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            } while (_pairCodes.ContainsKey(code));
            var expires = now + lifetime;
            _pairCodes[code] = new PairTicket(expires, deviceId, remoteAddress, nonce);
            return new LanPairCode(code, expires);
        }
    }

    public IReadOnlyList<LanDeviceInfo> List()
    {
        lock (_gate)
            return _state.Devices.Select(ToInfo).OrderBy(device => device.Name).ToList();
    }

    public bool SetScope(string deviceId, string scope)
    {
        scope = NormalizeScope(scope);
        lock (_gate)
        {
            var index = _state.Devices.FindIndex(device =>
                device.DeviceId.Equals(deviceId, StringComparison.Ordinal));
            if (index < 0)
                return false;
            _state.Devices[index] = _state.Devices[index] with { Scope = scope };
            Persist();
            return true;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            var index = _state.Devices.FindIndex(device =>
                device.DeviceId.Equals(deviceId, StringComparison.Ordinal));
            if (index < 0)
                return false;
            _state.Devices[index] = _state.Devices[index] with { RevokedAt = DateTimeOffset.UtcNow };
            Persist();
            return true;
        }
    }

    public DeviceAuthenticationResult Authenticate(
        string deviceId,
        string token,
        string? remoteAddress)
    {
        lock (_gate)
        {
            var index = _state.Devices.FindIndex(device =>
                device.DeviceId.Equals(deviceId, StringComparison.Ordinal));
            if (index < 0 || _state.Devices[index].RevokedAt != null)
                return DeviceAuthenticationResult.Reject();
            var device = _state.Devices[index];
            byte[] candidate;
            try
            {
                candidate = Rfc2898DeriveBytes.Pbkdf2(
                    token,
                    Convert.FromBase64String(device.Salt),
                    120_000,
                    HashAlgorithmName.SHA256,
                    32);
            }
            catch (FormatException)
            {
                return DeviceAuthenticationResult.Reject();
            }
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        candidate, Convert.FromBase64String(device.TokenHash)))
                    return DeviceAuthenticationResult.Reject();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidate);
            }

            var now = DateTimeOffset.UtcNow;
            if (device.LastUsedAt == null || now - device.LastUsedAt >= TimeSpan.FromMinutes(1))
            {
                device = device with { LastUsedAt = now };
                _state.Devices[index] = device;
                Persist();
            }
            return DeviceAuthenticationResult.Accept(
                device.DeviceId,
                $"device:{device.DeviceId}",
                [device.Scope]);
        }
    }

    public DevicePairingResult Pair(
        string code,
        string deviceId,
        string deviceName,
        string? remoteAddress)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var failureKey = remoteAddress ?? "unknown";
            if (!AllowPairAttempt(failureKey, now))
                return RejectPairing();
            RemoveExpiredCodes(now);
            if (!_pairCodes.Remove(code, out var ticket) || ticket.ExpiresAt <= now
                || !ValidDeviceId(deviceId) || string.IsNullOrWhiteSpace(deviceName))
            {
                RecordPairFailure(failureKey, now);
                return RejectPairing();
            }
            if (ticket.DeviceId != null
                && (!ticket.DeviceId.Equals(deviceId, StringComparison.Ordinal)
                    || !ticket.RemoteAddress!.Equals(remoteAddress, StringComparison.OrdinalIgnoreCase)))
            {
                RecordPairFailure(failureKey, now);
                return RejectPairing();
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes)
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(tokenBytes);
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(
                token, salt, 120_000, HashAlgorithmName.SHA256, 32);
            try
            {
                var record = new LanDeviceRecord(
                    deviceId,
                    deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 80)],
                    Convert.ToBase64String(salt),
                    Convert.ToBase64String(hash),
                    "read",
                    now,
                    null,
                    null);
                var index = _state.Devices.FindIndex(device =>
                    device.DeviceId.Equals(deviceId, StringComparison.Ordinal));
                if (index >= 0)
                    _state.Devices[index] = record;
                else
                    _state.Devices.Add(record);
                Persist();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(salt);
                CryptographicOperations.ZeroMemory(hash);
            }

            _pairFailures.Remove(failureKey);
            return new DevicePairingResult(
                true,
                _state.ServerId,
                deviceId,
                token,
                new HashSet<string>(["read"], StringComparer.OrdinalIgnoreCase));
        }
    }

    private LanDeviceState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<LanDeviceState>(
                    File.ReadAllText(_path), JsonOptions);
                if (state != null && ValidDeviceId(state.ServerId))
                    return state;
            }
        }
        catch (JsonException)
        {
        }
        var created = new LanDeviceState(Guid.NewGuid().ToString("N"), []);
        Persist(created);
        return created;
    }

    private void Persist() => Persist(_state);

    private void Persist(LanDeviceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions),
            new System.Text.UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    private void RemoveExpiredCodes(DateTimeOffset now)
    {
        foreach (var code in _pairCodes.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
            _pairCodes.Remove(code);
    }

    private bool AllowPairAttempt(string key, DateTimeOffset now)
        => !_pairFailures.TryGetValue(key, out var window)
           || now - window.Start >= TimeSpan.FromMinutes(1)
           || window.Count < 5;

    private void RecordPairFailure(string key, DateTimeOffset now)
    {
        _pairFailures[key] = !_pairFailures.TryGetValue(key, out var current)
                             || now - current.Start >= TimeSpan.FromMinutes(1)
            ? new FailureWindow(now, 1)
            : current with { Count = current.Count + 1 };
    }

    private DevicePairingResult RejectPairing()
        => new(false, _state.ServerId, null, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), "pairing rejected");

    private static string NormalizeScope(string scope)
        => scope.ToLowerInvariant() is "read" or "operate" or "admin"
            ? scope.ToLowerInvariant()
            : throw new ArgumentException("scope 只允许 read|operate|admin");

    internal static bool ValidDeviceId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
           && value.All(character => char.IsLetterOrDigit(character)
                                     || character is '-' or '_' or '.');

    private static LanDeviceInfo ToInfo(LanDeviceRecord device)
        => new(device.DeviceId, device.Name, device.Scope, device.CreatedAt,
            device.LastUsedAt, device.RevokedAt);

    private sealed record LanDeviceState(string ServerId, List<LanDeviceRecord> Devices);

    private sealed record LanDeviceRecord(
        string DeviceId,
        string Name,
        string Salt,
        string TokenHash,
        string Scope,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastUsedAt,
        DateTimeOffset? RevokedAt);

    private sealed record FailureWindow(DateTimeOffset Start, int Count);

    private sealed record PairTicket(
        DateTimeOffset ExpiresAt,
        string? DeviceId,
        string? RemoteAddress,
        string? Nonce);
}
