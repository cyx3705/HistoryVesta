using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Core.Modules;
using AppShell.Core.Storage;
using AppShell.Services.Modules;
using Xunit;

namespace AppShell.Tests
{

    public sealed class ModuleHostContextTests
    {
        [Fact]
        public async Task ContextAwareModuleReceivesHostServicesAndOwnsItsCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "AppShell.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(slotDirectory);
            Directory.CreateDirectory(dataDirectory);

            var modulePath = Path.Combine(slotDirectory, "ContextFixture.dll");
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location, modulePath);

            var registry = new CommandRegistry();
            var log = new TestLog();
            var settings = new MemorySettings();
            settings.Set("fixture.value", "attached");
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
            };

            try
            {
                host.Attach(registry, bus, settings, dataDirectory);
                host.Start();

                Assert.True(registry.TryGet("contextfixture.context-probe", out _));
                Assert.True(registry.TryGet("contextfixture.Probe", out _));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));

                var result = await bus.ExecuteAsync("contextfixture.context-probe", "test");

                Assert.True(result.Success, result.Message);
                Assert.Equal($"{Path.GetFullPath(dataDirectory)}|attached", result.Message);
                Assert.Equal(2, Assert.Single(host.Modules).CommandCount);
            }
            finally
            {
                host.Dispose();
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DisabledModuleDoesNotAttachContextOrRegisterCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "AppShell.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            File.Copy(
                typeof(ContextFixtureModuleInfo).Assembly.Location,
                Path.Combine(slotDirectory, "ContextFixture.dll"));

            var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable);
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, "0");
            var registry = new CommandRegistry();
            var log = new TestLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(modulesDirectory, log)
            {
                EnableFileWatching = false,
                EnableUiModules = false,
            };

            try
            {
                host.Attach(registry, bus, settings, Path.Combine(root, "data"));
                host.Start();

                Assert.Empty(host.Modules);
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, previous);
                Directory.Delete(root, recursive: true);
            }
        }

        private sealed class MemorySettings : ISettingsService
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

            public string? Get(string key) => _values.GetValueOrDefault(key);

            public int GetInt(string key, int fallback)
                => int.TryParse(Get(key), out var value) ? value : fallback;

            public void Set(string key, string value) => _values[key] = value;

            public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
        }

        private sealed class TestLog : IShellLog
        {
            public void Log(ShellLogLevel level, string category, string message) { }

            public event EventHandler<ShellLogEntry>? EntryAdded
            {
                add { }
                remove { }
            }

            public IReadOnlyList<ShellLogEntry> Snapshot() => [];
        }
    }

    public sealed class ContextFixtureModuleInfo : BaseVariable.ModuleInfoBase
    {
        public const string EnabledVariable = "APPSHELL_CONTEXT_FIXTURE_ENABLED";

        public override string ModuleName => "contextfixture";

        public override Type MainClassType => typeof(ContextAwareFixture);

        public override bool Enabled
            => !string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "0", StringComparison.Ordinal);
    }

    public sealed class ContextAwareFixture : IModuleContextAware
    {
        public void Attach(IModuleContext context)
        {
            context.RegisterCommands(registry => registry.Register(new CommandDescriptor
            {
                Name = "contextfixture.context-probe",
                Summary = "Returns the injected host context values.",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                    $"{context.DataDirectory}|{context.Settings.Get("fixture.value")}")),
            }));
        }

        public string Probe() => "reflected";
    }

}

namespace BaseVariable
{
    public abstract class ModuleInfoBase
    {
        public virtual string ModuleName => "fixture";

        public virtual string Description => "ModuleHost context fixture";

        public virtual string Author => "AppShell.Tests";

        public virtual string Version => "3.1.9";

        public virtual bool Open => false;

        public virtual Type? MainClassType => null;

        public virtual bool Enabled => true;
    }
}
