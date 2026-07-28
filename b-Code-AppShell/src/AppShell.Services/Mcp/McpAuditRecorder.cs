using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AppShell.Core.Data;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;

namespace AppShell.Services.Mcp;

/// <summary>MCP 调用留痕的 JSONL 实现。单条追加失败只告警，不阻断调用。</summary>
public sealed class McpAuditRecorder : IMcpAuditLog
{
    public const string TableName = "mcp_history";

    private readonly string _path;
    private readonly IShellLog _log;
    private readonly object _gate = new();

    public McpAuditRecorder(string dataDirectory, IShellLog log)
    {
        _path = Path.Combine(dataDirectory, "state", "mcp-history.jsonl");
        _log = log;
    }

    /// <summary>源代码兼容入口；新装配点应传数据目录。</summary>
    [Obsolete("Use McpAuditRecorder(string dataDirectory, IShellLog log).")]
    public McpAuditRecorder(IDataService data, IShellLog log)
        : this(Path.Combine(Path.GetTempPath(), "AppShell.McpAudit",
            RuntimeHelpers.GetHashCode(data).ToString("x")), log)
    {
    }

    public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs)
    {
        try
        {
            var row = new AuditRow(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                SqlText.Truncate(client, 100),
                tool,
                SqlText.Truncate(arguments, 500),
                result,
                elapsedMs);
            var line = JsonSerializer.Serialize(row) + Environment.NewLine;
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("mcp", $"MCP 留痕写入失败(不影响调用本身): {ex.Message}");
        }
    }

    private sealed record AuditRow(
        string Time,
        string Client,
        string Tool,
        string Arguments,
        string Result,
        long ElapsedMs);
}
