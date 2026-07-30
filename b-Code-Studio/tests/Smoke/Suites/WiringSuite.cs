using AppShell.Core.Commands;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Services.Mcp;
using AppShell.Shell.Mcp;
using OneHistoryStudio.Git;
using static OneHistoryStudio.Smoke.SmokeKit;

namespace OneHistoryStudio.Smoke.Suites;

/// <summary>
/// 装配路径回归(0.4.4 新增)。
///
/// 起因:0.4.4 把 MCP/模块/提示词治理上抛为框架自带能力后,ShellWindow 曾同时调用
/// McpCommands.RegisterAll(聚合入口,内部级联注册 command.*/prompt.*)与
/// CommandCatalogCommands.RegisterAll,导致 command.list 二次注册、启动即崩。
/// 既有五套冒烟都用**独立 registry 分别调各 RegisterAll**,从不走真实聚合入口,
/// 因此漏掉了这个只在完整装配路径出现的冲突。
///
/// 本套用例在**同一个 registry**上按 ShellWindow 的真实次序把全部指令域注册一遍,
/// 任何重复注册都会让 CommandRegistry 冲突即抛(§5.3),从而在无头环境复现装配冲突。
/// </summary>
internal static class WiringSuite
{
    public static async Task RunAsync(string[] args)
    {
        await Task.CompletedTask;
        var testName = $"OneHistoryStudio.Wiring.{Guid.NewGuid():N}";
        var paths = new AppPaths(testName);
        ShellLog? log = null;
        try
        {
            log = new ShellLog(paths);
            var settings = new SettingsService(paths);
            var registry = new CommandRegistry();
            var bus = new CommandBus(registry, log);

            // ---- 按 ShellWindow 的真实装配次序,在同一 registry 上注册全部指令域 ----
            // 1) 模块指令域(框架自带)
            var modules = new AppShell.Services.Modules.ModuleHost(
                paths.ModulesDir, log);
            AppShell.Shell.Modules.ModuleCommands.RegisterAll(registry, modules, settings);

            // 2) MCP 聚合入口(框架自带):内部级联 prompt.* 与 command.*
            var prompts = new PromptGovernanceStore(paths.Root, log);
            McpGateway? gateway = null;
            McpCommands.RegisterAll(registry, () => bus, () => gateway, settings, prompts);

            // 3) 应用专有指令域(OneHistoryStudio 的 ConfigureCommands 等价内容)
            var confirm = new Func<string, bool>(_ => true);
            var projects = new ProjectService(settings, confirm, paths.Root);
            var history = new HistoryRecorder(paths.Root, log);
            var branchHistory = new BranchHistoryService(projects);
            var gitRules = new GitFileRuleService(projects);
            var formatInventory = new FormatInventoryService(projects, log, paths.Root);
            var tools = new ToolSyncService(
                projects,
                paths.Root,
                log,
                () => modules.ModulesDirectory,
                () => registry.All().Select(command => command.Name));

            ProjectCommands.RegisterAll(registry, projects, history);
            BranchHistoryCommands.RegisterAll(registry, branchHistory, history);
            GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects);
            ToolCommands.RegisterAll(registry, tools);
            DebugCommands.RegisterAll(registry, log);
            // V2.4.4:不再调用 AppMcpPolicy.RegisterReadonlyCommands()——
            // 只读性已由各 CommandDescriptor.Readonly 自描述，无需按名字补登记。

            // 走到这里没抛,即证明完整装配路径无重复注册。
            var all = registry.All();
            True(all.Count > 0, "wiring: registry non-empty");

            True(all.All(command => McpExposurePolicy.State(command) ==
                                    (McpExposurePolicy.HardExclusionReason(command.Name) != null
                                        ? "hidden"
                                        : command.IsDangerous ? "dangerous"
                                        : command.Readonly ? "readonly"
                                        : "standard")),
                "wiring: MCP state projects the descriptor readonly truth");
            True(ToolManifestLoader.FindUnreservedBuiltinDomains(
                    all.Select(command => command.Name),
                    all.Select(command => command.Name)).Count == 0,
                "wiring: registered builtin domains are covered by module reservations");
            True(ToolManifestLoader.FindUnreservedBuiltinDomains(["future.list"])
                    .SequenceEqual(["future"], StringComparer.OrdinalIgnoreCase),
                "wiring: domain drift self-check detects an unreserved command domain");
            True(new HashSet<string>(AppShell.Services.Modules.ModuleHost.FindModuleDomainConflicts(
                    ["app.exit", "help", "future.list"],
                    ["app.run", "help.run", "future.run", "custom.run"]),
                    StringComparer.OrdinalIgnoreCase)
                    .SetEquals(["app", "future", "help"]),
                "wiring: production module swap blocks dynamic reserved domains");

            var manifestRoot = Path.Combine(paths.Root, "manifest-domain-fixture");
            Directory.CreateDirectory(manifestRoot);
            var manifestPath = Path.Combine(manifestRoot, ToolManifestLoader.FileName);
            File.WriteAllText(manifestPath, "{\"name\":\"future\",\"artifact\":\"module.dll\"}");
            var manifestEntry = ToolManifestLoader.Load(
                manifestPath,
                paths.Root,
                "fixture",
                ["future.list"]);
            True(manifestEntry.Manifest == null
                 && manifestEntry.Error?.Contains("内置指令域冲突", StringComparison.Ordinal) == true,
                "wiring: tool manifest consumes runtime reserved domains");

            // 关键指令都在且唯一(名称唯一由 Register 保证,此处确认存在)
            foreach (var name in new[]
                     {
                         "command.list", "command.show", "command.domains", "command.manual",
                         "mcp.start", "mcp.stop", "mcp.status", "mcp.schema",
                         "prompt.get", "module.list",
                         "proj.list", "proj.commit", "git.rule.list", "tool.list",
                     })
            {
                True(registry.TryGet(name, out _), $"wiring: {name} registered exactly once");
            }

            // 只读性来自描述符自描述，而不是名字白名单。
            foreach (var name in new[] { "proj.list", "git.rule.list", "tool.list" })
            {
                True(registry.TryGet(name, out var readonlyCommand)
                     && readonlyCommand.Readonly
                     && McpExposurePolicy.State(readonlyCommand) == "readonly",
                    $"wiring: {name} is self-described readonly and resolves to readonly");
            }

            // 名字白名单必须保持为空:任何往里补登记的行为都会让「一件事实两处声明」复活。
            // (模块清单的 mcpExposure=readonly 走 ModuleExposure 委托,不进本集合。)
            True(McpExposurePolicy.ReadonlyCommandNames.Count == 0,
                "wiring: name-based readonly whitelist stays empty");
        }
        finally
        {
            log?.Dispose();
            if (Directory.Exists(paths.Root))
                Directory.Delete(paths.Root, recursive: true);
        }
    }
}
