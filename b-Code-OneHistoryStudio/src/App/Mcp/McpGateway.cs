using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using OneHistoryStudio.Git;

namespace OneHistoryStudio.Mcp;

/// <summary>
/// MCP 网关(V2.1 §4/§6):HttpListener + JSON-RPC 2.0(Streamable HTTP 无状态子集),
/// 仅监听 127.0.0.1。铁律 1:唯一上游是指令总线——本类只认识
/// CommandSchemaExporter / CommandBus,不 import ModuleHost、不反射模块类型。
/// 默认关闭(MS-01),mcp.start 显式开启;每次调用/拒绝均留痕 mcp_history(铁律 2 / MS-05)。
/// </summary>
public sealed class McpGateway : IDisposable
{
    public const string KeyPort = "mcp.port";
    public const string KeyPolicy = "mcp.policy";
    public const string KeyToken = "mcp.token";
    public const string KeyAutostart = "mcp.autostart";
    public const string KeyTimeout = "mcp.timeout";

    private const int DefaultPort = 8737;

    /// <summary>readonly 档白名单(§6.2):只读查询类指令,代码内清单。</summary>
    private static readonly HashSet<string> ReadonlyWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "history",
        "proj.list", "proj.tree", "proj.scan", "proj.config", "proj.metalist",
        "db.query", "db.tables", "db.schema", "db.list",
        "module.list", "win.list", "layout.list", "app.get",
    };

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ISettingsService _settings;
    private readonly IShellLog _log;
    private readonly HistoryRecorder _history;
    private readonly object _lifecycleLock = new();

    private CommandSchemaExporter? _exporter;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _clientName = "client";
    private long _callCount;
    private string _lastCall = "(无)";

    public McpGateway(
        Func<CommandBus?> busAccessor, ISettingsService settings, IShellLog log, HistoryRecorder history)
    {
        _busAccessor = busAccessor;
        _settings = settings;
        _log = log;
        _history = history;
    }

    public bool IsRunning => _listener is { IsListening: true };

    public int Port { get; private set; }

    public string Policy
    {
        get
        {
            var p = _settings.Get(KeyPolicy);
            return p != null && p.Equals("standard", StringComparison.OrdinalIgnoreCase)
                ? "standard"
                : "readonly";
        }
    }

    public long CallCount => Interlocked.Read(ref _callCount);

    public string LastCall => _lastCall;

    /// <summary>当前策略下 tools/list 会暴露的工具(每次现算,与模块热重载天然同步)。</summary>
    public IReadOnlyList<McpToolInfo> VisibleTools()
    {
        var exporter = GetExporter();
        if (exporter == null)
            return Array.Empty<McpToolInfo>();
        var policy = Policy;
        return exporter.ExportTools()
            .Where(t => !t.Dangerous
                        && (policy == "standard" || ReadonlyWhitelist.Contains(t.CommandName)))
            .ToList();
    }

    private CommandSchemaExporter? GetExporter()
    {
        if (_exporter != null)
            return _exporter;
        var bus = _busAccessor();
        if (bus == null)
            return null;
        return _exporter = new CommandSchemaExporter(bus.Registry)
        {
            DescriptionsProvider = _history.AllMcpDescriptions, // V2.1.1:提示词覆盖对客户端生效
        };
    }

    // ---------------------------------------------------------------- 生命周期(MG-05)

    public (bool Success, string Message) Start(int? port)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
                return (false, $"MCP 服务已在运行(端口 {Port}),先 mcp.stop");

            Port = port
                   ?? (int.TryParse(_settings.Get(KeyPort), out var p) ? p : DefaultPort);
            if (Port is < 1024 or > 65535)
                return (false, $"端口无效: {Port}(允许 1024~65535)");

            try
            {
                // 只挂根前缀再自行校验路径:HttpListener 前缀须以 / 结尾,
                // 挂 /mcp/ 会漏接不带尾斜杠的 /mcp 请求
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener?.Close();
                _listener = null;
                return (false, $"监听失败(端口被占用?): {ex.Message}");
            }

            if (port.HasValue)
                _settings.Set(KeyPort, port.Value.ToString());

            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            _log.Info("mcp", $"MCP 服务已启动: http://127.0.0.1:{Port}/mcp(策略 {Policy})");
            return (true, $"MCP 服务已启动: http://127.0.0.1:{Port}/mcp\n策略 {Policy},当前暴露 {VisibleTools().Count} 个工具");
        }
    }

    public (bool Success, string Message) Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
                return (false, "MCP 服务未在运行");

            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
            _log.Info("mcp", "MCP 服务已停止");
            return (true, $"MCP 服务已停止(端口 {Port} 已释放)");
        }
    }

    public void Dispose()
    {
        if (IsRunning)
            Stop();
    }

    // ---------------------------------------------------------------- 请求处理

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                return; // 正常停机
            }
            catch (Exception ex)
            {
                _log.Warn("mcp", $"接收请求失败: {ex.Message}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleRequestAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("mcp", $"请求处理异常: {ex.Message}");
                    TryClose(context, 500);
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? "";

        if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            TryClose(context, 404);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            TryClose(context, 405);
            return;
        }

        // MS-02:可选共享密钥
        var token = _settings.Get(KeyToken);
        if (!string.IsNullOrEmpty(token))
        {
            var auth = request.Headers["Authorization"];
            if (auth != $"Bearer {token}")
            {
                _history.RecordMcp(_clientName, "(auth)", "", "拒绝", 0);
                _log.Warn("mcp", "拒绝一次请求: Authorization 缺失或 token 不匹配(MS-02)");
                TryClose(context, 401);
                return;
            }
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (body.Length > 1_048_576)
        {
            await WriteJsonAsync(context, RpcError(null, -32600, "请求体过大"), 400).ConfigureAwait(false);
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context, RpcError(null, -32700, "JSON 解析失败"), 200).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            JsonNode? id = root.TryGetProperty("id", out var idEl)
                ? JsonNode.Parse(idEl.GetRawText())
                : null;
            var hasParams = root.TryGetProperty("params", out var prms);

            // 通知(无 id)只确认收到,不回 JSON-RPC 响应体
            if (id == null && method.StartsWith("notifications/", StringComparison.Ordinal))
            {
                TryClose(context, 202);
                return;
            }

            var response = method switch
            {
                "initialize" => HandleInitialize(id, hasParams ? prms : null),
                "ping" => RpcResult(id, new JsonObject()),
                "tools/list" => HandleToolsList(id),
                "tools/call" => await HandleToolsCallAsync(id, hasParams ? prms : null).ConfigureAwait(false),
                _ => RpcError(id, -32601, $"method not found: {method}"),
            };

            await WriteJsonAsync(context, response, 200).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- JSON-RPC 方法

    private JsonObject HandleInitialize(JsonNode? id, JsonElement? prms)
    {
        if (prms is { } p
            && p.TryGetProperty("clientInfo", out var ci)
            && ci.TryGetProperty("name", out var cn)
            && cn.GetString() is { Length: > 0 } name)
        {
            _clientName = name;
        }

        var protocolVersion = prms is { } pv && pv.TryGetProperty("protocolVersion", out var ver)
            ? ver.GetString() ?? "2025-03-26"
            : "2025-03-26";

        _log.Info("mcp", $"客户端握手: {_clientName}");
        return RpcResult(id, new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "OneHistoryStudio",
                ["version"] = "2.1.0",
            },
        });
    }

    private JsonObject HandleToolsList(JsonNode? id)
    {
        var tools = new JsonArray();
        foreach (var t in VisibleTools())
        {
            tools.Add(new JsonObject
            {
                ["name"] = t.ToolName,
                ["description"] = t.Description,
                ["inputSchema"] = JsonNode.Parse(t.InputSchema.ToJsonString()),
            });
        }

        return RpcResult(id, new JsonObject { ["tools"] = tools });
    }

    private async Task<JsonObject> HandleToolsCallAsync(JsonNode? id, JsonElement? prms)
    {
        if (prms is not { } p || !p.TryGetProperty("name", out var nameEl)
                              || nameEl.GetString() is not { Length: > 0 } toolName)
        {
            return RpcError(id, -32602, "缺少工具名 params.name");
        }

        JsonElement? arguments = p.TryGetProperty("arguments", out var args) ? args.Clone() : null;
        var argsText = arguments?.GetRawText() ?? "{}";

        var exporter = GetExporter();
        var tool = exporter?.Find(toolName);
        if (tool == null)
            return RpcError(id, -32602, $"未知工具: {toolName}");

        // MS-04:危险指令(总线确认闸口类)对 MCP 一律拒绝——远端没有"人"来点确认
        if (tool.Dangerous)
        {
            _history.RecordMcp(_clientName, tool.ToolName, argsText, "拒绝", 0);
            _log.Warn("mcp", $"拒绝危险工具调用: {tool.ToolName}(MS-04)");
            return RpcResult(id, ToolText(
                $"已拒绝: {tool.CommandName} 是需二次确认的危险指令,须在宿主 UI/控制台人工执行(V2.1 安全策略 MS-04)",
                isError: true));
        }

        // §6.2:readonly 档只放行白名单
        if (Policy == "readonly" && !ReadonlyWhitelist.Contains(tool.CommandName))
        {
            _history.RecordMcp(_clientName, tool.ToolName, argsText, "拒绝", 0);
            _log.Warn("mcp", $"拒绝调用(策略 readonly 未暴露): {tool.ToolName}");
            return RpcResult(id, ToolText(
                $"已拒绝: 当前暴露策略为 readonly,{tool.CommandName} 未开放;宿主执行 app.set key=mcp.policy value=standard 可放开动作类指令",
                isError: true));
        }

        var bus = _busAccessor();
        if (bus == null)
            return RpcError(id, -32603, "宿主总线未就绪");

        var commandText = CommandSchemaExporter.BuildCommandText(tool.CommandName, arguments);
        var timeoutSeconds = int.TryParse(_settings.Get(KeyTimeout), out var t) ? Math.Clamp(t, 5, 3600) : 120;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var execTask = bus.ExecuteAsync(commandText, $"MCP:{_clientName}");
        var finished = await Task.WhenAny(execTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)))
            .ConfigureAwait(false);

        Interlocked.Increment(ref _callCount);

        if (finished != execTask)
        {
            // MG-07:超时只切断响应,不撕裂总线执行——指令继续跑完并留痕
            _lastCall = $"{tool.ToolName}(超时 {timeoutSeconds}s)";
            _history.RecordMcp(_clientName, tool.ToolName, argsText, "超时", sw.ElapsedMilliseconds);
            return RpcResult(id, ToolText(
                $"执行超时({timeoutSeconds}s): 指令仍在宿主内继续执行并留痕,可稍后经只读指令查询结果",
                isError: true));
        }

        var result = await execTask.ConfigureAwait(false);
        sw.Stop();
        _lastCall = $"{tool.ToolName} → {(result.Success ? "成功" : "失败")}({sw.ElapsedMilliseconds}ms)";
        _history.RecordMcp(_clientName, tool.ToolName, argsText,
            result.Success ? "成功" : "失败", sw.ElapsedMilliseconds);

        // MG-04:Message → 文本段;Data 非空追加 JSON 结构化段
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = result.Message },
        };
        if (result.Success && result.Data != null)
        {
            try
            {
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(result.Data, DataJson),
                });
            }
            catch (Exception)
            {
                // Data 序列化失败不影响文本结果(如含 WPF 类型的对象)
            }
        }

        return RpcResult(id, new JsonObject
        {
            ["content"] = content,
            ["isError"] = !result.Success,
        });
    }

    private static readonly JsonSerializerOptions DataJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
    };

    // ---------------------------------------------------------------- JSON-RPC 编码

    private static JsonObject ToolText(string text, bool isError) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    private static JsonObject RpcResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject RpcError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static async Task WriteJsonAsync(HttpListenerContext context, JsonObject payload, int status)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static void TryClose(HttpListenerContext context, int status)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.Close();
        }
        catch (Exception)
        {
            // 客户端已断开等,忽略
        }
    }
}
