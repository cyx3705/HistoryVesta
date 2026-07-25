using System.Text;
using System.Text.RegularExpressions;
using AppShell.Core.Data;
using Microsoft.Data.Sqlite;

namespace AppShell.Services;

/// <summary>
/// SQLite 数据服务(D-01,Q4 已定):嵌入式、零安装、零配置。
/// 库文件缺省位于 &lt;应用数据目录&gt;/data/ 下(D-04);
/// 支持多命名连接(D-03),缺省连接名 "main"。
/// 行定位:查询附带 rowid(别名 __rowid__),编辑/删除以 rowid 拼 where,
/// 无主键表同样可编辑;WITHOUT ROWID 表自动退化为只读结果。
/// </summary>
public sealed partial class SqliteDataService : IDataService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppPaths _paths;

    public SqliteDataService(AppPaths paths) => _paths = paths;

    public event Action<string, string?>? DataChanged;

    /// <summary>
    /// 注册命名连接(D-03)。dbFile 为相对名(落到 data/ 下)或绝对路径;
    /// 文件不存在时首次使用自动创建。
    /// </summary>
    public void RegisterConnection(string name, string dbFile)
    {
        var path = Path.IsPathRooted(dbFile) ? dbFile : Path.Combine(_paths.DataDir, dbFile);
        lock (_gate)
        {
            _connections[name] = path;
        }
    }

    public IReadOnlyList<string> ListConnections()
    {
        lock (_gate)
        {
            return _connections.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<string> ListTables(string? connection = null)
    {
        using var conn = Open(connection);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type IN ('table','view') " +
            "AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }

    public IReadOnlyList<ColumnInfo> GetSchema(string table, string? connection = null)
    {
        using var conn = Open(connection);
        ValidateTable(conn, table);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        var list = new List<ColumnInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ColumnInfo(
                Name: reader.GetString(1),
                Type: reader.IsDBNull(2) ? "" : reader.GetString(2),
                IsPrimaryKey: reader.GetInt32(5) > 0,
                NotNull: reader.GetInt32(3) != 0));
        }

        return list;
    }

    public QueryResult Query(
        string table,
        string? where = null,
        string? order = null,
        int limit = 500,
        int page = 1,
        string? connection = null)
    {
        limit = Math.Clamp(limit, 1, 10_000);
        page = Math.Max(1, page);

        using var conn = Open(connection);
        ValidateTable(conn, table);

        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}";
        var orderSql = string.IsNullOrWhiteSpace(order) ? "" : $" ORDER BY {order}";

        long total;
        using (var count = conn.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM {Quote(table)}{whereSql}";
            total = (long)count.ExecuteScalar()!;
        }

        // 常规表带 rowid 供行定位;WITHOUT ROWID 表退化为无定位列(只读)
        try
        {
            return ReadPage(conn,
                $"SELECT rowid AS __rowid__, * FROM {Quote(table)}{whereSql}{orderSql}",
                total, limit, page);
        }
        catch (SqliteException)
        {
            return ReadPage(conn,
                $"SELECT * FROM {Quote(table)}{whereSql}{orderSql}",
                total, limit, page);
        }
    }

    private static QueryResult ReadPage(SqliteConnection conn, string sql, long total, int limit, int page)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{sql} LIMIT {limit} OFFSET {(long)(page - 1) * limit}";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(reader.GetName(i));

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new QueryResult(columns, rows, total, page, limit);
    }

    public int Insert(string table, string set, string? connection = null)
    {
        var pairs = SplitAssignments(set);
        if (pairs.Count == 0)
            throw new InvalidOperationException("set= 不能为空,形如 set=\"name='张三', age=30\"");

        var cols = string.Join(", ", pairs.Select(p => Quote(p.Column)));
        var vals = string.Join(", ", pairs.Select(p => p.ValueSql));

        var affected = Execute(connection, table,
            conn => $"INSERT INTO {Quote(table)} ({cols}) VALUES ({vals})");
        return affected;
    }

    public int Update(string table, string set, string? where, string? connection = null)
    {
        if (string.IsNullOrWhiteSpace(set))
            throw new InvalidOperationException("set= 不能为空");
        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}";
        return Execute(connection, table,
            _ => $"UPDATE {Quote(table)} SET {set}{whereSql}");
    }

    public int Delete(string table, string? where, string? connection = null)
    {
        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}";
        return Execute(connection, table,
            _ => $"DELETE FROM {Quote(table)}{whereSql}");
    }

    private int Execute(string? connection, string table, Func<SqliteConnection, string> sqlFactory)
    {
        int affected;
        using (var conn = Open(connection))
        {
            ValidateTable(conn, table);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sqlFactory(conn);
            affected = cmd.ExecuteNonQuery();
        }

        DataChanged?.Invoke(ResolveName(connection), table);
        return affected;
    }

    public long ExportCsv(string table, string filePath, string? where = null, string? connection = null)
    {
        using var conn = Open(connection);
        ValidateTable(conn, table);

        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {Quote(table)}{whereSql}";
        using var reader = cmd.ExecuteReader();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(true)); // BOM,Excel 友好

        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = reader.GetName(i);
        writer.WriteLine(string.Join(",", headers.Select(CsvField)));

        long rows = 0;
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            writer.WriteLine(string.Join(",", cells.Select(CsvField)));
            rows++;
        }

        return rows;
    }

    public (QueryResult? Result, int Affected) ExecuteSql(string sql, string? connection = null)
    {
        var trimmed = sql.TrimStart();
        var isQuery = trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);

        using var conn = Open(connection);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (isQuery)
        {
            using var reader = cmd.ExecuteReader();
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            const int cap = 5000; // 直通查询防失控上限
            var rows = new List<object?[]>();
            while (rows.Count < cap && reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return (new QueryResult(columns, rows, rows.Count, 1, cap), 0);
        }

        var affected = cmd.ExecuteNonQuery();

        // 尽力解析目标表名,通知表窗口刷新
        var match = SqlTargetTable().Match(trimmed);
        DataChanged?.Invoke(ResolveName(connection), match.Success ? match.Groups[1].Value : null);
        return (null, affected);
    }

    [GeneratedRegex("""^(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM|REPLACE\s+INTO)\s+["'`\[]?(\w+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlTargetTable();

    // ---------------------------------------------------------------- 内部

    private string ResolveName(string? connection)
        => string.IsNullOrWhiteSpace(connection) ? IDataService.DefaultConnection : connection;

    private SqliteConnection Open(string? connection)
    {
        var name = ResolveName(connection);
        string path;
        lock (_gate)
        {
            if (!_connections.TryGetValue(name, out path!))
            {
                var known = string.Join(" / ", _connections.Keys);
                throw new InvalidOperationException(
                    $"没有名为 {name} 的连接。已注册: {(known.Length > 0 ? known : "(无)")}");
            }
        }

        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        return conn;
    }

    private static void ValidateTable(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        if ((long)cmd.ExecuteScalar()! == 0)
            throw new InvalidOperationException($"表不存在: {table}(db.tables 可查)");
    }

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string CsvField(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private readonly record struct Assignment(string Column, string ValueSql);

    /// <summary>
    /// 把 "name='张三, 先生', age=30" 拆成 (列, 值SQL片段) 列表:
    /// 顶层逗号分隔,单引号字符串('' 转义)与括号内的逗号不拆。
    /// </summary>
    private static List<Assignment> SplitAssignments(string set)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        var inString = false;
        var depth = 0;

        for (var i = 0; i < set.Length; i++)
        {
            var c = set[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    if (i + 1 < set.Length && set[i + 1] == '\'')
                    {
                        sb.Append(set[++i]); // '' 转义
                    }
                    else
                    {
                        inString = false;
                    }
                }
            }
            else if (c == '\'')
            {
                inString = true;
                sb.Append(c);
            }
            else if (c == '(')
            {
                depth++;
                sb.Append(c);
            }
            else if (c == ')')
            {
                depth--;
                sb.Append(c);
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
            parts.Add(sb.ToString());

        var result = new List<Assignment>();
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException($"set= 片段不是 列=值 形式: {part.Trim()}");
            result.Add(new Assignment(part[..eq].Trim(), part[(eq + 1)..].Trim()));
        }

        return result;
    }
}
