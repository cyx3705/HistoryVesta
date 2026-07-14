using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyApiLite;

/// <summary>
/// 极简 MCP 服务端（Streamable HTTP 传输，POST /mcp，无状态、无会话）。
/// 手写 JSON-RPC 2.0 分发，不依赖任何 SDK 包。
/// 工具集 = 当前快照的全部动态端点，描述来自模块 XML 注释，随 DLL 热重载实时变化。
/// </summary>
internal static class Mcp
{
    private const string DefaultProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Map(WebApplication app, ModuleHost host)
    {
        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(ctx.Request.Body); }
            catch { return RpcError(null, -32700, "Parse error: 请求体不是合法 JSON"); }

            using (doc)
            {
                var root = doc.RootElement;
                string? method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;

                // 通知（无 id）不需要应答，返回 202
                if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return Results.StatusCode(StatusCodes.Status202Accepted);

                object? id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64() : idEl.GetString();
                root.TryGetProperty("params", out var prms);

                try
                {
                    object result = method switch
                    {
                        "initialize" => Initialize(prms),
                        "ping" => new { },
                        "tools/list" => ToolsList(host),
                        "tools/call" => await ToolsCallAsync(host, prms),
                        _ => throw new McpError(-32601, $"Method not found: {method}")
                    };
                    return Results.Json(new { jsonrpc = "2.0", id, result }, JsonOut);
                }
                catch (McpError e) { return RpcError(id, e.Code, e.Message); }
                catch (Exception e) { return RpcError(id, -32603, $"Internal error: {e.Message}"); }
            }
        });

        // 无状态实现：不提供服务端主动推送流，客户端会自动降级为纯 POST 模式
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status200OK));
    }

    private static object Initialize(JsonElement prms)
    {
        string requested = prms.ValueKind == JsonValueKind.Object
                           && prms.TryGetProperty("protocolVersion", out var v)
                           && v.GetString() is { } s ? s : DefaultProtocolVersion;

        return new
        {
            protocolVersion = SupportedVersions.Contains(requested) ? requested : DefaultProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "myapi-lite", title = "MyAPI Lite 模块托管机", version = "1.0.0" },
            instructions = "工具集来自 Modules 目录中热加载的 DLL 模块，DLL 增删改后工具集会随之变化，" +
                           "必要时重新调用 tools/list 获取最新列表。每个工具对应模块里的一个公共方法。"
        };
    }

    private static object ToolsList(ModuleHost host)
    {
        var snap = host.Current;
        var tools = snap.Entries.Select(e => new
        {
            name = e.Info.McpTool,
            description = $"{e.Info.Description}（模块 {e.Info.Module}，HTTP 等价调用 {e.Info.FullPath}）",
            inputSchema = BuildSchema(e.Method, e.ParamDocs)
        });
        return new { tools };
    }

    private static async Task<object> ToolsCallAsync(ModuleHost host, JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object || !prms.TryGetProperty("name", out var nameEl)
            || nameEl.GetString() is not { Length: > 0 } toolName)
            throw new McpError(-32602, "缺少工具名 params.name");

        var snap = host.Current;
        if (!snap.ByTool.TryGetValue(toolName, out var ep))
            throw new McpError(-32602, $"未知工具: {toolName}（模块可能已被热重载移除，请重新 tools/list）");

        Dictionary<string, JsonElement>? args = null;
        if (prms.TryGetProperty("arguments", out var argEl) && argEl.ValueKind == JsonValueKind.Object)
            args = argEl.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

        try
        {
            var callArgs = Invoker.BindFromDict(ep.Method, args);
            object? target = ep.Method.IsStatic ? null : snap.GetInstance(ep.Type);
            var result = await Invoker.InvokeAsync(ep.Method, target, callArgs);

            string text = result switch
            {
                null => "(null)",
                string s => s,
                _ => JsonSerializer.Serialize(result, JsonOut)
            };
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception ex)
        {
            // 工具执行失败按 MCP 规范放进 result.isError，而不是 JSON-RPC error
            var real = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
            return new { content = new[] { new { type = "text", text = $"执行失败: {real.Message}" } }, isError = true };
        }
    }

    /// <summary>由方法签名 + XML param 注释生成 JSON Schema</summary>
    private static object BuildSchema(MethodInfo mi, IReadOnlyDictionary<string, string> paramDocs)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in mi.GetParameters())
        {
            if (p.Name == null) continue;
            string desc = paramDocs.TryGetValue(p.Name, out var d) ? d : "";
            properties[p.Name] = desc.Length > 0
                ? new { type = MapType(p.ParameterType), description = desc }
                : (object)new { type = MapType(p.ParameterType) };
            if (!p.HasDefaultValue) required.Add(p.Name);
        }

        return new { type = "object", properties, required };
    }

    private static string MapType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)) return "integer";
        if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "number";
        if (t == typeof(string) || t == typeof(char) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(Guid) || t == typeof(TimeSpan) || t.IsEnum) return "string";
        if (t.IsArray || (t.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))) return "array";
        return "object";
    }

    private static IResult RpcError(object? id, int code, string message) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOut);

    private sealed class McpError : Exception
    {
        public int Code { get; }
        public McpError(int code, string message) : base(message) => Code = code;
    }
}
