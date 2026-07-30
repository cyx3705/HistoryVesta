using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using AppShell.Core;
using AppShell.Core.Logging;
using AppShell.Core.Modules;

namespace OneHistoryStudio.Connection;

public sealed record LanDiscoveredServer(
    string ServerId,
    string ServerName,
    string MachineName,
    IPAddress Address,
    int Port,
    string ProductVersion,
    string MinimumClientVersion,
    string CertificateFingerprint)
{
    public Uri Endpoint => new($"https://{Address}:{Port}/");

    public string DisplayName => $"{ServerName} · {MachineName} · {Address}:{Port}";
}

public sealed record LanPairingTicket(string Code, string Nonce, DateTimeOffset ExpiresAt);

internal sealed record LanDiscoveryEnvelope(
    string Protocol,
    string Kind,
    string RequestId,
    string? Product = null,
    string? ClientVersion = null,
    string? ServerId = null,
    string? ServerName = null,
    string? MachineName = null,
    int? Port = null,
    string? ProductVersion = null,
    string? MinimumClientVersion = null,
    string? CertificateFingerprint = null,
    string? DeviceId = null,
    string? DeviceName = null,
    string? Nonce = null,
    string? Code = null,
    DateTimeOffset? ExpiresAt = null,
    string? Error = null);

internal static class LanDiscoveryProtocol
{
    public const string Name = "ohs-discovery/1";
    public const int Port = 8739;
    public const int MaximumDatagramBytes = 4 * 1024;
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] Encode(LanDiscoveryEnvelope value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaximumDatagramBytes)
            throw new InvalidDataException("LAN discovery datagram is too large");
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out LanDiscoveryEnvelope value)
    {
        value = null!;
        if (bytes.Length is 0 or > MaximumDatagramBytes)
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<LanDiscoveryEnvelope>(bytes, JsonOptions);
            if (parsed == null || !parsed.Protocol.Equals(Name, StringComparison.Ordinal))
                return false;
            value = parsed;
            return ValidId(parsed.RequestId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public static bool ValidId(string? value)
        => value is { Length: 48 } && value.All(Uri.IsHexDigit);
}

public sealed class LanDiscoveryClient
{
    private readonly int _port;
    private readonly bool _allowLoopback;

    public LanDiscoveryClient(int port = LanDiscoveryProtocol.Port, bool allowLoopback = false)
    {
        _port = port;
        _allowLoopback = allowLoopback;
    }

    public async Task<IReadOnlyList<LanDiscoveredServer>> DiscoverAsync(
        CancellationToken cancellation = default)
    {
        var requestId = LanDiscoveryProtocol.NewId();
        var request = LanDiscoveryProtocol.Encode(new LanDiscoveryEnvelope(
            LanDiscoveryProtocol.Name,
            "discover",
            requestId,
            Product: "OneHistoryStudio",
            ClientVersion: AppIdentity.Current.Version));
        using var udp = CreateClient();
        var targets = BroadcastTargets(_port).ToList();
        if (_allowLoopback)
            targets.Add(new IPEndPoint(IPAddress.Loopback, _port));
        foreach (var target in targets)
            await udp.SendAsync(request, target, cancellation).ConfigureAwait(false);
        await Task.Delay(150, cancellation).ConfigureAwait(false);
        foreach (var target in targets)
            await udp.SendAsync(request, target, cancellation).ConfigureAwait(false);
        await Task.Delay(150, cancellation).ConfigureAwait(false);
        foreach (var target in targets)
            await udp.SendAsync(request, target, cancellation).ConfigureAwait(false);

        var found = new Dictionary<string, LanDiscoveredServer>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(LanDiscoveryProtocol.DiscoveryTimeout);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            if (!AllowedAddress(received.RemoteEndPoint.Address)
                || !LanDiscoveryProtocol.TryDecode(received.Buffer, out var response)
                || response.Kind != "discover-response"
                || response.RequestId != requestId
                || response.Product != "OneHistoryStudio"
                || string.IsNullOrWhiteSpace(response.ServerId)
                || string.IsNullOrWhiteSpace(response.ServerName)
                || string.IsNullOrWhiteSpace(response.MachineName)
                || response.Port is not (>= 1024 and <= 65535)
                || string.IsNullOrWhiteSpace(response.ProductVersion)
                || string.IsNullOrWhiteSpace(response.MinimumClientVersion)
                || !CompatibleVersion(response.MinimumClientVersion)
                || !ValidFingerprint(response.CertificateFingerprint))
            {
                continue;
            }
            found[response.ServerId] = new LanDiscoveredServer(
                response.ServerId,
                response.ServerName,
                response.MachineName,
                received.RemoteEndPoint.Address,
                response.Port.Value,
                response.ProductVersion,
                response.MinimumClientVersion,
                response.CertificateFingerprint!);
        }
        return found.Values.OrderBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<LanPairingTicket> RequestTicketAsync(
        LanDiscoveredServer server,
        string deviceId,
        string deviceName,
        CancellationToken cancellation = default)
    {
        var requestId = LanDiscoveryProtocol.NewId();
        var nonce = LanDiscoveryProtocol.NewId();
        var request = LanDiscoveryProtocol.Encode(new LanDiscoveryEnvelope(
            LanDiscoveryProtocol.Name,
            "ticket",
            requestId,
            ServerId: server.ServerId,
            DeviceId: deviceId,
            DeviceName: deviceName,
            Nonce: nonce));
        using var udp = CreateClient();
        await udp.SendAsync(
            request,
            new IPEndPoint(server.Address, _port),
            cancellation).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(LanDiscoveryProtocol.DiscoveryTimeout);
        while (true)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("服务器未返回自动配对票据");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            if (!received.RemoteEndPoint.Address.Equals(server.Address)
                || !LanDiscoveryProtocol.TryDecode(received.Buffer, out var response)
                || response.Kind != "ticket-response"
                || response.RequestId != requestId
                || response.ServerId != server.ServerId
                || response.Nonce != nonce)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new InvalidOperationException(response.Error);
            if (string.IsNullOrWhiteSpace(response.Code)
                || response.ExpiresAt is not { } expiresAt
                || expiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("服务器返回的自动配对票据无效");
            return new LanPairingTicket(response.Code, nonce, expiresAt);
        }
    }

    private UdpClient CreateClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        return client;
    }

    private bool AllowedAddress(IPAddress address)
        => LanConfigurationService.IsPrivate(address)
           || _allowLoopback && IPAddress.IsLoopback(address);

    private static bool ValidFingerprint(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool CompatibleVersion(string minimumClientVersion)
        => Version.TryParse(minimumClientVersion, out var minimum)
           && Version.TryParse(AppIdentity.Current.Version, out var current)
           && current >= minimum;

    private static IEnumerable<IPEndPoint> BroadcastTargets(int port)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var item in adapter.GetIPProperties().UnicastAddresses)
            {
                if (item.Address.AddressFamily != AddressFamily.InterNetwork
                    || !LanConfigurationService.IsPrivate(item.Address)
                    || item.IPv4Mask == null)
                    continue;
                var address = item.Address.GetAddressBytes();
                var mask = item.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var index = 0; index < 4; index++)
                    broadcast[index] = (byte)(address[index] | ~mask[index]);
                values.Add(new IPAddress(broadcast).ToString());
            }
        }
        values.Add(IPAddress.Broadcast.ToString());
        return values.Select(value => new IPEndPoint(IPAddress.Parse(value), port));
    }
}

public sealed class LanDiscoveryResponder : IDeferredStartupWork, IDisposable
{
    private readonly LanConfigurationService _configuration;
    private readonly LanDeviceStore _devices;
    private readonly IShellLog _log;
    private readonly string _serverName;
    private readonly int _port;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rateWindows = new(StringComparer.Ordinal);
    private readonly object _rateGate = new();
    private CancellationTokenSource? _lifetime;
    private UdpClient? _udp;
    private Task? _receiveLoop;

    public LanDiscoveryResponder(
        LanConfigurationService configuration,
        LanDeviceStore devices,
        IShellLog log,
        string serverName,
        int port = LanDiscoveryProtocol.Port)
    {
        _configuration = configuration;
        _devices = devices;
        _log = log;
        _serverName = serverName;
        _port = port is >= 1024 and <= 65535
            ? port
            : throw new ArgumentOutOfRangeException(nameof(port));
    }

    public bool IsRunning => _udp != null && _receiveLoop is { IsCompleted: false };

    public string? LastError { get; private set; }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public void Start()
    {
        if (_udp != null)
            return;
        var status = _configuration.Status();
        if (!status.Enabled || status.CertificateThumbprint is not { Length: 64 })
            return;
        if (!IPAddress.TryParse(status.BindAddress, out var bind)
            || bind.AddressFamily != AddressFamily.InterNetwork
            || !LanConfigurationService.IsPrivate(bind)
            || !IsActiveLocalAddress(bind))
        {
            LastError = "LAN 自动发现绑定地址不是当前活动的 Private IPv4";
            return;
        }
        try
        {
            _udp = new UdpClient(new IPEndPoint(bind, _port));
            _lifetime = new CancellationTokenSource();
            _receiveLoop = ReceiveLoopAsync(_udp, _lifetime.Token);
            LastError = null;
            _log.Info("lan", $"LAN 自动发现已启动: udp://{bind}:{_port}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _udp?.Dispose();
            _udp = null;
            _log.Warn("lan", $"LAN 自动发现启动失败: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                LastError = ex.Message;
                _log.Warn("lan", $"LAN 自动发现接收失败: {ex.Message}");
                return;
            }

            if (!AllowedRemote(received.RemoteEndPoint.Address)
                || !LanDiscoveryProtocol.TryDecode(received.Buffer, out var request)
                || !AllowRequest(received.RemoteEndPoint.Address, request.Kind == "ticket" ? 5 : 20))
                continue;

            LanDiscoveryEnvelope? response = request.Kind switch
            {
                "discover" => DiscoveryResponse(request),
                "ticket" => TicketResponse(request, received.RemoteEndPoint.Address),
                _ => null,
            };
            if (response == null)
                continue;
            try
            {
                var bytes = LanDiscoveryProtocol.Encode(response);
                await udp.SendAsync(bytes, received.RemoteEndPoint, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                LastError = ex.Message;
            }
        }
    }

    private LanDiscoveryEnvelope? DiscoveryResponse(LanDiscoveryEnvelope request)
    {
        if (request.Product != "OneHistoryStudio")
            return null;
        var status = _configuration.Status();
        if (!status.Enabled || status.CertificateThumbprint is not { Length: 64 })
            return null;
        return new LanDiscoveryEnvelope(
            LanDiscoveryProtocol.Name,
            "discover-response",
            request.RequestId,
            Product: "OneHistoryStudio",
            ServerId: _devices.ServerId,
            ServerName: _serverName,
            MachineName: Environment.MachineName,
            Port: status.Port,
            ProductVersion: AppIdentity.Current.Version,
            MinimumClientVersion: AppIdentity.Current.Version,
            CertificateFingerprint: status.CertificateThumbprint);
    }

    private LanDiscoveryEnvelope TicketResponse(LanDiscoveryEnvelope request, IPAddress remoteAddress)
    {
        if (request.ServerId != _devices.ServerId
            || !LanDiscoveryProtocol.ValidId(request.Nonce)
            || !LanDeviceStore.ValidDeviceId(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.DeviceName))
        {
            return TicketError(request, "自动配对请求无效");
        }
        var ticket = _devices.CreateAutomaticPairCode(
            request.DeviceId!,
            remoteAddress.ToString(),
            request.Nonce!,
            LanDiscoveryProtocol.TicketLifetime);
        return new LanDiscoveryEnvelope(
            LanDiscoveryProtocol.Name,
            "ticket-response",
            request.RequestId,
            ServerId: _devices.ServerId,
            Nonce: request.Nonce,
            Code: ticket.Code,
            ExpiresAt: ticket.ExpiresAt);
    }

    private LanDiscoveryEnvelope TicketError(LanDiscoveryEnvelope request, string error)
        => new(
            LanDiscoveryProtocol.Name,
            "ticket-response",
            request.RequestId,
            ServerId: _devices.ServerId,
            Nonce: request.Nonce,
            Error: error);

    private static bool AllowedRemote(IPAddress address)
        => LanConfigurationService.IsPrivate(address) || IPAddress.IsLoopback(address);

    private static bool IsActiveLocalAddress(IPAddress address)
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Any(item => item.Address.Equals(address));

    private bool AllowRequest(IPAddress address, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_rateGate)
        {
            var key = address.ToString();
            if (!_rateWindows.TryGetValue(key, out var values))
                _rateWindows[key] = values = new Queue<DateTimeOffset>();
            while (values.TryPeek(out var first) && now - first >= TimeSpan.FromMinutes(1))
                values.Dequeue();
            if (values.Count >= limit)
                return false;
            values.Enqueue(now);
            return true;
        }
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _lifetime?.Dispose();
        _lifetime = null;
        try { _receiveLoop?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        _receiveLoop = null;
    }
}
