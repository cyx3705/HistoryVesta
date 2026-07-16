using AppShell.Core.Data;
using AppShell.Core.Logging;

namespace OneHistoryStudio.Git;

/// <summary>
/// 操作留痕(DT-01)与分支描述(DT-02):
/// push_history 表记录每次 proj.* 变更类操作(时间/分支/动作/消息/结果/警告数),
/// branch_notes 表存分支 → 项目描述(proj.note 写入,继承树优先取用)。
/// 留痕失败只告警不阻断主操作(记录是旁路,不是闸口)。
/// </summary>
public sealed class HistoryRecorder
{
    private readonly IDataService _data;
    private readonly IShellLog _log;

    public HistoryRecorder(IDataService data, IShellLog log)
    {
        _data = data;
        _log = log;
        try
        {
            _data.ExecuteSql(
                """
                CREATE TABLE IF NOT EXISTS push_history (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    time     TEXT    NOT NULL,
                    branch   TEXT    NOT NULL,
                    action   TEXT    NOT NULL,
                    message  TEXT,
                    result   TEXT    NOT NULL,
                    warnings INTEGER NOT NULL DEFAULT 0
                )
                """);
            _data.ExecuteSql(
                """
                CREATE TABLE IF NOT EXISTS branch_notes (
                    branch  TEXT PRIMARY KEY,
                    note    TEXT NOT NULL,
                    updated TEXT NOT NULL
                )
                """);
            _data.ExecuteSql(
                """
                CREATE TABLE IF NOT EXISTS mcp_descriptions (
                    command     TEXT PRIMARY KEY,
                    description TEXT NOT NULL,
                    updated     TEXT NOT NULL
                )
                """);
            _data.ExecuteSql(
                """
                CREATE TABLE IF NOT EXISTS mcp_history (
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
            _log.Error("history", $"留痕表初始化失败: {ex.Message}");
        }
    }

    /// <summary>写一条操作记录;action ∈ create/delete/commit/push/commitall/pushall/repair。</summary>
    public void Record(string branch, string action, string message, string result, int warnings = 0)
    {
        try
        {
            _data.ExecuteSql(
                $"INSERT INTO push_history (time, branch, action, message, result, warnings) VALUES (" +
                $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}','{Esc(branch)}','{Esc(action)}'," +
                $"'{Esc(Truncate(message, 500))}','{Esc(result)}',{warnings})");
        }
        catch (Exception ex)
        {
            _log.Warn("history", $"留痕写入失败(不影响操作本身): {ex.Message}");
        }
    }

    /// <summary>MCP 调用留痕(MO-01/MS-05);result ∈ 成功/失败/拒绝/超时。失败只告警不阻断。</summary>
    public void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs)
    {
        try
        {
            _data.ExecuteSql(
                $"INSERT INTO mcp_history (time, client, tool, arguments, result, elapsed_ms) VALUES (" +
                $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}','{Esc(Truncate(client, 100))}','{Esc(tool)}'," +
                $"'{Esc(Truncate(arguments, 500))}','{Esc(result)}',{elapsedMs})");
        }
        catch (Exception ex)
        {
            _log.Warn("history", $"MCP 留痕写入失败(不影响调用本身): {ex.Message}");
        }
    }

    /// <summary>MCP 工具提示词覆盖(V2.1.1,mcp.desc):写/更新。</summary>
    public void SetMcpDescription(string command, string text)
        => _data.ExecuteSql(
            $"INSERT OR REPLACE INTO mcp_descriptions (command, description, updated) VALUES (" +
            $"'{Esc(command)}','{Esc(text)}','{DateTime.Now:yyyy-MM-dd HH:mm:ss}')");

    /// <summary>MCP 工具提示词覆盖:清除(恢复指令自带 Summary)。</summary>
    public void DeleteMcpDescription(string command)
        => _data.ExecuteSql($"DELETE FROM mcp_descriptions WHERE command='{Esc(command)}'");

    /// <summary>全部提示词覆盖(导出层读取侧合成用);失败返回空表。</summary>
    public IReadOnlyDictionary<string, string> AllMcpDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (result, _) = _data.ExecuteSql("SELECT command, description FROM mcp_descriptions");
            if (result == null)
                return map;
            var ci = IndexOf(result.Columns, "command");
            var di = IndexOf(result.Columns, "description");
            if (ci < 0 || di < 0)
                return map;
            foreach (var row in result.Rows)
            {
                if (row[ci] is string c && row[di] is string d && c.Length > 0)
                    map[c] = d;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("history", $"读取 MCP 提示词覆盖失败: {ex.Message}");
        }

        return map;
    }

    /// <summary>写/更新分支描述(proj.note)。</summary>
    public void SetNote(string branch, string note)
        => _data.ExecuteSql(
            $"INSERT OR REPLACE INTO branch_notes (branch, note, updated) VALUES (" +
            $"'{Esc(branch)}','{Esc(note)}','{DateTime.Now:yyyy-MM-dd HH:mm:ss}')");

    /// <summary>全部分支描述(继承树覆盖显示用);失败返回空表。</summary>
    public IReadOnlyDictionary<string, string> AllNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (result, _) = _data.ExecuteSql("SELECT branch, note FROM branch_notes");
            if (result == null)
                return notes;
            var bi = IndexOf(result.Columns, "branch");
            var ni = IndexOf(result.Columns, "note");
            if (bi < 0 || ni < 0)
                return notes;
            foreach (var row in result.Rows)
            {
                if (row[bi] is string b && row[ni] is string n && b.Length > 0)
                    notes[b] = n;
            }
        }
        catch (Exception ex)
        {
            _log.Warn("history", $"读取分支描述失败: {ex.Message}");
        }

        return notes;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
