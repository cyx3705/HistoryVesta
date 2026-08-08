using HistoryJanus;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 命令目录合同：组合根是 Janus 业务命令的唯一权威注册入口，Help/命令集/MCP/手册
/// 全部从运行时注册表投影（QA-001；代码管道化 §4.1）。这里的断言替代人工核对，
/// 使命令数量、来源、只读与危险确认的漂移在 Contracts 阶段即被阻断。
/// </summary>
public sealed class CommandCatalogContractTests
{
    // 组合根注册的 33 条业务命令；第 34 条 janus.status 由模块宿主从
    // [ModuleCommand] 投影，不在组合根内（QA-001 的 34 = 33 + Status）。
    // 3.5.0 全部指令改为 janus.<类>.<方法> 三段式全小写命名。
    private const int ExpectedCommandCount = 33;

    private static readonly string[] ReadOnlyCommands =
    [
        "janus.proj.list", "janus.proj.tree", "janus.proj.scan", "janus.proj.config", "janus.meta.list",
        "janus.history.list", "janus.history.show", "janus.history.diff",
        "janus.gitrule.scan", "janus.gitrule.review", "janus.gitrule.list",
        "janus.github.status", "janus.github.accounts", "janus.github.test",
    ];

    private static readonly string[] ConfirmedCommands =
    [
        "janus.proj.delete", "janus.proj.commitall", "janus.proj.pushall", "janus.proj.repair",
        "janus.history.rollback", "janus.history.reset", "janus.history.forcepush",
        "janus.gitrule.sync", "janus.gitrule.set", "janus.gitrule.batchset", "janus.gitrule.remove",
    ];

    [Fact]
    public void CompositionRegistersTheFullCommandCatalog()
    {
        using var fixture = new CompositionFixture();

        Assert.Equal(ExpectedCommandCount, fixture.Registry.All().Count);
    }

    [Fact]
    public void EveryCommandCarriesTheRequestedSource()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            Assert.Equal(
                CompositionFixture.Source,
                fixture.Registry.GetSource(descriptor.Name));
        }
    }

    [Fact]
    public void CommandNamesAreUniqueAndLowercase()
    {
        using var fixture = new CompositionFixture();
        var names = fixture.Registry.All().Select(descriptor => descriptor.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Equal(name, name.ToLowerInvariant()));
    }

    [Fact]
    public void EveryCommandHasHelpProjectionText()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary), descriptor.Name);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Example), descriptor.Name);
        }
    }

    [Fact]
    public void ReadOnlyProjectionMatchesTheContract()
    {
        using var fixture = new CompositionFixture();
        var actual = fixture.Registry.All()
            .Where(descriptor => descriptor.Readonly)
            .Select(descriptor => descriptor.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ReadOnlyCommands.OrderBy(name => name, StringComparer.Ordinal),
            actual);
    }

    [Fact]
    public void DangerousCommandsCarryConfirmationGates()
    {
        using var fixture = new CompositionFixture();

        foreach (var name in ConfirmedCommands)
        {
            Assert.True(
                fixture.Registry.TryGet(name, out var descriptor), $"missing: {name}");
            Assert.True(
                descriptor.IsDangerous,
                $"{name} must expose a confirmation gate for catalog/MCP projection");
        }
    }

    [Fact]
    public void ReadOnlyCommandsNeverRequireConfirmation()
    {
        using var fixture = new CompositionFixture();

        foreach (var name in ReadOnlyCommands)
        {
            Assert.True(
                fixture.Registry.TryGet(name, out var descriptor), $"missing: {name}");
            Assert.False(descriptor.IsDangerous, $"{name} is read-only and must stay confirmation-free");
        }
    }

    private sealed class CompositionFixture : IDisposable
    {
        public const string Source = "test:contracts";

        private readonly string _dataDirectory;

        public CompositionFixture()
        {
            _dataDirectory = Path.Combine(
                Path.GetTempPath(), "HistoryJanus-Contracts", Guid.NewGuid().ToString("N"));
            Registry = new CommandRegistry();
            var bus = new CommandBus(Registry, new NullLog());
            StudioBusinessCompositionFactory.Register(
                Registry, bus, new MemorySettings(), new NullLog(), _dataDirectory, Source);
        }

        public CommandRegistry Registry { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dataDirectory))
                    Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响合同结论；操作系统会回收 %TEMP%。
            }
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];

        public void Log(ShellLogLevel level, string category, string message)
            => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));
    }
}
