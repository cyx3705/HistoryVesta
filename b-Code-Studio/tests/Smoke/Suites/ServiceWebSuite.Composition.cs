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
    private static async Task VerifyServiceCompositionAsync()
    {
        var temporaryName = "OHS-Composition-Smoke-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            temporaryName);
        var composition = StudioServiceCompositionFactory.Create(false, temporaryName);
        try
        {
            SmokeKit.Equal(
                StudioServiceCompositionFactory.DefaultServicePort.ToString(),
                composition.Settings.Get(WebGateway.KeyPort),
                "service composition pins the OHS default endpoint port");
            ServiceCommands.RegisterAll(
                composition.Registry,
                composition,
                static () => { },
                "OneHistoryStudio.exe",
                serviceArguments: ["--service-host"]);
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

    private static void VerifyConfiguredServicePortPreserved()
    {
        var temporaryName = "OHS-Configured-Port-Smoke-" + Guid.NewGuid().ToString("N");
        var paths = new AppPaths(temporaryName);
        var settings = new SettingsService(paths);
        settings.Set(WebGateway.KeyPort, "19438");
        var composition = StudioServiceCompositionFactory.Create(false, temporaryName);
        try
        {
            SmokeKit.Equal(
                "19438",
                composition.Settings.Get(WebGateway.KeyPort),
                "service composition preserves an explicitly configured endpoint port");
        }
        finally
        {
            composition.Dispose();
            DeleteAppData(paths.Root);
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
}
