using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Services.Mcp;
using AppShell.Services.Web;
using AppShell.ServiceHost;
using AppShell.Shell;
using AppShell.Shell.Mcp;
using OneHistoryStudio.Connection;
using OneHistoryStudio.Service;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Smoke.Suites;

internal static partial class ServiceWebSuite
{
    public static async Task RunAsync(string[] args)
    {
        await VerifyCommandRoutingAsync();
        await VerifyServiceCompositionAsync();
        VerifyConfiguredServicePortPreserved();
        await VerifyMcpSessionsAsync();
        await VerifyWebGatewayAsync();
    }

    private static async Task VerifyCommandRoutingAsync()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "route.ping",
            Summary = "routing smoke",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("local")),
        });
        var bus = new CommandBus(registry, new MemoryLog())
        {
            RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("remote")),
            ShouldUseRemote = source => !source.StartsWith("Service:", StringComparison.Ordinal),
        };
        SmokeKit.Equal("remote", (await bus.ExecuteAsync("route.ping", "UI")).Message, "remote route");
        SmokeKit.Equal("local", (await bus.ExecuteAsync("route.ping", "Service:Relay")).Message, "relay bypass");

        var frontendRegistry = new CommandRegistry();
        frontendRegistry.Register(new CommandDescriptor
        {
            Name = "win.test",
            Summary = "frontend smoke",
            ExecutionSite = CommandExecutionSite.Frontend,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("unexpected")),
        });
        var frontendBus = new CommandBus(frontendRegistry, new MemoryLog());
        SmokeKit.True(frontendBus.Validate("win.test") == null,
            "menu-style command validation accepts registered command");
        SmokeKit.True(frontendBus.Validate("win.test unexpected=true") != null,
            "menu-style command validation rejects unknown parameter");
        SmokeKit.True(frontendBus.Validate("missing.command") != null,
            "menu-style command validation rejects unknown command");
        var disconnected = await frontendBus.ExecuteAsync("win.test", "Web:test");
        SmokeKit.True(!disconnected.Success && disconnected.Message.Contains("前端未连接"), "frontend disconnected result");
    }

    private static async Task VerifyMcpSessionsAsync()
    {
        var appName = "OHS-ServiceWeb-Smoke-" + Guid.NewGuid().ToString("N");
        var paths = new AppPaths(appName);
        var settings = new MemorySettings();
        var log = new MemoryLog();
        var audit = new AuditProbe();
        var registry = PingRegistry();
        var bus = new CommandBus(registry, log);
        var prompts = new PromptGovernanceStore(paths.Root, log);
        var gateway = new McpGateway(() => bus, settings, log, audit, prompts,
            new AppShell.Core.ApplicationIdentity(appName, "2.6.0", "2.6.0-smoke", "2.6.0.0"));
        var port = FreePort();
        try
        {
            SmokeKit.True(gateway.Start(port).Success, "mcp start");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            var alpha = await McpAsync(http, "session-a", null, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "banana", clientInfo = new { name = "Alpha", version = "1" } },
            });
            SmokeKit.Equal(HttpStatusCode.OK, alpha.StatusCode, "banana status");
            SmokeKit.Contains(await alpha.Content.ReadAsStringAsync(), "2025-06-18", "banana fallback");
            SmokeKit.Equal("session-a", alpha.Headers.GetValues("Mcp-Session-Id").Single(), "session response header");

            await McpAsync(http, "session-b", null, new
            {
                jsonrpc = "2.0", id = 2, method = "initialize",
                @params = new { protocolVersion = "2025-03-26", clientInfo = new { name = "Beta", version = "1" } },
            });
            var alphaCall = await McpAsync(http, "session-a", "2025-06-18", new
            {
                jsonrpc = "2.0", id = 3, method = "tools/call",
                @params = new { name = "smoke_ping", arguments = new { } },
            });
            var betaCall = await McpAsync(http, "session-b", "2025-03-26", new
            {
                jsonrpc = "2.0", id = 4, method = "tools/call",
                @params = new { name = "smoke_ping", arguments = new { } },
            });
            SmokeKit.Equal(HttpStatusCode.OK, alphaCall.StatusCode, "alpha call");
            SmokeKit.Equal(HttpStatusCode.OK, betaCall.StatusCode, "beta call");
            SmokeKit.True(audit.Clients.SequenceEqual(["Alpha", "Beta"]), "session audit identity");

            var invalid = await McpAsync(http, "session-a", "9999-01-01", new
            {
                jsonrpc = "2.0", id = 5, method = "tools/list", @params = new { },
            });
            SmokeKit.Equal(HttpStatusCode.BadRequest, invalid.StatusCode, "invalid protocol header");
            var missing = await McpAsync(http, null, null, new
            {
                jsonrpc = "2.0", id = 6, method = "tools/list", @params = new { },
            });
            SmokeKit.Equal(HttpStatusCode.OK, missing.StatusCode, "missing protocol downgrade");
        }
        finally
        {
            gateway.Dispose();
            DeleteAppData(paths.Root);
        }
    }

    private static async Task VerifyWebGatewayAsync()
    {
        var settings = new MemorySettings();
        settings.Set(WebGateway.KeyToken, "secret");
        settings.Set(WebGateway.KeyCors, "https://example.test");
        settings.Set(WebGateway.KeyRateLimit, "10");
        var log = new MemoryLog();
        var registry = PingRegistry();
        var bus = new CommandBus(registry, log);
        using var gateway = new WebGateway(() => bus, settings, log);
        bus.FrontendExecutor = gateway.RelayFrontendCommandAsync;
        var port = FreePort();
        try
        {
            SmokeKit.True(gateway.Start(port).Success, "web start");
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            SmokeKit.Equal(HttpStatusCode.Unauthorized,
                (await http.GetAsync("api/health")).StatusCode, "web missing token");
            using (var wrong = new HttpRequestMessage(HttpMethod.Get, "api/health"))
            {
                wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
                SmokeKit.Equal(HttpStatusCode.Unauthorized,
                    (await http.SendAsync(wrong)).StatusCode, "web wrong token");
            }
            using (var health = Authorized(HttpMethod.Get, "api/health", "health"))
                SmokeKit.Equal(HttpStatusCode.OK, (await http.SendAsync(health)).StatusCode, "web health");

            using (var command = Authorized(HttpMethod.Post, "api/command", "command"))
            {
                command.Content = new StringContent("{\"text\":\"smoke.ping\"}", Encoding.UTF8, "application/json");
                var response = await http.SendAsync(command);
                SmokeKit.Equal(HttpStatusCode.OK, response.StatusCode, "web command status");
                SmokeKit.Contains(await response.Content.ReadAsStringAsync(), "pong", "web command result");
            }

            using (var client = new ShellServiceClient(new Uri($"http://127.0.0.1:{port}/"), "TreeContract"))
            {
                client.DataDeserializer = (_, data) =>
                    JsonSerializer.Deserialize<BranchTreeNode>(data.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                var treeResult = await client.ExecuteAsync("contract.tree", "UI");
                var tree = treeResult.Data as BranchTreeNode;
                SmokeKit.True(treeResult.Success && tree != null,
                    "recursive tree HTTP contract returns typed data");
                SmokeKit.True(tree!.TryValidate(out var nodeCount, out _)
                              && nodeCount == 4
                              && tree.Children[0].Children.Count == 2,
                    "recursive tree HTTP contract preserves all levels");
            }

            using (var options = new HttpRequestMessage(HttpMethod.Options, "api/health"))
            {
                options.Headers.Add("Origin", "https://example.test");
                var response = await http.SendAsync(options);
                SmokeKit.Equal(HttpStatusCode.NoContent, response.StatusCode, "cors preflight");
                SmokeKit.Equal("https://example.test",
                    response.Headers.GetValues("Access-Control-Allow-Origin").Single(), "cors origin");
            }

            HttpStatusCode last = 0;
            for (var i = 0; i < 11; i++)
            {
                using var request = Authorized(HttpMethod.Get, "api/health", "rate");
                last = (await http.SendAsync(request)).StatusCode;
            }
            SmokeKit.Equal(HttpStatusCode.TooManyRequests, last, "web rate limit");

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-AppShell-Client", "Shell");
            socket.Options.SetRequestHeader("X-Client-Name", "SmokeShell");
            socket.Options.SetRequestHeader("X-Session-Id", "shell-smoke");
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None);
            _ = await ReceiveJsonAsync(socket);
            var relay = gateway.RelayFrontendCommandAsync(
                "win.list", "Shell:shell-smoke:SmokeShell", CancellationToken.None);
            var forwarded = await ReceiveJsonAsync(socket);
            SmokeKit.Equal("uiCommand", forwarded.GetProperty("type").GetString(), "ui relay type");
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "commandResult",
                id = forwarded.GetProperty("id").GetString(),
                success = true,
                message = "relayed",
            });
            await socket.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
            SmokeKit.Equal("relayed", (await relay).Message, "ui relay response");

            using var webSocket = new ClientWebSocket();
            webSocket.Options.SetRequestHeader("Authorization", "Bearer secret");
            webSocket.Options.SetRequestHeader("X-Client-Name", "SmokeApprover");
            webSocket.Options.SetRequestHeader("X-Session-Id", "confirm-events");
            await webSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/events"), CancellationToken.None);
            _ = await ReceiveJsonAsync(webSocket);
            var confirmation = Task.Run(() => gateway.RequestWebConfirmation(
                "confirm smoke",
                "Web:confirm-events:SmokeApprover",
                TimeSpan.FromSeconds(5)));
            var confirmationEvent = await ReceiveJsonAsync(webSocket);
            SmokeKit.Equal("confirmation", confirmationEvent.GetProperty("type").GetString(), "confirmation event");
            using (var answer = Authorized(HttpMethod.Post, "api/confirm", "confirm-events"))
            {
                answer.Content = JsonContent.Create(new
                {
                    id = confirmationEvent.GetProperty("id").GetString(),
                    approved = true,
                });
                SmokeKit.Equal(HttpStatusCode.OK, (await http.SendAsync(answer)).StatusCode, "confirmation answer");
            }
            SmokeKit.True(await confirmation, "confirmation approved");
        }
        finally
        {
            gateway.Stop();
        }
    }

    private static CommandRegistry PingRegistry()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "smoke.ping",
            Summary = "smoke ping",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong", new { pong = true })),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "contract.tree",
            Summary = "recursive transport fixture",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("tree", new BranchTreeNode
            {
                BranchName = "root",
                Children =
                [
                    new BranchTreeNode
                    {
                        BranchName = "child",
                        Children =
                        [
                            new BranchTreeNode { BranchName = "leaf-a" },
                            new BranchTreeNode { BranchName = "leaf-b" },
                        ],
                    },
                ],
            })),
        });
        return registry;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string session)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        request.Headers.Add("X-Session-Id", session);
        request.Headers.Add("X-Client-Name", "SmokeWeb");
        return request;
    }

    private static async Task<HttpResponseMessage> McpAsync(
        HttpClient http,
        string? sessionId,
        string? protocol,
        object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = JsonContent.Create(payload),
        };
        if (sessionId != null)
            request.Headers.Add("Mcp-Session-Id", sessionId);
        if (protocol != null)
            request.Headers.Add("MCP-Protocol-Version", protocol);
        return await http.SendAsync(request);
    }

    private static async Task<JsonElement> ReceiveJsonAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return doc.RootElement.Clone();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void DeleteAppData(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private sealed class AuditProbe : IMcpAuditLog
    {
        public List<string> Clients { get; } = [];

        public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs)
            => Clients.Add(client);
    }
}
