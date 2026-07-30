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
    public static async Task RunAsync(string[] args)
    {
        VerifyBootstrapAndSecrets();
        VerifyPairingAndRevocation();
        VerifyAutomaticPairingTickets();
        VerifyCertificatePin();
        VerifyMachinePayloadSecurity();
        VerifyDiscoveryLifecycle();
        await VerifyDiscoveryProtocolAsync();
        await VerifyLanConfigurationAsync();
        await VerifyCommandRedactionAsync();
        await VerifyScopeAndPairingHttpAsync();
        await VerifyRemoteWorkspaceAsync();
        await VerifySessionAffinityAsync();
        VerifySingleEntrySource();
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
            var devices = new LanDeviceStore(devicesRoot);
            using var discovery = new LanDiscoveryResponder(
                configuration, devices, new MemoryLog(), "Smoke");
            LanCommands.RegisterAll(registry, devices, web, configuration, discovery);
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
        var connectionView = File.ReadAllText(Path.Combine(
            SmokeKit.RepoRoot, "Views", "ConnectionSettingsView.xaml"));
        SmokeKit.Contains(studio, "<StartupObject>OneHistoryStudio.Program</StartupObject>",
            "Studio explicit entrypoint");
        SmokeKit.Contains(program, "--service-host", "single EXE service mode");
        SmokeKit.Contains(program, "--generate-manual", "single EXE manual mode");
        SmokeKit.Contains(program, "--lan-install", "single EXE LAN install helper mode");
        SmokeKit.Contains(program, "--lan-remove", "single EXE LAN remove helper mode");
        SmokeKit.True(!solution.Contains("b-Code-Studio.Service", StringComparison.OrdinalIgnoreCase),
            "legacy Service project removed from product solution");
        SmokeKit.True(!Directory.Exists(Path.Combine(
                SmokeKit.ParentDir, "b-Code-Studio.Service")),
            "legacy Service project directory removed from source tree");
        SmokeKit.Contains(app, "workspace = new RemoteWorkspaceService(_serviceClient)",
            "client composition uses remote workspace");
        SmokeKit.Contains(app, "RefreshSavedEndpointAsync(bootstrapStore)",
            "saved client retries discovery by original server identity");
        SmokeKit.Contains(app, "当前客户端为只读，等待服务器授权",
            "read-only client receives a user-facing authorization message");
        SmokeKit.Contains(githubView, "服务器 GitHub 账号",
            "GitHub view identifies server account ownership");
        foreach (var (name, xaml) in new[]
                 {
                     ("connection", connectionView),
                     ("GitHub", githubView),
                 })
        {
            var userControl = xaml[..xaml.IndexOf('>')];
            SmokeKit.True(!userControl.Contains("MinWidth=", StringComparison.Ordinal)
                          && !userControl.Contains("MinHeight=", StringComparison.Ordinal),
                $"{name} docked view does not force the host minimum size");
            SmokeKit.Contains(xaml, "<WrapPanel",
                $"{name} docked view wraps narrow-width actions");
        }
        var helper = File.ReadAllText(Path.Combine(
            SmokeKit.RepoRoot, "Connection", "LanMachineHelper.cs"));
        SmokeKit.Contains(helper, "LAN 机器配置原本未启用，无需移除",
            "LAN remove is idempotent when no owned configuration exists");
        SmokeKit.Contains(helper, "如已被其他应用占用则失败",
            "new LAN endpoint does not replace external binding");
        SmokeKit.Contains(helper, "protocol=UDP",
            "LAN machine helper installs a UDP discovery firewall rule");
        SmokeKit.Contains(helper, "LanDiscoveryProtocol.Port",
            "LAN machine helper uses the fixed discovery port");
        SmokeKit.Contains(connectionView, "局域网服务器",
            "connection view exposes discovery as the default client flow");
        SmokeKit.Contains(connectionView, "Header=\"高级手工连接\"",
            "manual address, fingerprint, and pair code are advanced controls");
        SmokeKit.Contains(connectionView, "IsExpanded=\"False\"",
            "manual connection controls are collapsed by default");
        var composition = File.ReadAllText(Path.Combine(
            SmokeKit.RepoRoot, "Service", "StudioServiceCompositionFactory.cs"));
        SmokeKit.Contains(composition, "DeferredWork = [lanDiscovery]",
            "discovery responder follows ServiceHost deferred startup");
        SmokeKit.Contains(composition, "DisposeApplicationServices = lanDiscovery.Dispose",
            "discovery responder follows ServiceHost disposal");
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

    private static int FreeUdpPort()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
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
