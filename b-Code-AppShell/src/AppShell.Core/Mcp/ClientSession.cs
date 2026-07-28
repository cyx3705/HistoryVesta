namespace AppShell.Core.Mcp;

/// <summary>接入命令总线的客户端种类。</summary>
public enum ClientKind
{
    Mcp,
    Shell,
    Web,
}

/// <summary>一次客户端会话的稳定身份。</summary>
public sealed record ClientSession(
    string Id,
    ClientKind Kind,
    string Name,
    string ProtocolVersion,
    DateTimeOffset ConnectedAt)
{
    public string? RemoteAddress { get; init; }

    public string? DeviceId { get; init; }

    public string? AuthSubject { get; init; }

    public IReadOnlySet<string> Scopes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsLoopback { get; init; }

    public static ClientSession Create(
        ClientKind kind,
        string name,
        string protocolVersion = "2025-03-26",
        string? id = null,
        string? remoteAddress = null,
        string? deviceId = null,
        string? authSubject = null,
        IEnumerable<string>? scopes = null,
        bool isLoopback = false)
        => new(
            id ?? Guid.NewGuid().ToString("N"),
            kind,
            string.IsNullOrWhiteSpace(name) ? "client" : name.Trim(),
            protocolVersion,
            DateTimeOffset.UtcNow)
        {
            RemoteAddress = remoteAddress,
            DeviceId = deviceId,
            AuthSubject = authSubject,
            Scopes = new HashSet<string>(scopes ?? [], StringComparer.OrdinalIgnoreCase),
            IsLoopback = isLoopback,
        };
}
