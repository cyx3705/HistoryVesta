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

internal static class ServiceWebSuite
{
    public static async Task RunAsync(string[] args)
    {
        await VerifyCommandRoutingAsync();
        await VerifyServiceCompositionAsync();
        await VerifyMcpSessionsAsync();
        await VerifyWebGatewayAsync();
        Console.WriteLine("ServiceWebSmoke: PASS");
    }

    private static async Task VerifyServiceCompositionAsync()
    {
        var temporaryName = "OHS-Composition-Smoke-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            temporaryName);
        var composition = StudioServiceCompositionFactory.Create(false, temporaryName);
        try
        {
            ServiceCommands.RegisterAll(
                composition.Registry,
                composition,
                static () => { },
                "OneHistoryStudio.Service.exe");
            SmokeKit.True(composition.Registry.All().Count > 0, "service command registry is non-empty");
            foreach (var name in new[]
                     {
                         "help", "proj.list",
                         "mcp.status", "module.list", "svc.status", "web.status",
                     })
            {
                SmokeKit.True(composition.Registry.TryGet(name, out _), $"service command {name}");
            }
            SmokeKit.True(!composition.Registry.All().Any(command =>
                    command.Name.StartsWith("db.", StringComparison.OrdinalIgnoreCase)),
                "service composition omits retired database commands");

            SmokeKit.True(composition.Registry.All().All(command =>
                    command.ExecutionSite != CommandExecutionSite.Frontend),
                "service startup does not fabricate a frontend command catalog");
            var catalog = composition.Bus.ExecuteAsync("command.list", "smoke")
                .GetAwaiter().GetResult();
            var catalogJson = JsonSerializer.SerializeToElement(catalog.Data);
            var rows = StudioCommandDataDeserializer.Deserialize("command.list", catalogJson)
                as List<CommandCatalogRow>;
            SmokeKit.True(rows != null,
                "remote command catalog typed projection");
            AssertCatalogProjection(
                rows!, composition.Registry, composition.Mcp?.Policy ?? "readonly",
                "in-process serialized catalog");
            var worktrees = JsonSerializer.SerializeToElement(new List<WorktreeInfo>
            {
                new("2026-020-OneHistoryStudio", @"C:\OneHistory", "now"),
            });
            var typedWorktrees = StudioCommandDataDeserializer.Deserialize("proj.list", worktrees)
                as List<WorktreeInfo>;
            SmokeKit.True(
                typedWorktrees is { Count: 1 }
                && typedWorktrees[0].BranchName == "2026-020-OneHistoryStudio",
                "remote project list typed projection");

            var port = FreePort();
            SmokeKit.True(composition.Web!.Start(port).Success, "composition web start");
            using var client = new ShellServiceClient(new Uri($"http://127.0.0.1:{port}/"), "SmokeShell")
            {
                DataDeserializer = StudioCommandDataDeserializer.Deserialize,
            };
            SmokeKit.True(await client.WaitForReadyAsync(TimeSpan.FromSeconds(2)), "composition shell ready");
            var remoteCatalog = await client.ExecuteAsync("command.list", "UI");
            var remoteRows = remoteCatalog.Data as List<CommandCatalogRow>;
            SmokeKit.True(remoteRows != null,
                "HTTP command catalog typed projection");
            AssertCatalogProjection(
                remoteRows!,
                composition.Registry,
                composition.Mcp?.Policy ?? "readonly",
                "HTTP command catalog");
            SmokeKit.True(remoteRows!.All(row =>
                    !row.CommandName.StartsWith("db.", StringComparison.OrdinalIgnoreCase)),
                "HTTP command catalog omits retired database commands");
        }
        finally
        {
            composition.Dispose();
            DeleteAppData(root);
        }
    }

    private static void AssertCatalogProjection(
        IReadOnlyList<CommandCatalogRow> rows,
        CommandRegistry registry,
        string policy,
        string scope)
    {
        var descriptors = registry.All().ToDictionary(
            descriptor => descriptor.Name,
            StringComparer.OrdinalIgnoreCase);
        var exporter = new CommandSchemaExporter(registry);
        SmokeKit.Equal(descriptors.Count, rows.Count, $"{scope}: complete command set");
        SmokeKit.Equal(rows.Count, rows.Select(row => row.CommandName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            $"{scope}: command names are unique");

        foreach (var row in rows)
        {
            SmokeKit.True(descriptors.TryGetValue(row.CommandName, out var descriptor),
                $"{scope}: registered command {row.CommandName}");
            var command = descriptor!;
            var dot = command.Name.IndexOf('.');
            var expectedDomain = dot > 0 ? command.Name[..dot] : "core";
            var rawSource = registry.GetSource(command.Name);
            var module = rawSource.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
            var expectedSource = module ? "module" : rawSource;
            var expectedSourceDetail = module ? rawSource["module:".Length..] : null;
            var tool = exporter.Find(command.Name);

            SmokeKit.Equal(command.Name, row.CommandName, $"{scope}: name {command.Name}");
            SmokeKit.Equal(expectedDomain, row.Domain, $"{scope}: domain {command.Name}");
            SmokeKit.Equal(command.Summary, row.Summary, $"{scope}: summary {command.Name}");
            SmokeKit.Equal(command.Example, row.Example, $"{scope}: example {command.Name}");
            SmokeKit.Equal(command.Parameters.Count, row.ParameterCount,
                $"{scope}: parameter count {command.Name}");
            SmokeKit.Equal(expectedSource, row.Source, $"{scope}: source {command.Name}");
            SmokeKit.Equal(expectedSourceDetail, row.SourceDetail,
                $"{scope}: source detail {command.Name}");
            SmokeKit.Equal(command.IsDangerous, row.Dangerous,
                $"{scope}: dangerous {command.Name}");
            SmokeKit.Equal(command.RequiresUiThread, row.RequiresUiThread,
                $"{scope}: UI thread {command.Name}");
            SmokeKit.Equal(tool?.ToolName, row.McpToolName, $"{scope}: MCP name {command.Name}");
            SmokeKit.Equal(McpExposurePolicy.State(command), row.McpState,
                $"{scope}: MCP state {command.Name}");
            SmokeKit.Equal(McpExposurePolicy.IsVisible(command, policy), row.PolicyVisible,
                $"{scope}: MCP visibility {command.Name}");
            SmokeKit.Equal(McpExposurePolicy.HardExclusionReason(command.Name), row.HardExclusionReason,
                $"{scope}: MCP exclusion {command.Name}");
            SmokeKit.True(!row.Customized && row.CurrentRevision == null
                          && row.OpenProposals == 0 && row.IncidentCount == 0,
                $"{scope}: fresh governance state {command.Name}");
        }
    }

    private static void AssertProxyMetadata(CommandDescriptor source, CommandDescriptor proxy)
    {
        SmokeKit.Equal(source.Name, proxy.Name, $"proxy name {source.Name}");
        SmokeKit.Equal(source.Summary, proxy.Summary, $"proxy summary {source.Name}");
        SmokeKit.Equal(source.Example, proxy.Example, $"proxy example {source.Name}");
        SmokeKit.Equal(source.Readonly, proxy.Readonly, $"proxy readonly {source.Name}");
        SmokeKit.Equal(source.SupportsUndo, proxy.SupportsUndo, $"proxy undo {source.Name}");
        SmokeKit.Equal(source.IsDangerous, proxy.IsDangerous, $"proxy dangerous {source.Name}");
        SmokeKit.Equal(CommandExecutionSite.Frontend, proxy.ExecutionSite,
            $"proxy execution site {source.Name}");
        SmokeKit.True(!proxy.AllowUnspecifiedParameters,
            $"proxy rejects unspecified parameters {source.Name}");
        AssertParameters(source, proxy, "proxy");
    }

    private static void AssertSharedBuiltinMetadata(
        CommandDescriptor desktop,
        CommandDescriptor service)
    {
        var name = desktop.Name;
        SmokeKit.Equal(desktop.Name, service.Name, $"shared name {name}");
        SmokeKit.Equal(desktop.Summary, service.Summary, $"shared summary {name}");
        SmokeKit.Equal(desktop.Example, service.Example, $"shared example {name}");
        SmokeKit.Equal(desktop.Readonly, service.Readonly, $"shared readonly {name}");
        SmokeKit.Equal(desktop.SupportsUndo, service.SupportsUndo, $"shared undo {name}");
        SmokeKit.Equal(desktop.Dangerous, service.Dangerous, $"shared danger flag {name}");
        SmokeKit.Equal(desktop.IsDangerous, service.IsDangerous, $"shared dangerous {name}");
        SmokeKit.Equal(desktop.ExecutionSite, service.ExecutionSite, $"shared execution site {name}");
        SmokeKit.Equal(desktop.AllowUnspecifiedParameters, service.AllowUnspecifiedParameters,
            $"shared unspecified parameters {name}");
        SmokeKit.Equal(desktop.ConfirmPrompt == null, service.ConfirmPrompt == null,
            $"shared confirmation presence {name}");
        SmokeKit.Equal(name is "db.query" or "db.sql", desktop.RequiresUiThread,
            $"desktop UI capability {name}");
        SmokeKit.True(!service.RequiresUiThread, $"service has no UI-thread dependency {name}");
        AssertParameters(desktop, service, "shared");

        var withoutWhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["table"] = "users",
            ["sql"] = "SELECT 1",
        };
        var withWhere = new Dictionary<string, string>(withoutWhere, StringComparer.OrdinalIgnoreCase)
        {
            ["where"] = "id=1",
        };
        foreach (var values in new[] { withoutWhere, withWhere })
        {
            var desktopPrompt = desktop.ConfirmPrompt?.Invoke(
                new CommandContext(desktop, values, "Smoke", null, CancellationToken.None));
            var servicePrompt = service.ConfirmPrompt?.Invoke(
                new CommandContext(service, values, "Smoke", null, CancellationToken.None));
            SmokeKit.Equal(desktopPrompt, servicePrompt,
                $"shared confirmation result {name} where={values.ContainsKey("where")}");
            var confirmationExpected = name == "db.sql"
                                       || (name is "db.update" or "db.delete"
                                           && !values.ContainsKey("where"));
            SmokeKit.Equal(confirmationExpected, desktopPrompt != null,
                $"shared confirmation policy {name} where={values.ContainsKey("where")}");
        }
    }

    private static void AssertParameters(
        CommandDescriptor expected,
        CommandDescriptor actual,
        string scope)
    {
        SmokeKit.Equal(expected.Parameters.Count, actual.Parameters.Count,
            $"{scope} parameter count {expected.Name}");
        for (var i = 0; i < expected.Parameters.Count; i++)
        {
            var expectedParameter = expected.Parameters[i];
            var actualParameter = actual.Parameters[i];
            SmokeKit.Equal(expectedParameter.Name, actualParameter.Name,
                $"{scope} parameter name {expected.Name}[{i}]");
            SmokeKit.Equal(expectedParameter.Description, actualParameter.Description,
                $"{scope} parameter description {expected.Name}.{expectedParameter.Name}");
            SmokeKit.Equal(expectedParameter.Type, actualParameter.Type,
                $"{scope} parameter type {expected.Name}.{expectedParameter.Name}");
            SmokeKit.Equal(expectedParameter.Required, actualParameter.Required,
                $"{scope} parameter required {expected.Name}.{expectedParameter.Name}");
            SmokeKit.Equal(expectedParameter.Default, actualParameter.Default,
                $"{scope} parameter default {expected.Name}.{expectedParameter.Name}");
            SmokeKit.Equal(expectedParameter.Position, actualParameter.Position,
                $"{scope} parameter position {expected.Name}.{expectedParameter.Name}");
            SmokeKit.True((expectedParameter.AllowedValues ?? []).SequenceEqual(
                    actualParameter.AllowedValues ?? [], StringComparer.OrdinalIgnoreCase),
                $"{scope} parameter allowed values {expected.Name}.{expectedParameter.Name}");
        }
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
