using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AppShell.Services.Web;

public enum ShellConnectionState
{
    Disconnected,
    Connecting,
    PairingRequired,
    Ready,
    Rejected,
    VersionMismatch,
}

/// <summary>Shell HTTP/WS 共享的端点、设备凭据与证书固定配置。</summary>
public sealed record ShellEndpointProfile(
    Uri BaseUri,
    string DeviceId,
    Func<string?>? AccessTokenProvider = null,
    string? CertificateFingerprint = null,
    TimeSpan? ConnectTimeout = null,
    string? ServerId = null)
{
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(5);

    public static string NormalizeFingerprint(string? value)
        => string.Concat((value ?? "").Where(Uri.IsHexDigit)).ToUpperInvariant();

    public bool ValidateCertificate(X509Certificate2? certificate)
    {
        var expected = NormalizeFingerprint(CertificateFingerprint);
        if (expected.Length == 0)
            return BaseUri.IsLoopback;
        if (certificate == null)
            return false;
        var actual = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}
