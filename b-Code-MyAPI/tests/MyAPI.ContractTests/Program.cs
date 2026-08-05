using System.Text.Json;
using MyAPI.Abstractions;
using MyAPI.Host;
using MyAPI.Runtime;
using MyAPI.TestCapability;
using MyAPI.TestModule;

var tests = new (string Name, Func<Task> Run)[]
{
    ("capability can be consumed without MyAPI", DirectCapabilityConsumption),
    ("only explicitly registered commands are exposed", ExplicitRegistration),
    ("invalid arguments are rejected", InvalidArguments),
    ("command requests snapshot caller arguments", RequestSnapshotsArguments),
    ("failed replacement leaves the previous snapshot intact", AtomicReplacement),
    ("failed module reload preserves the active snapshot", FailedModuleReloadPreservesSnapshot),
    ("command timeout is enforced", TimeoutIsEnforced),
    ("host defaults are minimal and local", HostDefaults)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static Task DirectCapabilityConsumption()
{
    var capabilityReferences = typeof(Calculator).Assembly.GetReferencedAssemblies();
    Assert(!capabilityReferences.Any(x => x.Name?.StartsWith("MyAPI", StringComparison.OrdinalIgnoreCase) == true),
        "TestCapability must not reference MyAPI runtime assemblies.");
    Assert(new Calculator().Add(2, 3) == 5, "Calculator direct invocation failed.");
    return Task.CompletedTask;
}

static async Task ExplicitRegistration()
{
    var catalog = new CommandCatalog();
    catalog.RegisterOrReplace(new TestModule());
    Assert(catalog.Modules.Count == 1, "Expected one module.");
    Assert(catalog.Commands.Count == 4, "Expected four explicit commands.");

    var result = await catalog.DispatchAsync(new CommandRequest("test.calculator.add", Args("{\"a\":2,\"b\":4}")));
    Assert(result.IsSuccess && result.Value is int value && value == 6, "Command dispatch returned the wrong value.");
}

static async Task InvalidArguments()
{
    var catalog = new CommandCatalog();
    catalog.RegisterOrReplace(new TestModule());
    var result = await catalog.DispatchAsync(new CommandRequest("test.calculator.add", Args("{\"a\":2}")));
    Assert(result.Status == CommandStatus.Rejected && result.Error?.Code == "missing_argument", "Missing arguments were not rejected.");
}

static async Task RequestSnapshotsArguments()
{
    var catalog = new CommandCatalog();
    catalog.RegisterOrReplace(new TestModule());
    var source = Args("{\"a\":2,\"b\":4}");
    var request = new CommandRequest("test.calculator.add", source);
    using var replacement = JsonDocument.Parse("100");
    source["a"] = replacement.RootElement.Clone();
    var result = await catalog.DispatchAsync(request);
    Assert(result.IsSuccess && result.Value is int value && value == 6, "CommandRequest did not snapshot its arguments.");
}

static Task AtomicReplacement()
{
    var catalog = new CommandCatalog();
    catalog.RegisterOrReplace(new TestModule());
    var before = catalog.Commands.Select(x => x.Id).ToArray();
    AssertThrows<CommandRegistrationException>(() => catalog.ReplaceAll([new TestModule(), new CollisionModule()]));
    Assert(before.SequenceEqual(catalog.Commands.Select(x => x.Id)), "Failed replacement changed the active snapshot.");
    return Task.CompletedTask;
}

static Task FailedModuleReloadPreservesSnapshot()
{
    var directory = Path.Combine(Path.GetTempPath(), $"myapi-contract-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    var modulePath = Path.Combine(directory, Path.GetFileName(typeof(TestModule).Assembly.Location));
    try
    {
        File.Copy(typeof(TestModule).Assembly.Location, modulePath);
        File.Copy(typeof(Calculator).Assembly.Location, Path.Combine(directory, Path.GetFileName(typeof(Calculator).Assembly.Location)));
        var catalog = new CommandCatalog();
        using var host = new MyAPI.Host.ModuleHost(directory, catalog);
        host.Start(hotReload: false);
        Assert(catalog.Commands.Count == 4, "Initial module load failed.");
        File.WriteAllBytes(modulePath, [0, 1, 2, 3]);
        AssertThrows<InvalidOperationException>(() => host.Reload());
        Assert(catalog.Commands.Count == 4, "Failed reload removed the active command snapshot.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
}

static async Task TimeoutIsEnforced()
{
    var catalog = new CommandCatalog(new CommandCatalogOptions { DefaultTimeout = TimeSpan.FromMilliseconds(20) });
    catalog.RegisterOrReplace(new SlowModule());
    var result = await catalog.DispatchAsync(new CommandRequest("slow.wait"));
    Assert(result.Status == CommandStatus.TimedOut, "Timeout was not enforced.");
}

static Task HostDefaults()
{
    var options = MyApiHostOptions.From(_ => null, Directory.GetCurrentDirectory());
    Assert(!options.EnableHttp && !options.EnableMcp && !options.EnableModules, "Host defaults must be disabled.");
    Assert(options.BindAddress == "127.0.0.1", "Host must default to loopback.");
    AssertThrows<InvalidOperationException>(() => MyApiHostOptions.From(key => key == "BindAddress" ? "0.0.0.0" : null, Directory.GetCurrentDirectory()));
    return Task.CompletedTask;
}

static Dictionary<string, JsonElement> Args(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.EnumerateObject()
        .ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    catch (T) { }
}

sealed class CollisionModule : IMyApiModule
{
    public ModuleDescriptor Descriptor { get; } = new("collision", "Collision", "1.0.0");
    public void Register(ICommandRegistry registry) => registry.Register(
        new CommandDescriptor("test.calculator.add", Descriptor.Id, "collision"),
        (_, _, _) => ValueTask.FromResult<object?>(0));
}

sealed class SlowModule : IMyApiModule
{
    public ModuleDescriptor Descriptor { get; } = new("slow", "Slow", "1.0.0");
    public void Register(ICommandRegistry registry) => registry.Register(
        new CommandDescriptor("slow.wait", Descriptor.Id, "waits"),
        async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        });
}
