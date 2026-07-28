using System.Diagnostics;
using System.IO;
using System.Text;
using AppShell.Core.Commands;
using AppShell.Core.Data;
using AppShell.Core.Storage;

namespace OneHistoryStudio.Service;

internal static class ServiceBuiltinCommands
{
    private static readonly string[] FrontendCommands =
    [
        "cls",
        "history", "run",
        "app.exit", "app.about", "log.level",
        "panel.list", "panel.show", "panel.set", "panel.reload",
        "win.list", "win.show", "win.hide", "win.float", "win.reset", "win.dock", "win.ratio",
        "win.max", "win.restore",
        "layout.save", "layout.load", "layout.list", "layout.reset",
        "res.root", "res.list", "res.mkdir", "res.rename", "res.delete", "res.open", "res.reveal",
        "conn.status", "conn.role", "conn.endpoint", "conn.pair", "conn.forget", "conn.reconnect",
    ];

    public static void RegisterAll(
        CommandRegistry registry,
        IDataService data,
        ISettingsService settings,
        string dataDirectory)
    {
        RegisterHelp(registry);
        RegisterApp(registry, settings, dataDirectory);
        RegisterData(registry, data, dataDirectory);
        foreach (var name in FrontendCommands)
        {
            registry.Register(new CommandDescriptor
            {
                Name = name,
                Summary = "由已连接的 WPF 前端执行",
                ExecutionSite = CommandExecutionSite.Frontend,
                AllowUnspecifiedParameters = true,
                Readonly = name is "history" or "win.list" or "layout.list" or "panel.list",
                Handler = _ => Task.FromResult(CommandResult.Fail("前端命令未被中继")),
            }, "framework:frontend");
        }
    }

    private static void RegisterHelp(CommandRegistry registry)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "help",
            Summary = "列出全部指令或查看指令详情",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "command", Description = "指令名", Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.GetString("command");
                if (name == null)
                    return CommandResult.Ok("指令清单:" + string.Concat(registry.All().Select(item => $"\n  {item.Name,-24} {item.Summary}")));
                if (!registry.TryGet(name, out var descriptor))
                    return CommandResult.Fail($"未知指令: {name}");
                return CommandResult.Ok(
                    $"{descriptor.Name} - {descriptor.Summary}\n{CommandBus.FormatUsage(descriptor)}" +
                    (descriptor.Example == null ? "" : $"\n示例: {descriptor.Example}"));
            }),
        });
    }

    private static void RegisterApp(
        CommandRegistry registry,
        ISettingsService settings,
        string dataDirectory)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "app.get",
            Summary = "读取设置",
            Readonly = true,
            Parameters = [new ParameterSpec { Name = "key", Description = "设置键", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.GetString("key");
                if (key != null)
                    return CommandResult.Ok($"{key}={settings.Get(key) ?? "(未配置)"}");
                return CommandResult.Ok("设置:" + string.Concat(settings.All().Select(item => $"\n  {item.Key} = {item.Value}")));
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "app.set",
            Summary = "写入设置",
            Parameters =
            [
                new ParameterSpec { Name = "key", Description = "设置键", Required = true, Position = 0 },
                new ParameterSpec { Name = "value", Description = "设置值", Required = true, Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                settings.Set(ctx.RequireString("key"), ctx.RequireString("value"));
                return CommandResult.Ok("设置已保存");
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "app.opendata",
            Summary = "打开应用数据目录",
            Handler = CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true });
                return CommandResult.Ok($"已打开 {dataDirectory}");
            }),
        });
    }

    private static void RegisterData(CommandRegistry registry, IDataService data, string dataDirectory)
    {
        var connection = new ParameterSpec { Name = "conn", Description = "连接名" };
        var table = new ParameterSpec { Name = "table", Description = "表名", Required = true, Position = 0 };

        registry.Register(new CommandDescriptor
        {
            Name = "db.list", Summary = "列出数据库连接", Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                "连接:" + string.Concat(data.ListConnections().Select(name => $"\n  {name}")),
                data.ListConnections())),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "db.tables", Summary = "列出数据表", Readonly = true,
            Parameters = [connection],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tables = data.ListTables(ctx.GetString("conn"));
                return CommandResult.Ok("数据表:" + string.Concat(tables.Select(name => $"\n  {name}")), tables);
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "db.schema", Summary = "查看表结构", Readonly = true,
            Parameters = [table, connection],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var schema = data.GetSchema(ctx.RequireString("table"), ctx.GetString("conn"));
                return CommandResult.Ok($"{ctx.RequireString("table")} 共 {schema.Count} 列", schema);
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "db.query", Summary = "分页查询表数据", Readonly = true,
            Parameters =
            [
                table,
                new ParameterSpec { Name = "where", Description = "SQL 条件" },
                new ParameterSpec { Name = "order", Description = "排序片段" },
                new ParameterSpec { Name = "limit", Description = "每页行数", Type = ParamType.Int, Default = "500" },
                new ParameterSpec { Name = "page", Description = "页码", Type = ParamType.Int, Default = "1" },
                connection,
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var result = data.Query(
                    ctx.RequireString("table"), ctx.GetString("where"), ctx.GetString("order"),
                    ctx.GetInt("limit", 500), ctx.GetInt("page", 1), ctx.GetString("conn"));
                return CommandResult.Ok($"{result.Rows.Count} 行 / 共 {result.TotalRows} 行", result);
            }),
        });
        registry.Register(Mutation("db.insert", "插入数据", table, connection, ctx =>
            data.Insert(ctx.RequireString("table"), ctx.RequireString("set"), ctx.GetString("conn")),
            [new ParameterSpec { Name = "set", Description = "列值表达式", Required = true, Position = 1 }]));
        registry.Register(Mutation("db.update", "更新数据", table, connection, ctx =>
            data.Update(ctx.RequireString("table"), ctx.RequireString("set"), ctx.GetString("where"), ctx.GetString("conn")),
            [
                new ParameterSpec { Name = "set", Description = "列值表达式", Required = true, Position = 1 },
                new ParameterSpec { Name = "where", Description = "SQL 条件" },
            ], ctx => $"确认更新 {ctx.RequireString("table")} {(ctx.GetString("where") == null ? "整表" : "匹配行")}？"));
        registry.Register(Mutation("db.delete", "删除数据", table, connection, ctx =>
            data.Delete(ctx.RequireString("table"), ctx.GetString("where"), ctx.GetString("conn")),
            [new ParameterSpec { Name = "where", Description = "SQL 条件" }],
            ctx => $"确认删除 {ctx.RequireString("table")} {(ctx.GetString("where") == null ? "整表" : "匹配行")}？"));
        registry.Register(new CommandDescriptor
        {
            Name = "db.export", Summary = "导出 CSV", Parameters =
            [
                table,
                new ParameterSpec { Name = "file", Description = "输出文件", Required = true, Position = 1 },
                new ParameterSpec { Name = "where", Description = "SQL 条件" },
                connection,
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var raw = ctx.RequireString("file");
                var file = Path.IsPathRooted(raw) ? raw : Path.Combine(dataDirectory, raw);
                var count = data.ExportCsv(ctx.RequireString("table"), file, ctx.GetString("where"), ctx.GetString("conn"));
                return CommandResult.Ok($"已导出 {count} 行到 {file}", count);
            }),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "db.sql", Summary = "执行 SQL", Parameters =
            [
                new ParameterSpec { Name = "sql", Description = "完整 SQL", Required = true, Position = 0 },
                connection,
            ],
            ConfirmPrompt = ctx => $"SQL 直通绕过保护,确认执行？\n\n{ctx.RequireString("sql")}",
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var (result, affected) = data.ExecuteSql(ctx.RequireString("sql"), ctx.GetString("conn"));
                return result == null
                    ? CommandResult.Ok($"影响 {affected} 行", affected)
                    : CommandResult.Ok($"返回 {result.Rows.Count} 行", result);
            }),
        });
    }

    private static CommandDescriptor Mutation(
        string name,
        string summary,
        ParameterSpec table,
        ParameterSpec connection,
        Func<CommandContext, int> execute,
        IReadOnlyList<ParameterSpec> parameters,
        Func<CommandContext, string?>? confirm = null)
        => new()
        {
            Name = name,
            Summary = summary,
            Parameters = [table, .. parameters, connection],
            ConfirmPrompt = confirm,
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var affected = execute(ctx);
                return CommandResult.Ok($"影响 {affected} 行", affected);
            }),
        };
}
