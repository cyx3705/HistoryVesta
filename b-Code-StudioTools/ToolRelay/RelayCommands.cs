using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HistoryVulcan.Core.Modules;

namespace ToolRelay;

public sealed class RelayCommands
{
    private const int MaximumArgumentsBytes = 64 * 1024;

    /// <summary>实时列出 OHS 当前 MCP 策略下可见的工具</summary>
    /// <param name="filter">按工具名或描述包含匹配，空字符串不过滤</param>
    /// <param name="modulesOnly">true 只返回模块工具，false 返回全部可见工具</param>
    [ModuleCommand(Readonly = true, CommandClass = "relay")]
    public async Task<object> List(string filter = "", bool modulesOnly = true)
    {
        using var client = OhsmcpClient.FromSettings();
        var tools = await client.ListToolsAsync().ConfigureAwait(false);

        if (modulesOnly)
        {
            var moduleNames = await client.ListVisibleModuleToolNamesAsync().ConfigureAwait(false);
            tools = tools.Where(tool => moduleNames.Contains(tool.Name)).ToList();
        }

        filter = (filter ?? "").Trim();
        if (filter.Length > 0)
        {
            tools = tools.Where(tool =>
                    tool.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || tool.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var ordered = tools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new { Count = ordered.Count, Tools = ordered };
    }

    /// <summary>实时查看一个当前可见 MCP 工具的描述和 JSON Schema</summary>
    /// <param name="name">MCP 工具名，例如 ProjectPulse_Summary</param>
    [ModuleCommand(Readonly = true, CommandClass = "relay")]
    public async Task<object> Describe(string name)
    {
        name = RequireToolName(name);
        using var client = OhsmcpClient.FromSettings();
        var tools = await client.ListToolsAsync().ConfigureAwait(false);
        return tools.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"当前 MCP 策略下没有可见工具: {name}");
    }

    /// <summary>通过 OHS 最新 MCP 工具目录调用一个工具，不依赖 Codex 当前任务的旧快照</summary>
    /// <param name="name">MCP 工具名，例如 ProjectPulse_Summary</param>
    /// <param name="argumentsJson">JSON 对象字符串，默认空对象，最大 64 KiB</param>
    [ModuleCommand(CommandClass = "relay")]
    public async Task<object> Call(string name, string argumentsJson = "{}")
    {
        name = RequireToolName(name);
        if (name.StartsWith("ToolRelay_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("禁止 ToolRelay 自调用");

        argumentsJson ??= "{}";
        if (Encoding.UTF8.GetByteCount(argumentsJson) > MaximumArgumentsBytes)
            throw new ArgumentException("argumentsJson 超过 64 KiB 上限", nameof(argumentsJson));

        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(argumentsJson) as JsonObject
                        ?? throw new ArgumentException("argumentsJson 必须是单个 JSON 对象", nameof(argumentsJson));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"argumentsJson 不是有效 JSON: {ex.Message}", nameof(argumentsJson));
        }

        using var client = OhsmcpClient.FromSettings();
        var tools = await client.ListToolsAsync().ConfigureAwait(false);
        var target = tools.FirstOrDefault(tool => tool.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            throw new InvalidOperationException($"当前 MCP 策略下没有可见工具: {name}");

        var result = await client.CallToolAsync(target.Name, arguments).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out var errorElement)
            && errorElement.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                $"目标工具 {target.Name} 返回失败: {FirstText(result) ?? "未提供错误文本"}");
        }

        return new { Tool = target.Name, Result = result };
    }

    private static string? FirstText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement)
                && textElement.GetString() is { Length: > 0 } text)
            {
                const int maximumLength = 2048;
                return text.Length <= maximumLength ? text : text[..maximumLength] + "...";
            }
        }

        return null;
    }

    private static string RequireToolName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 64)
            throw new ArgumentException("name 必须是 1~64 字符的 MCP 工具名", nameof(name));
        if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("name 只能包含 ASCII 字母、数字、下划线和连字符", nameof(name));
        return name;
    }
}
