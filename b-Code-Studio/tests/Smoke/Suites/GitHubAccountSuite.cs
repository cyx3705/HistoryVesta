using AppShell.Core.Commands;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Smoke.Suites;

internal static class GitHubAccountSuite
{
    private const string Repository = @"C:\repo";

    public static async Task RunAsync(string[] args)
    {
        await VerifyOverviewAndPublicKeysAsync();
        await VerifyConnectionClassificationAsync();
        await VerifyPreviewAndMutationsAsync();
        await VerifyLocalOnlyCommandsAsync();
        VerifyRedaction();
    }

    private static async Task VerifyOverviewAndPublicKeysAsync()
    {
        var ssh = Path.Combine(Path.GetTempPath(), "ohs-github-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ssh);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 public");
            await File.WriteAllTextAsync(Path.Combine(ssh, "id_ed25519"),
                "-----BEGIN OPENSSH PRIVATE KEY-----\nnever-read\n-----END OPENSSH PRIVATE KEY-----");
            var runner = new FakeRunner((file, arguments) =>
            {
                var command = Join(file, arguments);
                if (command == "git --version") return Ok("git version 2.54.0.windows.1");
                if (command == "git credential-manager --version") return Ok("2.7.3");
                if (command == "gh --version") return Fail("gh is not installed");
                if (command.Contains("config --show-origin --get user.name"))
                    return Ok("file:C:/Users/test/.gitconfig\tcommit-author");
                if (command.Contains("config --show-origin --get user.email"))
                    return Ok("file:C:/Users/test/.gitconfig\tauthor@example.test");
                if (command.Contains("remote get-url --push origin"))
                    return Ok("git@github.com:repo-owner/push-repo.git");
                if (command.Contains("remote get-url origin"))
                    return Ok("git@github.com:repo-owner/fetch-repo.git");
                if (command == "git credential-manager github list")
                    return Ok("github.com credential-user\naccount second-user");
                if (file == "ssh-keygen")
                    return Ok("256 SHA256:public-fingerprint comment (ED25519)");
                return Fail("unexpected: " + command);
            });
            var service = new GitHubAccountService(() => Repository, runner, ssh);

            var overview = await service.GetOverviewAsync();

            SmokeKit.Equal("commit-author", overview.EffectiveIdentity.Name, "Git identity fact");
            SmokeKit.Equal("credential-user", overview.CredentialAccounts[0].Account, "GCM identity fact");
            SmokeKit.Equal("repo-owner", overview.Origin.Owner, "origin owner fact");
            SmokeKit.Equal("fetch-repo", overview.Origin.Repository, "origin repository fact");
            SmokeKit.Equal(GitRemoteTransport.Ssh, overview.Origin.Transport, "origin transport fact");
            SmokeKit.True(!overview.GhCliAvailable, "gh absence is optional");
            SmokeKit.Equal(1, overview.Ssh.PublicKeys.Count, "public key enumeration");
            SmokeKit.Equal("id_ed25519.pub", overview.Ssh.PublicKeys[0].FileName, "public key filename");
            SmokeKit.True(runner.Calls.Where(call => call.FileName == "ssh-keygen")
                .All(call => call.Arguments.Any(value => value.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))),
                "private keys never passed to fingerprint tool");
            SmokeKit.True(runner.Calls.SelectMany(call => call.Arguments)
                .All(value => !value.EndsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)),
                "private key path never consumed");
        }
        finally
        {
            SmokeKit.DeleteTree(ssh);
        }
    }

    private static async Task VerifyConnectionClassificationAsync()
    {
        async Task<string> Probe(ToolProcessResult sshResult)
        {
            var runner = new FakeRunner((file, arguments) => file == "ssh"
                ? sshResult
                : RemoteResult(arguments));
            return (await new GitHubAccountService(() => Repository, runner).TestAsync("ssh", 3)).State;
        }

        SmokeKit.Equal("ok", await Probe(new ToolProcessResult(1, "", "Hi! You've successfully authenticated")),
            "SSH greeting succeeds despite non-zero exit");
        SmokeKit.Equal("auth-failed", await Probe(Fail("Permission denied (publickey)")), "SSH auth classification");
        SmokeKit.Equal("network-failed", await Probe(Fail("Temporary failure in name resolution")),
            "SSH DNS classification");
        SmokeKit.Equal("timeout", await Probe(new ToolProcessResult(-1, "", "", TimedOut: true)),
            "SSH timeout classification");
    }

    private static async Task VerifyPreviewAndMutationsAsync()
    {
        var previewRunner = new FakeRunner((_, arguments) => IdentityOrRemoteRead(arguments));
        var previewService = new GitHubAccountService(() => Repository, previewRunner);
        var identityPreview = await previewService.SetIdentityAsync("new-name", "new@example.test", "repository", false);
        var remotePreview = await previewService.SetRemoteAsync(
            "git@github.com:new/fetch.git", "git@github.com:new/push.git", false);
        SmokeKit.True(!identityPreview.Applied && !remotePreview.Applied, "preview reports zero apply");
        SmokeKit.True(previewRunner.Calls.All(call => IsReadOnlyCall(call.Arguments)), "preview performs zero writes");

        var localName = "";
        var localEmail = "";
        var failEmailOnce = true;
        var rollbackRunner = new FakeRunner((_, arguments) =>
        {
            var args = StripRepository(arguments);
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
                return Ok("file:C:/Users/test/.gitconfig\tglobal-name");
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
                return Ok("file:C:/Users/test/.gitconfig\tglobal@example.test");
            if (args.SequenceEqual(["config", "--local", "--get", "user.name"]))
                return string.IsNullOrEmpty(localName) ? Fail() : Ok(localName);
            if (args.SequenceEqual(["config", "--local", "--get", "user.email"]))
                return string.IsNullOrEmpty(localEmail) ? Fail() : Ok(localEmail);
            if (args.Count == 4 && args[0] == "config" && args[1] == "--local" && args[2] == "user.name")
            {
                localName = args[3];
                return Ok();
            }
            if (args.Count == 4 && args[0] == "config" && args[1] == "--local" && args[2] == "user.email")
            {
                if (failEmailOnce)
                {
                    failEmailOnce = false;
                    return Fail("password=should-not-leak");
                }
                localEmail = args[3];
                return Ok();
            }
            if (args.SequenceEqual(["config", "--local", "--unset", "user.name"]))
            {
                localName = "";
                return Ok();
            }
            if (args.SequenceEqual(["config", "--local", "--unset", "user.email"]))
            {
                localEmail = "";
                return Ok();
            }
            return Fail("unexpected identity call");
        });
        var rollbackService = new GitHubAccountService(() => Repository, rollbackRunner);
        var error = await CaptureAsync(() => rollbackService.SetIdentityAsync(
            "local-name", "local@example.test", "repository", true));
        SmokeKit.True(error is InvalidOperationException, "identity write failure surfaces");
        SmokeKit.True(!error!.Message.Contains("should-not-leak", StringComparison.Ordinal), "identity error redacted");
        SmokeKit.Equal("", localName, "identity name rolled back to unset local value");
        SmokeKit.Equal("", localEmail, "identity email remains unset");

        var fetch = "git@github.com:old/fetch.git";
        var push = "git@github.com:old/push.git";
        var remoteRunner = new FakeRunner((_, arguments) =>
        {
            var args = StripRepository(arguments);
            if (args.SequenceEqual(["remote", "get-url", "origin"])) return Ok(fetch);
            if (args.SequenceEqual(["remote", "get-url", "--push", "origin"])) return Ok(push);
            if (args.Count == 5 && args.Take(4).SequenceEqual(["remote", "set-url", "--push", "origin"]))
            {
                push = args[4];
                return Ok();
            }
            if (args.Count == 4 && args.Take(3).SequenceEqual(["remote", "set-url", "origin"]))
            {
                fetch = args[3];
                return Ok();
            }
            return Fail("unexpected remote call");
        });
        var remoteService = new GitHubAccountService(() => Repository, remoteRunner);
        var changed = await remoteService.SetRemoteAsync(
            "git@github.com:new/fetch.git", "git@github.com:new/push.git", true);
        SmokeKit.True(changed.Applied, "remote apply reports success");
        SmokeKit.Equal("git@github.com:new/fetch.git", fetch, "fetch URL changed independently");
        SmokeKit.Equal("git@github.com:new/push.git", push, "push URL changed independently");
        SmokeKit.True(remoteRunner.Calls.Any(call => StripRepository(call.Arguments)
            .SequenceEqual(["remote", "set-url", "--push", "origin", "git@github.com:new/push.git"])),
            "push uses dedicated Git operation");

        await AssertThrowsAsync<ArgumentException>(() => remoteService.SetRemoteAsync(
            "https://user:password@github.com/owner/repo.git", null, false), "remote userinfo rejected");
        await AssertThrowsAsync<ArgumentException>(() => remoteService.SetRemoteAsync(
            "https://github.com/owner/repo.git?token=github_pat_abcdefghijklmnopqrstuvwxyz", null, false),
            "remote token rejected");
    }

    private static async Task VerifyLocalOnlyCommandsAsync()
    {
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("runner must not execute"));
        var registry = new CommandRegistry();
        GitHubAccountCommands.RegisterAll(registry, new GitHubAccountService(() => Repository, runner));
        var bus = new CommandBus(registry, new MemoryLog())
        {
            ConfirmationRouter = (_, _) => true,
        };
        foreach (var command in new[]
                 {
                     "github.login", "github.logout account=test apply=true",
                     "github.identity name=test email=test@example.test apply=true",
                     "github.remote fetch=git@github.com:test/repo.git apply=true",
                 })
        {
            var result = await bus.ExecuteAsync(command, "Web:remote");
            SmokeKit.True(!result.Success && result.Message.Contains("只允许服务器本机", StringComparison.Ordinal),
                "remote mutation rejected: " + command.Split(' ')[0]);
        }
        SmokeKit.Equal(0, runner.Calls.Count, "remote rejection occurs before tools execute");
    }

    private static void VerifyRedaction()
    {
        var privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret-body\n-----END OPENSSH PRIVATE KEY-----";
        var raw = "token=plain password=hunter2 Authorization: Bearer bearer-value "
                  + "github_pat_abcdefghijklmnopqrstuvwxyz https://user:pass@github.com/repo " + privateKey;
        var safe = GitHubRedactor.Redact(raw);
        foreach (var secret in new[] { "plain", "hunter2", "bearer-value", "github_pat_", "user:pass", "secret-body" })
            SmokeKit.True(!safe.Contains(secret, StringComparison.OrdinalIgnoreCase), "redactor removes " + secret);
        var url = GitHubRedactor.SanitizeRemoteUrl(
            "https://user:pass@github.com/owner/repo.git?token=secret#fragment");
        SmokeKit.Equal("https://github.com/owner/repo.git", url, "remote URL strips userinfo/query/fragment");
    }

    private static ToolProcessResult IdentityOrRemoteRead(IReadOnlyList<string> arguments)
    {
        var args = StripRepository(arguments);
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
            return Ok("file:C:/Users/test/.gitconfig\told-name");
        if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
            return Ok("file:C:/Users/test/.gitconfig\told@example.test");
        return RemoteResult(arguments);
    }

    private static ToolProcessResult RemoteResult(IReadOnlyList<string> arguments)
    {
        var args = StripRepository(arguments);
        if (args.SequenceEqual(["remote", "get-url", "origin"]))
            return Ok("git@github.com:owner/fetch.git");
        if (args.SequenceEqual(["remote", "get-url", "--push", "origin"]))
            return Ok("git@github.com:owner/push.git");
        return Fail("unexpected remote read");
    }

    private static bool IsReadOnlyCall(IReadOnlyList<string> arguments)
    {
        var args = StripRepository(arguments);
        return args.Contains("--get") || args.Contains("get-url");
    }

    private static IReadOnlyList<string> StripRepository(IReadOnlyList<string> arguments)
        => arguments.Count >= 2 && arguments[0] == "-C" ? arguments.Skip(2).ToArray() : arguments;

    private static string Join(string file, IReadOnlyList<string> arguments)
        => file + (arguments.Count == 0 ? "" : " " + string.Join(' ', arguments));

    private static ToolProcessResult Ok(string output = "") => new(0, output, "");
    private static ToolProcessResult Fail(string error = "failed") => new(1, "", error);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        var error = await CaptureAsync(action);
        SmokeKit.True(error is T, $"{name}; expected {typeof(T).Name}, actual {error?.GetType().Name ?? "none"}");
    }

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<string>, ToolProcessResult> handler) : IToolProcessRunner
    {
        public List<ToolCall> Calls { get; } = [];

        public Task<ToolProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            int timeoutSeconds = 30,
            CancellationToken cancellation = default)
        {
            Calls.Add(new ToolCall(fileName, arguments.ToArray(), timeoutSeconds));
            return Task.FromResult(handler(fileName, arguments));
        }
    }

    private sealed record ToolCall(string FileName, IReadOnlyList<string> Arguments, int TimeoutSeconds);
}
