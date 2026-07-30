using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Files;
using AppShell.Services.Web;
using OneHistoryStudio.Connection;

namespace OneHistoryStudio.Smoke.Suites;

internal static partial class LanSingleExeSuite
{
    private static void VerifyBootstrapAndSecrets()
    {
        var root = Temporary("bootstrap");
        try
        {
            var store = new BootstrapProfileStore("Smoke", root);
            var profile = store.Load();
            SmokeKit.Equal(NodeRole.Server, profile.Role, "legacy bootstrap defaults server");
            SmokeKit.True(File.Exists(store.Path), "bootstrap default persisted");
            SmokeKit.True(!File.ReadAllText(store.Path).Contains("token", StringComparison.OrdinalIgnoreCase),
                "bootstrap has no token field");

            var client = profile with
            {
                Role = NodeRole.Client,
                ServerEndpoint = "https://192.168.1.20:8738/",
                ServerId = "server-a",
                CertificateFingerprint = new string('A', 64),
            };
            store.Save(client);
            SmokeKit.Equal(client, store.Load(), "client bootstrap roundtrip");
            SmokeKit.Throws<ArgumentException>(() => BootstrapProfileStore.ValidateEndpoint(
                new Uri("http://192.168.1.20:8738/")), "remote HTTP rejected");
            SmokeKit.Throws<ArgumentException>(() => BootstrapProfileStore.ValidateEndpoint(
                new Uri("https://user:pass@192.168.1.20:8738/")), "endpoint userinfo rejected");

            var secrets = new DpapiSecretStore(root);
            const string token = "device-token-never-plaintext";
            secrets.Write("server-a", token);
            SmokeKit.Equal(token, secrets.Read("server-a"), "DPAPI secret roundtrip");
            var secretFile = Directory.EnumerateFiles(Path.Combine(root, "secrets")).Single();
            SmokeKit.True(!Encoding.UTF8.GetString(File.ReadAllBytes(secretFile))
                .Contains(token, StringComparison.Ordinal), "DPAPI file excludes plaintext token");
            secrets.Delete("server-a");
            SmokeKit.True(secrets.Read("server-a") == null, "DPAPI secret deletion");

            store.Save(client with { ServerId = "server-a" });
            secrets.Write("server-a", token);
            using var profileClient = new ShellServiceClient(
                new Uri("http://127.0.0.1:65534/"), "ProfileSmoke");
            var profiles = new ConnectionProfileService(store, secrets, profileClient);
            var changedEndpoint = profiles.Prepare(
                NodeRole.Client,
                "https://192.168.1.21:8738/",
                new string('A', 64));
            SmokeKit.True(changedEndpoint.ServerId == null,
                "endpoint change clears paired server identity in preview");
            profiles.Apply(changedEndpoint);
            SmokeKit.True(secrets.Read("server-a") == null,
                "endpoint change deletes stale device token");
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }

        var appName = "OHS-Client-Paths-" + Guid.NewGuid().ToString("N");
        var paths = new AppShell.Services.AppPaths(appName, createBusinessDirectories: false);
        try
        {
            SmokeKit.True(!Directory.Exists(paths.DataDir), "client paths omit data");
            SmokeKit.True(!Directory.Exists(paths.WorkspaceDir), "client paths omit workspace");
            SmokeKit.True(!Directory.Exists(paths.ModulesDir), "client paths omit modules");
        }
        finally
        {
            SmokeKit.DeleteTree(paths.Root);
        }
    }

    private static void VerifyPairingAndRevocation()
    {
        var root = Temporary("devices");
        try
        {
            var store = new LanDeviceStore(root);
            var pairCode = store.CreatePairCode();
            var paired = store.Pair(pairCode.Code, "device-a", "Office Client", "192.168.1.20");
            SmokeKit.True(paired.Success && !string.IsNullOrWhiteSpace(paired.AccessToken),
                "one-time pairing succeeds");
            var persisted = File.ReadAllText(Path.Combine(root, "lan-devices.json"));
            SmokeKit.True(!persisted.Contains(pairCode.Code, StringComparison.Ordinal), "pair code not persisted");
            SmokeKit.True(!persisted.Contains(paired.AccessToken!, StringComparison.Ordinal), "token not persisted");
            SmokeKit.Contains(persisted, "tokenHash", "token hash persisted");
            SmokeKit.True(!store.Pair(pairCode.Code, "device-b", "Replay", "192.168.1.20").Success,
                "pair code replay rejected");

            var auth = store.Authenticate("device-a", paired.AccessToken!, "192.168.1.20");
            SmokeKit.True(auth.Success && auth.Scopes.Contains("read"), "new device defaults read");
            SmokeKit.True(store.SetScope("device-a", "operate"), "device scope update");
            SmokeKit.True(store.Authenticate("device-a", paired.AccessToken!, null).Scopes.Contains("operate"),
                "updated scope takes effect");
            SmokeKit.True(store.Revoke("device-a"), "device revoke");
            SmokeKit.True(!store.Authenticate("device-a", paired.AccessToken!, null).Success,
                "revoked token rejected immediately");

            for (var i = 0; i < 5; i++)
                SmokeKit.True(!store.Pair("00000000", "bad", "Bad", "10.0.0.8").Success,
                    "bad pairing rejected");
            var limitedCode = store.CreatePairCode();
            SmokeKit.True(!store.Pair(limitedCode.Code, "device-c", "Limited", "10.0.0.8").Success,
                "pairing failures are rate limited");
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }
    }

    private static void VerifyCertificatePin()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=OHS Smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));
        var profile = new ShellEndpointProfile(
            new Uri("https://192.168.1.20:8738/"), "device", CertificateFingerprint: fingerprint);
        SmokeKit.True(profile.ValidateCertificate(certificate), "matching SHA-256 pin accepted");
        SmokeKit.True(!profile.ValidateCertificate(null), "missing remote certificate rejected");
        SmokeKit.True(!new ShellEndpointProfile(
                new Uri("https://192.168.1.20:8738/"), "device", CertificateFingerprint: new string('0', 64))
            .ValidateCertificate(certificate), "wrong SHA-256 pin rejected");
        SmokeKit.True(!new ShellEndpointProfile(new Uri("https://192.168.1.20:8738/"), "device")
            .ValidateCertificate(certificate), "remote endpoint requires explicit pin");
    }

    private static void VerifyMachinePayloadSecurity()
    {
        var root = Temporary("helper-payload");
        try
        {
            var productVersion = AppIdentity.Current.Version;
            var payloads = new LanMachinePayloadStore(root, productVersion);
            var now = DateTimeOffset.UtcNow;
            var operation = new LanMachineOperation(
                "install",
                "192.168.10.20",
                8738,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                now,
                now.AddMinutes(3),
                productVersion,
                null,
                null,
                null,
                null);
            var encoded = payloads.Encode(operation);
            SmokeKit.Equal(operation, payloads.Decode(encoded, "install"),
                "signed LAN helper payload roundtrip");
            payloads.ConsumeNonce(operation);
            SmokeKit.Throws<InvalidDataException>(() => payloads.ConsumeNonce(operation),
                "LAN helper nonce replay rejected");

            var tampered = encoded[..^1] + (encoded[^1] == 'A' ? "B" : "A");
            SmokeKit.Throws<Exception>(() => payloads.Decode(tampered, "install"),
                "LAN helper signature tampering rejected");
            SmokeKit.Throws<ArgumentException>(() => payloads.Encode(operation with
                {
                    BindAddress = "8.8.8.8",
                    Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                }),
                "public IPv4 rejected");
            SmokeKit.Throws<ArgumentException>(() => payloads.Encode(operation with
                {
                    BindAddress = "0.0.0.0",
                    Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
                }),
                "wildcard IPv4 rejected");
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }
    }
}
