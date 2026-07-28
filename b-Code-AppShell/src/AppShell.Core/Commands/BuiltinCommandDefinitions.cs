using AppShell.Core.Data;

namespace AppShell.Core.Commands;

/// <summary>
/// Shared metadata for commands that execute locally in both the desktop shell and a service host.
/// Hosts bind only their execution handler and UI-thread capability.
/// </summary>
public static class BuiltinCommandDefinitions
{
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = CreateDefinitions();

    public static IReadOnlyList<string> Names { get; } = Definitions.Keys.ToArray();

    public static bool Contains(string name) => Definitions.ContainsKey(name);

    public static CommandDescriptor Bind(
        string name,
        Func<CommandContext, Task<CommandResult>> handler,
        bool requiresUiThread = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Definitions.TryGetValue(name, out var definition))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown shared built-in command");

        return new CommandDescriptor
        {
            Name = definition.Name,
            Summary = definition.Summary,
            Example = definition.Example,
            Parameters = definition.Parameters.Select(Clone).ToList(),
            ConfirmPrompt = definition.ConfirmPrompt,
            SupportsUndo = definition.SupportsUndo,
            Dangerous = definition.Dangerous,
            Readonly = definition.Readonly,
            RequiresUiThread = requiresUiThread,
            Handler = handler,
        };
    }

    private static IReadOnlyDictionary<string, Definition> CreateDefinitions()
    {
        var definitions = new[]
        {
            new Definition(
                "help",
                "列出全部指令 / 显示某指令详情与示例",
                "help win.dock",
                [Parameter("command", "指令名;省略时列出全部指令", position: 0)],
                Readonly: true),
            new Definition(
                "app.get",
                "读应用配置项;不带参数列出全部",
                "app.get key=console.history",
                [Parameter("key", "配置键;省略列出全部", position: 0)],
                Readonly: true),
            new Definition(
                "app.set",
                "写应用配置项",
                "app.set key=console.history value=1000",
                [
                    Parameter("key", "配置键", required: true, position: 0),
                    Parameter("value", "配置值", required: true, position: 1),
                ]),
            new Definition(
                "app.opendata",
                "在系统资源管理器中打开应用数据目录"),
            new Definition(
                "db.list",
                "列出全部命名数据库连接",
                Readonly: true),
            new Definition(
                "db.tables",
                "列出连接内全部表",
                "db.tables",
                [Parameter("conn", $"连接名,缺省 {IDataService.DefaultConnection}", position: 0)],
                Readonly: true),
            new Definition(
                "db.schema",
                "查看表结构(字段名 / 类型 / 主键 / 非空)",
                "db.schema table=users",
                [TableParameter(), ConnectionParameter()],
                Readonly: true),
            new Definition(
                "db.query",
                "查询表数据(表窗口同步显示结果)",
                "db.query table=users where=\"age>30 and city='北京'\" limit=100",
                [
                    TableParameter(),
                    Parameter("where", "SQL 条件片段(原样拼接)"),
                    Parameter("order", "SQL 排序片段,如 \"age desc\""),
                    Parameter("limit", "每页行数", ParamType.Int, defaultValue: "500"),
                    Parameter("page", "页码(1 起)", ParamType.Int, defaultValue: "1"),
                    ConnectionParameter(),
                ],
                Readonly: true),
            new Definition(
                "db.insert",
                "插入一行",
                "db.insert table=users set=\"name='张三', age=30\"",
                [
                    TableParameter(),
                    Parameter("set", "列=值 列表,逗号分隔,字符串用单引号", required: true),
                    ConnectionParameter(),
                ]),
            new Definition(
                "db.update",
                "更新行(无 where 将更新整表,需二次确认)",
                "db.update table=users set=\"vip=1\" where=\"id=1032\"",
                [
                    TableParameter(),
                    Parameter("set", "列=值 列表,逗号分隔", required: true),
                    Parameter("where", "SQL 条件;省略 = 整表更新(危险)"),
                    ConnectionParameter(),
                ],
                ConfirmPrompt: ConfirmWholeTableUpdate),
            new Definition(
                "db.delete",
                "删除行(无 where 将清空整表,需二次确认)",
                "db.delete table=users where=\"id=1032\"",
                [
                    TableParameter(),
                    Parameter("where", "SQL 条件;省略 = 清空整表(危险)"),
                    ConnectionParameter(),
                ],
                ConfirmPrompt: ConfirmWholeTableDelete),
            new Definition(
                "db.export",
                "导出查询结果为 CSV 文件",
                "db.export table=users file=用户.csv where=\"vip=1\"",
                [
                    TableParameter(),
                    Parameter("file", "目标文件;相对路径落到工作区目录", required: true),
                    Parameter("where", "SQL 条件片段"),
                    ConnectionParameter(),
                ]),
            new Definition(
                "db.sql",
                "SQL 直通(高级用户;执行前有风险确认)",
                "db.sql \"SELECT city, COUNT(*) FROM users GROUP BY city\"",
                [
                    Parameter("sql", "完整 SQL 语句", required: true, position: 0),
                    ConnectionParameter(),
                ],
                ConfirmPrompt: ConfirmSql),
        };

        return definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static string? ConfirmWholeTableUpdate(CommandContext context)
        => context.GetString("where") == null
            ? $"db.update 未指定 where,将更新表 {context.GetString("table")} 的全部行!确认继续?"
            : null;

    private static string? ConfirmWholeTableDelete(CommandContext context)
        => context.GetString("where") == null
            ? $"db.delete 未指定 where,将删除表 {context.GetString("table")} 的全部行!确认继续?"
            : null;

    private static string ConfirmSql(CommandContext context)
        => $"SQL 直通绕过一切保护,确认执行?\n\n{context.RequireString("sql")}";

    private static ParameterSpec TableParameter()
        => Parameter("table", "表名(db.tables 可查)", required: true, position: 0);

    private static ParameterSpec ConnectionParameter()
        => Parameter("conn", $"连接名,缺省 {IDataService.DefaultConnection}(db.list 可查)");

    private static ParameterSpec Parameter(
        string name,
        string description,
        ParamType type = ParamType.String,
        bool required = false,
        string? defaultValue = null,
        int? position = null)
        => new()
        {
            Name = name,
            Description = description,
            Type = type,
            Required = required,
            Default = defaultValue,
            Position = position,
        };

    private static ParameterSpec Clone(ParameterSpec source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        Type = source.Type,
        Required = source.Required,
        Default = source.Default,
        Position = source.Position,
        AllowedValues = source.AllowedValues?.ToArray(),
    };

    private sealed record Definition(
        string Name,
        string Summary,
        string? Example = null,
        IReadOnlyList<ParameterSpec>? ParameterList = null,
        bool Readonly = false,
        bool SupportsUndo = false,
        bool Dangerous = false,
        Func<CommandContext, string?>? ConfirmPrompt = null)
    {
        public IReadOnlyList<ParameterSpec> Parameters { get; } = ParameterList ?? [];
    }
}
