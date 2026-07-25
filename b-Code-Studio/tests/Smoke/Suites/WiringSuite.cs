using AppShell.Core.Commands;
using AppShell.Core.Mcp;
using AppShell.Services;
using AppShell.Services.Mcp;
using AppShell.Shell.Mcp;
using Microsoft.Data.Sqlite;
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
        Environment.CurrentDirectory = RepoRoot;

        var testName = $"OneHistoryStudio.Wiring.{Guid.NewGuid():N}";
        var paths = new AppPaths(testName);
        ShellLog? log = null;
        try
        {
            log = new ShellLog(paths);
            var settings = new SettingsService(paths);
            var data = new SqliteDataService(paths);
            data.RegisterConnection("main", "main.db");

            var registry = new CommandRegistry();
            var bus = new CommandBus(registry, log);

            // ---- 按 ShellWindow 的真实装配次序,在同一 registry 上注册全部指令域 ----
            // 1) 模块指令域(框架自带)
            var modules = new AppShell.Services.Modules.ModuleHost(
                System.IO.Path.Combine(paths.Root, "Modules"), log);
            AppShell.Shell.Modules.ModuleCommands.RegisterAll(registry, modules, settings);

            // 2) MCP 聚合入口(框架自带):内部级联 prompt.* 与 command.*
            var prompts = new PromptGovernanceStore(data, log);
            McpGateway? gateway = null;
            McpCommands.RegisterAll(registry, () => bus, () => gateway, settings, prompts);

            // 3) 应用专有指令域(OneHistoryStudio 的 ConfigureCommands 等价内容)
            var confirm = new Func<string, bool>(_ => true);
            var projects = new ProjectService(settings, confirm, paths.Root);
            var history = new HistoryRecorder(data, log);
            var branchHistory = new BranchHistoryService(projects);
            var gitRules = new GitFileRuleService(projects);
            var formatInventory = new FormatInventoryService(projects, log, paths.Root);
            var tools = new ToolSyncService(projects, data, log, () => modules.ModulesDirectory);

            ProjectCommands.RegisterAll(registry, projects, history);
            BranchHistoryCommands.RegisterAll(registry, branchHistory, history);
            GitRuleCommands.RegisterAll(registry, gitRules, formatInventory, projects);
            ToolCommands.RegisterAll(registry, tools);
            DebugCommands.RegisterAll(registry, log);
            OneHistoryStudio.AppMcpPolicy.RegisterReadonlyCommands();

            // 走到这里没抛,即证明完整装配路径无重复注册。
            var all = registry.All();
            True(all.Count > 0, "wiring: registry non-empty");

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

            // 应用只读指令确已登记进框架策略
            True(McpExposurePolicy.IsReadonlyAllowed("proj.list")
                 && McpExposurePolicy.IsReadonlyAllowed("git.rule.list"),
                "wiring: app readonly commands registered into framework policy");

            Console.WriteLine("WiringSmoke: PASS");
        }
        finally
        {
            log?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root))
                Directory.Delete(paths.Root, recursive: true);
        }
    }
}
