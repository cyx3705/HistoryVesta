using AppShell.Core.Data;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;

namespace AppShell.Services.Mcp;

/// <summary>
/// MCP 调用留痕的框架缺省实现(0.4.4):写入 <c>mcp_history</c> 表。
///
/// **表结构、列名、时间戳格式与截断长度逐项沿用反哺来源 OneHistoryStudio 的既有定义**,
/// 派生应用升级到 0.4.4 后既有 mcp_history 数据可直接继续使用,不需要迁移。
/// 留痕失败只告警不阻断——记录是旁路,不是闸口。
/// </summary>
public sealed class McpAuditRecorder : IMcpAuditLog
{
    public const string TableName = "mcp_history";

    private readonly IDataService _data;
    private readonly IShellLog _log;

    public McpAuditRecorder(IDataService data, IShellLog log)
    {
        _data = data;
        _log = log;
        try
        {
            _data.ExecuteSql(
                $"""
                CREATE TABLE IF NOT EXISTS {TableName} (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    time       TEXT    NOT NULL,
                    client     TEXT    NOT NULL,
                    tool       TEXT    NOT NULL,
                    arguments  TEXT,
                    result     TEXT    NOT NULL,
                    elapsed_ms INTEGER NOT NULL DEFAULT 0
                )
                """);
        }
        catch (Exception ex)
        {
            _log.Error("mcp", $"MCP 留痕表初始化失败: {ex.Message}");
        }
    }

    public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs)
    {
        try
        {
            _data.ExecuteSql(
                $"INSERT INTO {TableName} (time, client, tool, arguments, result, elapsed_ms) VALUES (" +
                $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}',{SqlText.Quote(SqlText.Truncate(client, 100))}," +
                $"{SqlText.Quote(tool)},{SqlText.Quote(SqlText.Truncate(arguments, 500))}," +
                $"{SqlText.Quote(result)},{elapsedMs})");
        }
        catch (Exception ex)
        {
            _log.Warn("mcp", $"MCP 留痕写入失败(不影响调用本身): {ex.Message}");
        }
    }
}
