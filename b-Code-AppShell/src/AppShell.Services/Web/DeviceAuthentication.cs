namespace AppShell.Services.Web;

public sealed record DeviceAuthenticationResult(
    bool Success,
    string? DeviceId,
    string? Subject,
    IReadOnlySet<string> Scopes,
    string? Failure = null)
{
    public static DeviceAuthenticationResult Reject(string failure = "unauthorized")
        => new(false, null, null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), failure);

    public static DeviceAuthenticationResult Accept(
        string deviceId,
        string subject,
        IEnumerable<string> scopes)
        => new(true, deviceId, subject,
            new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase));
}

public interface IDeviceAuthenticationProvider
{
    DeviceAuthenticationResult Authenticate(string deviceId, string token, string? remoteAddress);
}

public sealed record DevicePairingResult(
    bool Success,
    string? ServerId,
    string? DeviceId,
    string? AccessToken,
    IReadOnlySet<string> Scopes,
    string? Failure = null);

public interface IDevicePairingProvider
{
    DevicePairingResult Pair(
        string code,
        string deviceId,
        string deviceName,
        string? remoteAddress);
}
