using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyAPI.Abstractions;

namespace MyAPI.Host;

internal static class Mcp
{
    private const string DefaultProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static void Map(WebApplication app, ICommandDispatcher dispatcher)
    {
        app.MapPost("/mcp", async (HttpContext context) =>
        {
            JsonDocument document;
            try { document = await JsonDocument.ParseAsync(context.Request.Body).ConfigureAwait(false); }
            catch (JsonException ex) { return RpcError(null, -32700, $"Parse error: {ex.Message}"); }

            using (document)
            {
                var root = document.RootElement;
                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    return Results.StatusCode(StatusCodes.Status202Accepted);

                var id = idElement.Clone();
                root.TryGetProperty("params", out var parameters);
                try
                {
                    object result = method switch
                    {
                        "initialize" => Initialize(parameters),
                        "ping" => new { },
                        "tools/list" => ToolsList(dispatcher),
                        "tools/call" => await ToolsCallAsync(dispatcher, parameters, context.RequestAborted).ConfigureAwait(false),
                        _ => throw new McpException(-32601, $"Method not found: {method}")
                    };
                    return Results.Json(new { jsonrpc = "2.0", id, result }, JsonOptions);
                }
                catch (McpException ex) { return RpcError(id, ex.Code, ex.Message); }
                catch (Exception ex) { return RpcError(id, -32603, ex.Message); }
            }
        });

        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.MapDelete("/mcp", () => Results.StatusCode(StatusCodes.Status200OK));
    }

    private static object Initialize(JsonElement parameters)
    {
        var requested = parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("protocolVersion", out var version)
            ? version.GetString() ?? DefaultProtocolVersion
            : DefaultProtocolVersion;

        return new
        {
            protocolVersion = SupportedVersions.Contains(requested) ? requested : DefaultProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "myapi", title = "MyAPI command runtime", version = "4.0.0-exploration" }
        };
    }

    private static object ToolsList(ICommandDispatcher dispatcher) => new
    {
        tools = dispatcher.Commands.Select(command => new
        {
            name = ToolName(command.Id),
            description = command.Description,
            inputSchema = new
            {
                type = "object",
                properties = command.Parameters.ToDictionary(
                    parameter => parameter.Name,
                    parameter => (object)new { type = parameter.Type, description = parameter.Description }),
                required = command.Parameters.Where(x => x.Required).Select(x => x.Name).ToArray()
            }
        }).ToArray()
    };

    private static async Task<object> ToolsCallAsync(ICommandDispatcher dispatcher, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var nameElement))
            throw new McpException(-32602, "params.name is required.");

        var toolName = nameElement.GetString();
        var command = dispatcher.Commands.FirstOrDefault(x => ToolName(x.Id) == toolName);
        if (command is null) throw new McpException(-32602, $"Unknown tool '{toolName}'.");

        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (parameters.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in argumentsElement.EnumerateObject()) arguments[property.Name] = property.Value.Clone();
        }

        var result = await dispatcher.DispatchAsync(new CommandRequest(command.Id, arguments), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new
        {
            content = new[] { new { type = "text", text = result.IsSuccess ? JsonSerializer.Serialize(result.Value, JsonOptions) : result.Error?.Message ?? "Command failed." } },
            isError = !result.IsSuccess
        };
    }

    private static string ToolName(string commandId)
    {
        var builder = new StringBuilder("cmd_");
        foreach (var character in commandId)
        {
            builder.Append(character switch
            {
                '.' => "_d",
                '_' => "_u",
                '-' => "_h",
                _ => character.ToString()
            });
        }

        var value = builder.ToString();
        if (value.Length <= 64) return value;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(commandId)))[..12].ToLowerInvariant();
        return $"{value[..51]}_{hash}";
    }

    private static IResult RpcError(JsonElement? id, int code, string message) =>
        Results.Json(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions);

    private sealed class McpException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
