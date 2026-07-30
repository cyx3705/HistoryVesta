using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
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

    private static void VerifyAutomaticPairingTickets()
    {
        var root = Temporary("automatic-tickets");
        try
        {
            var store = new LanDeviceStore(root);
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

            var correct = store.CreateAutomaticPairCode(
                "device-auto", "192.168.1.20", nonce, TimeSpan.FromSeconds(30));
            var paired = store.Pair(
                correct.Code, "device-auto", "Automatic Client", "192.168.1.20");
            SmokeKit.True(paired.Success && paired.Scopes.Contains("read"),
                "automatic ticket accepts bound client and defaults read");
            SmokeKit.True(!store.Pair(
                    correct.Code, "device-auto", "Replay", "192.168.1.20").Success,
                "automatic ticket rejects replay");

            var wrongDevice = store.CreateAutomaticPairCode(
                "device-expected", "192.168.1.21", nonce, TimeSpan.FromSeconds(30));
            SmokeKit.True(!store.Pair(
                    wrongDevice.Code, "device-other", "Wrong", "192.168.1.21").Success,
                "automatic ticket rejects wrong deviceId");

            var wrongAddress = store.CreateAutomaticPairCode(
                "device-address", "192.168.1.22", nonce, TimeSpan.FromSeconds(30));
            SmokeKit.True(!store.Pair(
                    wrongAddress.Code, "device-address", "Wrong", "192.168.1.23").Success,
                "automatic ticket rejects wrong source address");

            var expired = store.CreateAutomaticPairCode(
                "device-expired", "192.168.1.24", nonce, TimeSpan.FromMilliseconds(10));
            Thread.Sleep(30);
            SmokeKit.True(!store.Pair(
                    expired.Code, "device-expired", "Expired", "192.168.1.24").Success,
                "automatic ticket rejects expiry");
            SmokeKit.Throws<ArgumentException>(() => store.CreateAutomaticPairCode(
                    "device-invalid", "192.168.1.25", "wrong-nonce"),
                "automatic ticket rejects invalid nonce");
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }
    }

    private static async Task VerifyDiscoveryProtocolAsync()
    {
        var port = FreeUdpPort();
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var responder = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var received = await server.ReceiveAsync();
                using var request = JsonDocument.Parse(received.Buffer);
                var requestId = request.RootElement.GetProperty("requestId").GetString()!;
                async Task SendAsync(object value) => await server.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(value), received.RemoteEndPoint);

                await server.SendAsync(Encoding.UTF8.GetBytes("{invalid-json"), received.RemoteEndPoint);
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId = new string('0', 48),
                    product = "OneHistoryStudio",
                    serverId = "stale",
                    serverName = "Stale",
                    machineName = "STALE",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = new string('A', 64),
                });
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId = "server-a",
                    serverName = "Office A",
                    machineName = "SERVER-A",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = new string('A', 64),
                });
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId = "server-b",
                    serverName = "Office B",
                    machineName = "SERVER-B",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = new string('B', 64),
                });
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId = "future",
                    serverName = "Future",
                    machineName = "FUTURE",
                    port = 8738,
                    productVersion = "99.0.0",
                    minimumClientVersion = "99.0.0",
                    certificateFingerprint = new string('C', 64),
                });
                await SendAsync(new
                {
                    protocol = "wrong-protocol",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId = "wrong-protocol",
                    serverName = "Invalid",
                    machineName = "INVALID",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = new string('D', 64),
                });
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OtherProduct",
                    serverId = "wrong-product",
                    serverName = "Invalid",
                    machineName = "INVALID",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = new string('D', 64),
                });
                await SendAsync(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId = "invalid-fields",
                    serverName = "Invalid",
                    machineName = "INVALID",
                    port = 80,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = "BAD",
                });
            }
        });

        var client = new LanDiscoveryClient(port, allowLoopback: true);
        var found = await client.DiscoverAsync();
        await responder;
        SmokeKit.Equal(2, found.Count,
            "discovery deduplicates responses and ignores stale or incompatible payloads");

        var ticketResponder = Task.Run(async () =>
        {
            var received = await server.ReceiveAsync();
            using var request = JsonDocument.Parse(received.Buffer);
            var root = request.RootElement;
            var requestId = root.GetProperty("requestId").GetString()!;
            var nonce = root.GetProperty("nonce").GetString()!;
            async Task SendAsync(string responseNonce) => await server.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "ticket-response",
                    requestId,
                    serverId = "server-a",
                    nonce = responseNonce,
                    code = "12345678",
                    expiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
                }), received.RemoteEndPoint);
            await SendAsync(new string('0', 48));
            await SendAsync(nonce);
        });
        var ticket = await client.RequestTicketAsync(
            found.Single(value => value.ServerId == "server-a"),
            "device-discovery",
            "Discovery Client");
        await ticketResponder;
        SmokeKit.Equal("12345678", ticket.Code,
            "ticket client ignores wrong nonce and accepts correlated response");

        async Task RespondForDiscoveryAsync(string serverId, string fingerprint)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var received = await server.ReceiveAsync();
                using var request = JsonDocument.Parse(received.Buffer);
                var requestId = request.RootElement.GetProperty("requestId").GetString()!;
                await server.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    protocol = "ohs-discovery/1",
                    kind = "discover-response",
                    requestId,
                    product = "OneHistoryStudio",
                    serverId,
                    serverName = "Office",
                    machineName = "SERVER",
                    port = 8738,
                    productVersion = AppIdentity.Current.Version,
                    minimumClientVersion = AppIdentity.Current.Version,
                    certificateFingerprint = fingerprint,
                }), received.RemoteEndPoint);
            }
        }

        var profileRoot = Temporary("dhcp-refresh");
        try
        {
            var profiles = new BootstrapProfileStore("Smoke", profileRoot);
            var profile = profiles.Load() with
            {
                Role = NodeRole.Client,
                ServerEndpoint = "https://192.168.1.99:8738/",
                ServerId = "server-a",
                CertificateFingerprint = new string('A', 64),
            };
            profiles.Save(profile);
            var refreshResponder = Task.Run(() => RespondForDiscoveryAsync(
                "server-a", new string('A', 64)));
            SmokeKit.True(await ConnectionProfileService.RefreshSavedEndpointAsync(
                    profiles, client),
                "saved client rediscovers its original server");
            await refreshResponder;
            SmokeKit.Equal("https://127.0.0.1:8738/", profiles.Load().ServerEndpoint,
                "saved client updates a DHCP-changed endpoint");

            profiles.Save(profile);
            var mismatchResponder = Task.Run(() => RespondForDiscoveryAsync(
                "server-other", new string('A', 64)));
            SmokeKit.True(!await ConnectionProfileService.RefreshSavedEndpointAsync(
                    profiles, client),
                "saved client does not switch to another discovered server");
            await mismatchResponder;
            SmokeKit.Equal(profile.ServerEndpoint, profiles.Load().ServerEndpoint,
                "mismatched discovery leaves the saved endpoint unchanged");
        }
        finally
        {
            SmokeKit.DeleteTree(profileRoot);
        }

        var empty = await new LanDiscoveryClient(FreeUdpPort(), allowLoopback: true).DiscoverAsync();
        SmokeKit.Equal(0, empty.Count, "discovery returns empty when no server responds");
    }

    private static void VerifyDiscoveryLifecycle()
    {
        var root = Temporary("discovery-lifecycle");
        try
        {
            var settings = new MemorySettings();
            var log = new MemoryLog();
            var registry = new CommandRegistry();
            var bus = new CommandBus(registry, log);
            using var web = new WebGateway(() => bus, settings, log);
            var configuration = new LanConfigurationService(
                settings, web, new FakeLanMachineManager(), AppIdentity.Current.Version);
            var devices = new LanDeviceStore(root);
            using (var disabled = new LanDiscoveryResponder(
                       configuration, devices, log, "Smoke", FreeUdpPort()))
            {
                disabled.Start();
                SmokeKit.True(!disabled.IsRunning && disabled.LastError == null,
                    "disabled LAN does not open discovery");
            }

            settings.Set(LanConfigurationService.KeyEnabled, "true");
            settings.Set(LanConfigurationService.KeyBind, "8.8.8.8");
            settings.Set(LanConfigurationService.KeyCertificateThumbprint, new string('A', 64));
            using (var invalid = new LanDiscoveryResponder(
                       configuration, devices, log, "Smoke", FreeUdpPort()))
            {
                invalid.Start();
                SmokeKit.True(!invalid.IsRunning && invalid.LastError != null,
                    "non-Private or inactive bind does not open discovery");
            }

            var privateAddress = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                                           && IsPrivateAddress(address));
            if (privateAddress != null)
            {
                var occupiedPort = FreeUdpPort();
                using var occupied = new UdpClient(AddressFamily.InterNetwork);
                occupied.Client.ExclusiveAddressUse = true;
                occupied.Client.Bind(new IPEndPoint(privateAddress, occupiedPort));
                settings.Set(LanConfigurationService.KeyBind, privateAddress.ToString());
                using var conflict = new LanDiscoveryResponder(
                    configuration, devices, log, "Smoke", occupiedPort);
                conflict.Start();
                SmokeKit.True(!conflict.IsRunning && conflict.LastError != null,
                    "UDP port conflict is reported without starting discovery");
                SmokeKit.True(web.Start(FreePort()).Success,
                    "discovery port conflict does not block loopback Web service");
                web.Stop();
            }
        }
        finally
        {
            SmokeKit.DeleteTree(root);
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168;
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
