using System.IO;
using System.Reflection;
using System.Text.Json;
using AppShell.Core.Docking;
using AppShell.Core.Modules;
using GitHubConnection;

var root = FindProjectRoot();
var temp = Path.Combine(Path.GetTempPath(), "github-connection-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    TestRepositoryPathResolver(temp);
    TestRedaction();
    await TestOverviewAndConnectionAsync(temp);
    await TestIdentityRollbackAsync(temp);
    await TestRemoteRollbackAsync(temp);
    TestModuleContract();
    TestUiDescriptor();
    TestSourceArchitecture(root);
    TestFormalPackage(root);
    Console.WriteLine("GitHubConnectionSmoke: PASS");
}
finally
{
    Directory.Delete(temp, recursive: true);
}

static void TestRepositoryPathResolver(string temp)
{
    var configured = Path.Combine(temp, "configured.git");
    var fallback = Path.Combine(temp, "fallback.git");
    var settings = Path.Combine(temp, "settings.json");
    File.WriteAllText(settings, JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["proj.barerepo"] = configured,
    }));
    Equal(Path.GetFullPath(configured), new RepositoryPathResolver(settings, fallback).Resolve(),
        "absolute configured repository");

    File.WriteAllText(settings, "{broken");
    Equal(Path.GetFullPath(fallback), new RepositoryPathResolver(settings, fallback).Resolve(),
        "broken settings fallback");

    File.WriteAllText(settings, "{\"proj.barerepo\":\"relative.git\"}");
    Equal(Path.GetFullPath(fallback), new RepositoryPathResolver(settings, fallback).Resolve(),
        "relative settings fallback");

    File.Delete(settings);
    Equal(Path.GetFullPath(fallback), new RepositoryPathResolver(settings, fallback).Resolve(),
        "missing settings fallback");
}

static void TestRedaction()
{
    var input = "https://alice:secret@github.com/acme/repo.git " +
                "github_pat_ABC123 token=hidden Authorization: Bearer abc";
    var safe = GitHubRedactor.Redact(input);
    False(safe.Contains("alice", StringComparison.Ordinal), "URL userinfo redacted");
    False(safe.Contains("github_pat_", StringComparison.OrdinalIgnoreCase), "token redacted");
    False(safe.Contains("token=hidden", StringComparison.OrdinalIgnoreCase), "assignment redacted");
    False(safe.Contains("Bearer abc", StringComparison.OrdinalIgnoreCase), "bearer redacted");
}

static async Task TestOverviewAndConnectionAsync(string temp)
{
    var repository = Path.Combine(temp, "overview.git");
    var ssh = Path.Combine(temp, "ssh");
    Directory.CreateDirectory(repository);
    Directory.CreateDirectory(ssh);
    File.WriteAllText(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 AAAA test");

    var runner = new FakeRunner((file, arguments) =>
    {
        var args = StripRepository(arguments);
        if (file == "gh") return Ok("gh version 2.74.0");
        if (file == "ssh-keygen") return Ok("256 SHA256:TEST comment (ED25519)");
        if (file == "ssh") return Fail("Hi! You've successfully authenticated");
        if (args.SequenceEqual(["--version"])) return Ok("git version 2.49.0.windows.1");
        if (args.SequenceEqual(["credential-manager", "--version"])) return Ok("2.6.1");
        if (args.SequenceEqual(["credential-manager", "github", "list"]))
            return Ok("github.com alice\ngithub.com alice\ngithub.com bob");
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
            return Ok("file:.git/config Alice");
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
            return Ok("file:.git/config alice@example.com");
        if (args.SequenceEqual(["remote", "get-url", "origin"]))
            return Ok("git@github.com:acme/studio.git");
        if (args.SequenceEqual(["remote", "get-url", "--push", "origin"]))
            return Ok("git@github.com:acme/studio.git");
        return Ok();
    });

    var service = new GitHubConnectionService(() => repository, runner, ssh);
    var overview = await service.GetOverviewAsync();
    Equal("git version 2.49.0.windows.1", overview.GitVersion, "git version");
    Equal("Alice", overview.EffectiveIdentity.Name, "identity name");
    Equal(GitRemoteTransport.Ssh, overview.Origin.Transport, "scp transport");
    Equal("acme", overview.Origin.Owner, "remote owner");
    Equal(2, overview.CredentialAccounts.Count, "deduplicated accounts");
    Equal(1, overview.Ssh.PublicKeys.Count, "public key count");

    var connection = await service.TestAsync("auto", 3);
    Equal("ok", connection.State, "SSH authenticated result");

    var timeout = new GitHubConnectionService(() => repository,
        new FakeRunner((file, arguments) => file == "ssh"
            ? new ToolProcessResult(-1, "", "", TimedOut: true)
            : runner.Handle(file, arguments)), ssh);
    Equal("timeout", (await timeout.TestAsync("ssh", 3)).State, "SSH timeout result");
}

static async Task TestIdentityRollbackAsync(string temp)
{
    var repository = Path.Combine(temp, "identity.git");
    Directory.CreateDirectory(repository);
    var calls = new List<string>();
    var runner = new FakeRunner((_, arguments) =>
    {
        var args = StripRepository(arguments);
        var text = string.Join(' ', args);
        calls.Add(text);
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
            return Ok("file:.git/config Old Name");
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
            return Ok("file:.git/config old@example.com");
        if (args.SequenceEqual(["config", "--local", "--get", "user.name"]))
            return Ok("Old Name");
        if (args.SequenceEqual(["config", "--local", "--get", "user.email"]))
            return Ok("old@example.com");
        if (args.SequenceEqual(["config", "--local", "user.email", "new@example.com"]))
            return Fail("email write failed github_pat_SECRET");
        return Ok();
    });

    var service = new GitHubConnectionService(() => repository, runner);
    await ThrowsAsync<InvalidOperationException>(() => service.SetIdentityAsync(
        "New Name", "new@example.com", "repository", apply: true));
    True(calls.Contains("config --local user.name Old Name"), "identity name rollback");
    True(calls.Contains("config --local user.email old@example.com"), "identity email rollback");
}

static async Task TestRemoteRollbackAsync(string temp)
{
    var repository = Path.Combine(temp, "remote.git");
    Directory.CreateDirectory(repository);
    var calls = new List<string>();
    var runner = new FakeRunner((_, arguments) =>
    {
        var args = StripRepository(arguments);
        var text = string.Join(' ', args);
        calls.Add(text);
        if (args.SequenceEqual(["remote", "get-url", "origin"]))
            return Ok("https://github.com/old/repo.git");
        if (args.SequenceEqual(["remote", "get-url", "--push", "origin"]))
            return Ok("git@github.com:old/repo.git");
        if (args.SequenceEqual(["remote", "set-url", "--push", "origin", "git@github.com:new/repo.git"]))
            return Fail("push URL failed");
        return Ok();
    });

    var service = new GitHubConnectionService(() => repository, runner);
    await ThrowsAsync<InvalidOperationException>(() => service.SetRemoteAsync(
        "https://github.com/new/repo.git", "git@github.com:new/repo.git", apply: true));
    True(calls.Contains("remote set-url origin https://github.com/old/repo.git"),
        "fetch URL rollback");
}

static void TestModuleContract()
{
    var info = new ModuleInfo();
    Equal("github", info.ModuleName, "module command domain");
    Equal("1.0.1", info.Version, "module version");
    Equal(typeof(GitHubCommands), info.MainClassType, "precise command class");
    False(info.Open, "module precise exposure");

    var methods = typeof(GitHubCommands).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(method => method.DeclaringType == typeof(GitHubCommands))
        .OrderBy(method => method.Name, StringComparer.Ordinal)
        .ToList();
    Equal("accounts,status,test", string.Join(',', methods.Select(method => method.Name)),
        "only three commands exposed");
    foreach (var method in methods)
    {
        var metadata = method.GetCustomAttribute<ModuleCommandAttribute>();
        True(metadata?.Readonly == true, $"{method.Name} readonly metadata");
    }
}

static void TestUiDescriptor()
{
    var descriptor = GitHubConnectionUiModule.CreateDescriptor();
    Equal("github.account", descriptor.Id, "stable window ID");
    Equal("GitHub 连接", descriptor.Title, "window title");
    Equal(DockSide.Right, descriptor.DefaultSide, "right docking");
    Equal(0.38, descriptor.DefaultRatio, "window ratio");
    True(descriptor.IsSingleton, "singleton window");
    True(descriptor.ContentFactory != null, "window content factory");
    True(typeof(IUiModule).IsAssignableFrom(typeof(GitHubConnectionUiModule)), "UI module contract");
    True(typeof(IShellUiAware).IsAssignableFrom(typeof(GitHubConnectionUiModule)), "UI registrar contract");

    // 无窗服务宿主不注入注册器：此时 CreateUi 必须弃权，不得空引用。
    var serviceHosted = new GitHubConnectionUiModule();
    serviceHosted.CreateUi();
    serviceHosted.DestroyUi();
}

static void TestSourceArchitecture(string root)
{
    var production = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                       && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                       && !path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"));
    foreach (var path in production)
        True(File.ReadLines(path).Count() <= 700, $"production source <= 700: {Path.GetFileName(path)}");
    var smoke = Path.Combine(root, "tests", "Smoke", "Program.cs");
    True(File.ReadLines(smoke).Count() <= 550, "Smoke source <= 550");
}

static void TestFormalPackage(string root)
{
    var z = Path.Combine(Directory.GetParent(root)!.FullName, "z-GitHubConnection");
    var files = Directory.EnumerateFiles(z, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(z, path).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
    Equal("GitHubConnection.dll,GitHubConnection.xml,module.manifest.json",
        string.Join(',', files), "formal package file set");

    using var manifest = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(z, "module.manifest.json")));
    var rootElement = manifest.RootElement;
    Equal("GitHubConnection", rootElement.GetProperty("name").GetString(), "manifest name");
    Equal("1.0.1", rootElement.GetProperty("version").GetString(), "manifest version");
    Equal("z-GitHubConnection/GitHubConnection.dll",
        rootElement.GetProperty("artifact").GetString(), "manifest artifact");
    Equal("z-GitHubConnection/GitHubConnection.xml",
        rootElement.GetProperty("docs").GetString(), "manifest docs");
    Equal("readonly", rootElement.GetProperty("mcpExposure").GetString(), "manifest MCP exposure");
    True(rootElement.GetProperty("ui").GetBoolean(), "manifest UI flag");

    var assembly = AssemblyName.GetAssemblyName(Path.Combine(z, "GitHubConnection.dll"));
    Equal(new Version(1, 0, 1, 0), assembly.Version, "formal assembly version");
}

static string FindProjectRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "GitHubConnection.csproj")))
            return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("GitHubConnection project root not found");
}

static IReadOnlyList<string> StripRepository(IReadOnlyList<string> arguments)
    => arguments.Count >= 2 && arguments[0] == "-C" ? arguments.Skip(2).ToArray() : arguments;

static ToolProcessResult Ok(string output = "") => new(0, output, "");
static ToolProcessResult Fail(string error) => new(1, "", error);

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

static void True(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
}

static void False(bool value, string name) => True(!value, name);

sealed class FakeRunner(
    Func<string, IReadOnlyList<string>, ToolProcessResult> handler) : IToolProcessRunner
{
    public ToolProcessResult Handle(string file, IReadOnlyList<string> arguments)
        => handler(file, arguments);

    public Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds = 30,
        CancellationToken cancellation = default)
        => Task.FromResult(handler(fileName, arguments));
}
