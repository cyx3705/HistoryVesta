namespace AppShell.Core.Data;

/// <summary>
/// SQL 文本片段助手(V2.1.6 R1):单引号转义/引用/截断的唯一实现,
/// HistoryRecorder 与 PromptGovernanceStore 共同取用。
/// 时间戳格式不在此统一——各表既有格式属对外行为,由调用方自持。
/// </summary>
public static class SqlText
{
    public static string Escape(string value) => value.Replace("'", "''");

    public static string Quote(string value) => $"'{Escape(value)}'";

    public static string QuoteNullable(string? value) => value == null ? "NULL" : Quote(value);

    public static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
