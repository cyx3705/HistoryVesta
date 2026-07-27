using AppShell.Core.Commands;
using AppShell.Core.Data;

namespace AppShell.Services.Web;

/// <summary>把同步 IDataService 门面映射到后台服务的 db.* 命令。</summary>
public sealed class RemoteDataService : IDataService
{
    private readonly ShellServiceClient _client;

    public RemoteDataService(ShellServiceClient client) => _client = client;

    public event Action<string, string?>? DataChanged;

    public void NotifyDataChanged(string? connection, string? table)
        => DataChanged?.Invoke(connection ?? IDataService.DefaultConnection, table);

    public IReadOnlyList<string> ListConnections()
        => Data<List<string>>(Execute("db.list"));

    public IReadOnlyList<string> ListTables(string? connection = null)
        => Data<List<string>>(Execute(Command("db.tables", ("conn", connection))));

    public IReadOnlyList<ColumnInfo> GetSchema(string table, string? connection = null)
        => Data<List<ColumnInfo>>(Execute(Command("db.schema", ("table", table), ("conn", connection))));

    public QueryResult Query(
        string table,
        string? where = null,
        string? order = null,
        int limit = 500,
        int page = 1,
        string? connection = null)
        => Data<QueryResult>(Execute(Command(
            "db.query",
            ("table", table),
            ("where", where),
            ("order", order),
            ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("page", page.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("conn", connection))));

    public int Insert(string table, string set, string? connection = null)
        => Mutation(Command("db.insert", ("table", table), ("set", set), ("conn", connection)), connection, table);

    public int Update(string table, string set, string? where, string? connection = null)
        => Mutation(Command(
            "db.update", ("table", table), ("set", set), ("where", where), ("conn", connection)), connection, table);

    public int Delete(string table, string? where, string? connection = null)
        => Mutation(Command("db.delete", ("table", table), ("where", where), ("conn", connection)), connection, table);

    public long ExportCsv(string table, string filePath, string? where = null, string? connection = null)
    {
        var result = Execute(Command(
            "db.export", ("table", table), ("file", filePath), ("where", where), ("conn", connection)));
        return Convert.ToInt64(result.Data ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    }

    public (QueryResult? Result, int Affected) ExecuteSql(string sql, string? connection = null)
    {
        var result = Execute(Command("db.sql", ("sql", sql), ("conn", connection)));
        if (result.Data is QueryResult query)
            return (query, 0);
        var affected = Convert.ToInt32(result.Data ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        NotifyDataChanged(connection, null);
        return (null, affected);
    }

    private int Mutation(string command, string? connection, string table)
    {
        var result = Execute(command);
        var affected = Convert.ToInt32(result.Data ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        NotifyDataChanged(connection, table);
        return affected;
    }

    private CommandResult Execute(string command)
    {
        var result = _client.ExecuteAsync(command, "Shell:Data").GetAwaiter().GetResult();
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    private static T Data<T>(CommandResult result)
        => result.Data is T value
            ? value
            : throw new InvalidOperationException($"远程数据类型不匹配: 期望 {typeof(T).Name}");

    private static string Command(string name, params (string Name, string? Value)[] parameters)
    {
        var parts = new List<string> { name };
        foreach (var (parameterName, value) in parameters)
        {
            if (value != null)
                parts.Add($"{parameterName}={CommandParser.QuoteArg(value)}");
        }
        return string.Join(' ', parts);
    }
}
