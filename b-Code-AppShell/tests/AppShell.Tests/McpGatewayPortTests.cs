using AppShell.Core;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;
using AppShell.Core.Storage;
using AppShell.Services.Mcp;
using Xunit;
using System.Net;
using System.Net.Sockets;

namespace AppShell.Tests;

public sealed class McpGatewayPortTests
{
    [Fact]
    public void DefaultPortIsDerivedPerApplication()
    {
        using var first = Fixture.Create("PortAppAlpha");
        using var second = Fixture.Create("PortAppBeta");

        Assert.True(first.Gateway.Start(null).Success);
        Assert.True(second.Gateway.Start(null).Success);
        Assert.NotEqual(first.Gateway.Port, second.Gateway.Port);
    }

    [Fact]
    public void OccupiedDerivedPortRetriesSequentially()
    {
        using var first = Fixture.Create("SamePortApp");
        using var second = Fixture.Create("SamePortApp");

        Assert.True(first.Gateway.Start(null).Success);
        Assert.True(second.Gateway.Start(null).Success);
        Assert.Equal(first.Gateway.Port + 1, second.Gateway.Port);
    }

    [Fact]
    public void ExplicitPortHasPriority()
    {
        using var fixture = Fixture.Create("ExplicitPortApp");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.True(fixture.Gateway.Start(port).Success);
        Assert.Equal(port, fixture.Gateway.Port);
        Assert.Equal(port.ToString(), fixture.Settings.Get(McpGateway.KeyPort));
    }

    [Fact]
    public void ExplicitPortRetryPersistsActualPortAndStopClearsRuntimePort()
    {
        using var first = Fixture.Create("RetryPortOwner");
        using var second = Fixture.Create("RetryPortCandidate");
        var port = FreePort();
        Assert.True(first.Gateway.Start(port).Success);

        Assert.True(second.Gateway.Start(port).Success);
        Assert.Equal(port + 1, second.Gateway.Port);
        Assert.Equal((port + 1).ToString(), second.Settings.Get(McpGateway.KeyPort));
        Assert.True(second.Gateway.Stop().Success);
        Assert.Equal(0, second.Gateway.Port);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, MemorySettings settings, McpGateway gateway)
        {
            Root = root;
            Settings = settings;
            Gateway = gateway;
        }

        public string Root { get; }
        public MemorySettings Settings { get; }
        public McpGateway Gateway { get; }

        public static Fixture Create(string appName)
        {
            var root = Path.Combine(Path.GetTempPath(), "AppShell.Tests", Guid.NewGuid().ToString("N"));
            var settings = new MemorySettings();
            var log = new NullLog();
            var gateway = new McpGateway(
                () => null,
                settings,
                log,
                new NullAudit(),
                new PromptGovernanceStore(root, log),
                new AppShell.Core.ApplicationIdentity(appName, "3.0.0", "3.0.0", "3.0.0.0"));
            return new Fixture(root, settings, gateway);
        }

        public void Dispose()
        {
            Gateway.Dispose();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullAudit : IMcpAuditLog
    {
        public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs) { }
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
