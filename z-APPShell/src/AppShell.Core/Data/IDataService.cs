namespace AppShell.Core.Data;

/// <summary>一列的结构信息(T-07 表结构查看)。</summary>
public sealed record ColumnInfo(
    string Name,
    string Type,
    bool IsPrimaryKey,
    bool NotNull);

/// <summary>
/// 一次分页查询的结果(db.query / 表窗口共用)。
/// Rows 的每行与 Columns 一一对应;首列约定为行定位符
/// (SQLite 为 rowid,别名 __rowid__),UI 隐藏、编辑指令用它拼 where。
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    long TotalRows,
    int Page,
    int PageSize)
{
    public int TotalPages => PageSize <= 0
        ? 1
        : (int)Math.Max(1, (TotalRows + PageSize - 1) / PageSize);
}

/// <summary>
/// 数据服务门面(§6.1):表窗口与 db.* 指令的公共实现层。
/// D-01 默认实现为嵌入式 SQLite;D-02 本接口即多数据库接入位
/// (MySQL / SQL Server / PostgreSQL 实现后续按连接注册)。
/// D-03 多命名连接,缺省 "main"。
/// where / order / set 参数为原始 SQL 片段(§5.1 示例语义),
/// 面向使用者自己的数据库,不做注入防护;表名会做存在性校验。
/// </summary>
public interface IDataService
{
    /// <summary>缺省连接名。</summary>
    const string DefaultConnection = "main";

    /// <summary>全部命名连接(db.list)。</summary>
    IReadOnlyList<string> ListConnections();

    /// <summary>列出连接内全部表(db.tables)。</summary>
    IReadOnlyList<string> ListTables(string? connection = null);

    /// <summary>表结构(db.schema / T-07)。</summary>
    IReadOnlyList<ColumnInfo> GetSchema(string table, string? connection = null);

    /// <summary>
    /// 分页查询(db.query / T-03)。page 从 1 起;limit 为每页行数。
    /// 结果首列为行定位符 __rowid__。
    /// </summary>
    QueryResult Query(
        string table,
        string? where = null,
        string? order = null,
        int limit = 500,
        int page = 1,
        string? connection = null);

    /// <summary>插入一行(db.insert);set 形如 "name='张三', age=30"。返回影响行数。</summary>
    int Insert(string table, string set, string? connection = null);

    /// <summary>更新(db.update);where 为 null 时更新整表(调用方负责二次确认,T-08)。</summary>
    int Update(string table, string set, string? where, string? connection = null);

    /// <summary>删除(db.delete);where 为 null 时删除整表(调用方负责二次确认,T-08)。</summary>
    int Delete(string table, string? where, string? connection = null);

    /// <summary>导出查询结果为 CSV 文件(db.export / T-09),返回导出行数。</summary>
    long ExportCsv(string table, string filePath, string? where = null, string? connection = null);

    /// <summary>SQL 直通(db.sql / T-10,P2):SELECT 返回结果集,否则返回影响行数、结果为 null。</summary>
    (QueryResult? Result, int Affected) ExecuteSql(string sql, string? connection = null);

    /// <summary>
    /// 数据变更通知(Insert/Update/Delete/ExecuteSql 非查询成功后触发):
    /// 表窗口订阅并在正显示该表时自动重载(验收 4 的“手输指令 → 表窗口同步”)。
    /// 参数:(连接名, 表名;db.sql 无法解析表名时为 null)。
    /// </summary>
    event Action<string, string?>? DataChanged;
}
