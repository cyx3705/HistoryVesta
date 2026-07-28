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

internal static class LanSingleExeSuite
{
    public static async Task RunAsync(string[] args)
    {
        VerifyBootstrapAndSecrets();
        VerifyPairingAndRevocation();
        VerifyCertificatePin();
        VerifyMachinePayloadSecurity();
        await VerifyLanConfigurationAsync();
        await VerifyCommandRedactionAsync();
        await VerifyScopeAndPairingHttpAsync();
        await VerifyRemoteWorkspaceAsync();
        await VerifySessionAffinityAsync();
        VerifySingleEntrySource();
        Console.WriteLine("LanSingleExeSmoke: PASS");
    }

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

    private static async Task VerifyLanConfigurationAsync()
    {
        var settings = new MemorySettings();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new MemoryLog());
        using var web = new WebGateway(() => bus, settings, new MemoryLog());
        var machine = new FakeLanMachineManager();
        var configuration = new LanConfigurationService(
            settings, web, machine, AppIdentity.Current.Version);
        var preview = await configuration.ConfigureAsync(
            true, "192.168.10.20", 8738, apply: false);
        SmokeKit.True(preview.Success && machine.Calls.Count == 0,
            "LAN preview does not invoke machine manager");
        var applied = await configuration.ConfigureAsync(
            true, "192.168.10.20", 8738, apply: true);
        SmokeKit.True(applied.Success && machine.Calls.Count == 1,
            "LAN apply invokes machine manager once");
        SmokeKit.True(configuration.Status() is
            {
                Enabled: true,
                BindAddress: "192.168.10.20",
                Port: 8738,
                CertificateThumbprint.Length: 64,
            }, "LAN applied state persisted");
        SmokeKit.True(!JsonSerializer.Serialize(configuration.Status())
                .Contains("certificateStoreThumbprint", StringComparison.OrdinalIgnoreCase),
            "LAN public status omits certificate store locator");
        var rotated = await configuration.RotateCertificateAsync(apply: true);
        SmokeKit.True(rotated.Success && machine.Calls[^1].Action == "rotate",
            "LAN certificate rotation uses controlled manager");

        var devicesRoot = Temporary("commands");
        try
        {
            LanCommands.RegisterAll(registry, new LanDeviceStore(devicesRoot), web, configuration);
            SmokeKit.True(registry.TryGet("lan.configure", out _)
                          && registry.TryGet("lan.cert.rotate", out _),
                "LAN machine commands registered");
            var remote = await bus.ExecuteAsync(
                "lan.configure enabled=true bind=192.168.10.20 port=8738",
                "LanShell:remote");
            SmokeKit.True(!remote.Success && machine.Calls.Count == 2,
                "remote LAN machine configuration rejected before manager");
            var remoteDevices = await bus.ExecuteAsync("lan.device.list", "LanShell:remote");
            SmokeKit.True(!remoteDevices.Success,
                "remote read device cannot enumerate paired devices");
        }
        finally
        {
            SmokeKit.DeleteTree(devicesRoot);
        }
    }

    private static async Task VerifyScopeAndPairingHttpAsync()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "smoke.read",
            Summary = "read",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("read-ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "smoke.write",
            Summary = "write",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("write-unexpected")),
        });
        var settings = new MemorySettings();
        using var gateway = new WebGateway(() => new CommandBus(registry, new MemoryLog()), settings, new MemoryLog())
        {
            DeviceAuthentication = new ReadDeviceAuthentication(),
        };
        var pairingRoot = Temporary("pair-http");
        var devices = new LanDeviceStore(pairingRoot);
        gateway.DevicePairing = devices;
        var port = FreePort();
        try
        {
            SmokeKit.True(gateway.Start(port).Success, "scope gateway start");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using (var wrongServer = new ShellServiceClient(new ShellEndpointProfile(
                       http.BaseAddress, "wrong-server-device", ServerId: "unexpected-server"), "Mismatch"))
            {
                SmokeKit.True(!await wrongServer.WaitForReadyAsync(TimeSpan.FromSeconds(1))
                              && wrongServer.State == ShellConnectionState.Rejected,
                    "serverId mismatch blocks before business commands");
            }
            var read = await DeviceCommandAsync(http, "smoke.read");
            SmokeKit.Equal(HttpStatusCode.OK, read.StatusCode, "read scope HTTP status");
            SmokeKit.Contains(await read.Content.ReadAsStringAsync(), "read-ok", "read scope command");
            var write = await DeviceCommandAsync(http, "smoke.write");
            SmokeKit.Equal(HttpStatusCode.Forbidden, write.StatusCode, "read scope blocks write");

            using (var deviceSocket = new ClientWebSocket())
            {
                deviceSocket.Options.SetRequestHeader("Authorization", "Bearer read-token");
                deviceSocket.Options.SetRequestHeader("X-AppShell-Client", "Web");
                deviceSocket.Options.SetRequestHeader("X-Client-Name", "Revocable");
                deviceSocket.Options.SetRequestHeader("X-Session-Id", "revocable-session");
                deviceSocket.Options.SetRequestHeader("X-Device-Id", "read-device");
                await deviceSocket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None);
                _ = await ReceiveAsync(deviceSocket);
                SmokeKit.Equal(1, gateway.DisconnectDevice("read-device"),
                    "device revoke disconnects existing websocket");
                SmokeKit.Equal(0, gateway.ConnectedClients,
                    "revoked websocket removed from session catalog");
            }

            var code = devices.CreatePairCode();
            var pair = await http.PostAsJsonAsync("api/pair", new
            {
                code = code.Code,
                deviceId = "http-device",
                deviceName = "HTTP Client",
            });
            SmokeKit.Equal(HttpStatusCode.OK, pair.StatusCode, "loopback pairing endpoint");
            var body = await pair.Content.ReadAsStringAsync();
            SmokeKit.True(!string.IsNullOrWhiteSpace(
                JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString()),
                "pairing returns token once");
            var replay = await http.PostAsJsonAsync("api/pair", new
            {
                code = code.Code,
                deviceId = "http-replay",
                deviceName = "Replay",
            });
            SmokeKit.Equal(HttpStatusCode.Unauthorized, replay.StatusCode, "pairing endpoint rejects replay");
        }
        finally
        {
            gateway.Stop();
            SmokeKit.DeleteTree(pairingRoot);
        }
    }

    private static async Task VerifySessionAffinityAsync()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new MemoryLog());
        var settings = new MemorySettings();
        var log = new MemoryLog();
        using var gateway = new WebGateway(() => bus, settings, log);
        var port = FreePort();
        try
        {
            SmokeKit.True(gateway.Start(port).Success, "affinity gateway start");
            using var alpha = await ConnectShellAsync(port, "alpha");
            using var beta = await ConnectShellAsync(port, "beta");
            _ = await ReceiveAsync(alpha);
            _ = await ReceiveAsync(beta);

            var relay = gateway.RelayFrontendCommandAsync(
                "win.list", "Shell:alpha:Alpha", CancellationToken.None);
            var command = await ReceiveAsync(alpha);
            SmokeKit.Equal("uiCommand", command.GetProperty("type").GetString(), "alpha receives UI command");
            var id = command.GetProperty("id").GetString();

            await SendAsync(beta, new { type = "commandResult", id, success = true, message = "spoofed" });
            SmokeKit.True(await Task.WhenAny(relay, Task.Delay(150)) != relay,
                "other session cannot complete correlation");
            await SendAsync(alpha, new { type = "commandResult", id, success = true, message = "alpha-result" });
            SmokeKit.Equal("alpha-result", (await relay).Message, "origin session completes correlation");

            var noOrigin = await gateway.RelayFrontendCommandAsync(
                "win.list", "Web:missing", CancellationToken.None);
            SmokeKit.True(!noOrigin.Success, "web without origin shell cannot relay UI command");
        }
        finally
        {
            gateway.Stop();
        }
    }

    private static Task VerifyRemoteWorkspaceAsync()
    {
        var registry = new CommandRegistry();
        var serverRoot = Path.Combine(Path.GetTempPath(), "ohs-server-workspace");
        registry.Register(new CommandDescriptor
        {
            Name = "res.list",
            Summary = "remote workspace list",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "path", Description = "relative path", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("one entry",
                new WorkspaceListing(serverRoot,
                [new WorkspaceEntry("server.txt", "server.txt", false, 12, DateTime.Today)]))),
        });
        var settings = new MemorySettings();
        using var gateway = new WebGateway(
            () => new CommandBus(registry, new MemoryLog()), settings, new MemoryLog());
        var port = FreePort();
        try
        {
            SmokeKit.True(gateway.Start(port).Success, "remote workspace gateway start");
            using var client = new ShellServiceClient(
                new Uri($"http://127.0.0.1:{port}/"), "RemoteWorkspace");
            var workspace = new RemoteWorkspaceService(client);
            var entries = workspace.List();
            SmokeKit.Equal(1, entries.Count, "remote workspace returns server entries");
            SmokeKit.Equal("server.txt", entries[0].Name, "remote workspace entry identity");
            SmokeKit.Equal(serverRoot, workspace.Root, "remote workspace uses server root");
            SmokeKit.True(!workspace.CanSelectLocalRoot,
                "remote workspace blocks client folder picker");
            SmokeKit.Throws<InvalidOperationException>(() => workspace.ResolveFull("..\\escape.txt"),
                "remote workspace rejects path escape");
        }
        finally
        {
            gateway.Stop();
        }

        return Task.CompletedTask;
    }

    private static void VerifySingleEntrySource()
    {
        var studio = File.ReadAllText(Path.Combine(SmokeKit.RepoRoot, "Studio.csproj"));
        var program = File.ReadAllText(Path.Combine(SmokeKit.RepoRoot, "Program.cs"));
        var solution = File.ReadAllText(Path.Combine(SmokeKit.ParentDir, "OHS.sln"));
        var app = File.ReadAllText(Path.Combine(SmokeKit.RepoRoot, "App.xaml.cs"));
        var githubView = File.ReadAllText(Path.Combine(
            SmokeKit.RepoRoot, "Views", "GitHubAccountView.xaml"));
        var legacy = File.ReadAllText(Path.Combine(
            SmokeKit.ParentDir, "b-Code-Studio.Service", "Studio.Service.csproj"));
        SmokeKit.Contains(studio, "<StartupObject>OneHistoryStudio.Program</StartupObject>",
            "Studio explicit entrypoint");
        SmokeKit.Contains(program, "--service-host", "single EXE service mode");
        SmokeKit.Contains(program, "--generate-manual", "single EXE manual mode");
        SmokeKit.Contains(program, "--lan-install", "single EXE LAN install helper mode");
        SmokeKit.Contains(program, "--lan-remove", "single EXE LAN remove helper mode");
        SmokeKit.True(!solution.Contains("b-Code-Studio.Service", StringComparison.OrdinalIgnoreCase),
            "legacy Service project removed from product solution");
        SmokeKit.Contains(legacy, "<OutputType>Library</OutputType>",
            "legacy Service source is non-product library");
        SmokeKit.Contains(app, "workspace = new RemoteWorkspaceService(_serviceClient)",
            "client composition uses remote workspace");
        SmokeKit.Contains(githubView, "服务器 GitHub 账号",
            "GitHub view identifies server account ownership");
        var helper = File.ReadAllText(Path.Combine(
            SmokeKit.RepoRoot, "Connection", "LanMachineHelper.cs"));
        SmokeKit.Contains(helper, "LAN 机器配置原本未启用，无需移除",
            "LAN remove is idempotent when no owned configuration exists");
        SmokeKit.Contains(helper, "如已被其他应用占用则失败",
            "new LAN endpoint does not replace external binding");
    }

    private static async Task VerifyCommandRedactionAsync()
    {
        const string secret = "48151623";
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "secret.test",
            Summary = "secret echo smoke",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "code",
                    Description = "secret code",
                    Required = true,
                },
            ],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("accepted")),
        });
        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);
        string? executedText = null;
        bus.Executed += (text, _, _) => executedText = text;
        SmokeKit.True((await bus.ExecuteAsync($"secret.test code={secret}", "UI")).Success,
            "sensitive command executes");
        SmokeKit.True(executedText != null
                      && !executedText.Contains(secret, StringComparison.Ordinal)
                      && executedText.Contains("[REDACTED]", StringComparison.Ordinal),
            "status event redacts code");
        SmokeKit.True(log.Snapshot().All(entry =>
                !entry.Message.Contains(secret, StringComparison.Ordinal)),
            "command logs redact code");
    }

    private static async Task<HttpResponseMessage> DeviceCommandAsync(HttpClient http, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/command")
        {
            Content = JsonContent.Create(new { text }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "read-token");
        request.Headers.Add("X-AppShell-Client", "Web");
        request.Headers.Add("X-Client-Name", "ReadClient");
        request.Headers.Add("X-Session-Id", "read-session");
        request.Headers.Add("X-Device-Id", "read-device");
        return await http.SendAsync(request);
    }

    private static async Task<ClientWebSocket> ConnectShellAsync(int port, string session)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-AppShell-Client", "Shell");
        socket.Options.SetRequestHeader("X-Client-Name", session);
        socket.Options.SetRequestHeader("X-Session-Id", session);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None);
        return socket;
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private static Task SendAsync(ClientWebSocket socket, object value)
        => socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(value),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Temporary(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), "ohs-lan-smoke-" + suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ReadDeviceAuthentication : IDeviceAuthenticationProvider
    {
        public DeviceAuthenticationResult Authenticate(string deviceId, string token, string? remoteAddress)
            => deviceId == "read-device" && token == "read-token"
                ? DeviceAuthenticationResult.Accept(deviceId, "device:read", ["read"])
                : DeviceAuthenticationResult.Reject();
    }

    private sealed class FakeLanMachineManager : ILanMachineManager
    {
        public List<LanMachineOperation> Calls { get; } = [];

        public Task<LanMachineResult> ApplyAsync(
            LanMachineOperation operation,
            CancellationToken cancellation = default)
        {
            Calls.Add(operation);
            return Task.FromResult(new LanMachineResult(
                true,
                "fake machine apply",
                new string(operation.Action == "rotate" ? 'B' : 'A', 64),
                DateTimeOffset.UtcNow.AddYears(1),
                "private-rule-installed",
                new string(operation.Action == "rotate" ? 'D' : 'C', 40)));
        }
    }
}
