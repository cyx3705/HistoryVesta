using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppShell.Core.Commands;
using AppShell.Core;
using AppShell.Core.Mcp;
using AppShell.Core.Logging;
using AppShell.Core.Storage;

namespace AppShell.Services.Mcp;

/// <summary>
/// MCP 网关(V2.1 §4/§6):HttpListener + JSON-RPC 2.0(Streamable HTTP 无状态子集),
/// 仅监听 127.0.0.1。铁律 1:唯一上游是指令总线——本类只认识
/// CommandSchemaExporter / CommandBus,不 import ModuleHost、不反射模块类型。
/// 宿主启动时默认自动监听;mcp.autostart=false 可关闭,mcp.start 仍可手动恢复。
/// 每次调用/拒绝均留痕 mcp_history(铁律 2 / MS-05)。
/// </summary>
public sealed class McpGateway : IDisposable
{
    public const string KeyPort = "mcp.port";
    public const string KeyPolicy = "mcp.policy";
    public const string KeyToken = "mcp.token";
    public const string KeyAutostart = "mcp.autostart";
    public const string KeyTimeout = "mcp.timeout";
    public const string KeyConfirm = "mcp.confirm";              // deny(默认) / host
    public const string KeyConfirmTimeout = "mcp.confirmtimeout"; // 秒,默认 60,夹取 10~600

    private const int DefaultPort = 8737;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ISettingsService _settings;
    private readonly IShellLog _log;
    private readonly IMcpAuditLog _history;
    private readonly PromptGovernanceStore _prompts;
    private readonly ApplicationIdentity _identity;

    /// <summary>宿主确认中继对话框(client, 完整提示, 超时秒) → true/false/null;host 档需要。</summary>
    private readonly Func<string, string, int, bool?>? _remoteConfirm;

    /// <summary>CX-02 §9-4:同一时刻只弹一个中继确认框,多个远程请求按序处理。</summary>
    private readonly SemaphoreSlim _confirmGate = new(1, 1);

    private readonly object _lifecycleLock = new();

    private CommandSchemaExporter? _exporter;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _clientName = "client";
    private long _callCount;
    private string _lastCall = "(无)";

    public McpGateway(
        Func<CommandBus?> busAccessor, ISettingsService settings, IShellLog log, IMcpAuditLog history,
        PromptGovernanceStore prompts, ApplicationIdentity identity,
        Func<string, string, int, bool?>? remoteConfirm = null)
    {
        _busAccessor = busAccessor;
        _settings = settings;
        _log = log;
        _history = history;
        _prompts = prompts;
        _identity = identity;
        _remoteConfirm = remoteConfirm;
    }

    /// <summary>确认中继模式(CX-02):deny=危险指令一律拒绝(默认);host=宿主弹框人工裁决。</summary>
    public string ConfirmMode
    {
        get
        {
            var m = _settings.Get(KeyConfirm);
            return m != null && m.Equals("host", StringComparison.OrdinalIgnoreCase) ? "host" : "deny";
        }
    }

    public int ConfirmTimeout
        => int.TryParse(
            _settings.Get(KeyConfirmTimeout), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var t)
            ? Math.Clamp(t, 10, 600)
            : 60;

    /// <summary>缺省启用；仅显式配置 false 时关闭宿主启动自动监听。</summary>
    public bool AutostartEnabled
        => !bool.TryParse(_settings.Get(KeyAutostart), out var enabled) || enabled;

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

    /// <summary>
    /// 当前策略下 tools/list 会暴露的工具(每次现算,与模块热重载天然同步)。
    /// 危险指令(带确认闸口)默认不暴露;仅当 mcp.confirm=host 且策略=standard 时暴露,
    /// 供 Codex 发现"可请求、由人把关"的操作(V2.2 CX-02)。
    /// </summary>
    public IReadOnlyList<McpToolInfo> VisibleTools()
    {
        var exporter = GetExporter();
        if (exporter == null)
            return Array.Empty<McpToolInfo>();
        var policy = Policy;
        var relayOn = ConfirmMode == "host" && policy == "standard";
        var registry = _busAccessor()?.Registry;
        if (registry == null)
            return Array.Empty<McpToolInfo>();

        return exporter.ExportTools()
            .Where(t =>
            {
                if (!registry.TryGet(t.CommandName, out var descriptor))
                    return false;
                if (t.Dangerous)
                    return relayOn && McpExposurePolicy.HardExclusionReason(t.CommandName) == null;
                return McpExposurePolicy.IsVisible(descriptor, policy);
            })
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
            DescriptionsProvider = _prompts.AllEffectiveDescriptions,
        };
    }

    // ---------------------------------------------------------------- 生命周期(MG-05)

    /// <summary>宿主完成全部指令和模块装配后调用；禁用时不监听但仍视为正常配置。</summary>
    public (bool Success, string Message) TryAutostart()
    {
        if (!AutostartEnabled)
            return (true, "MCP 自动启动已关闭(mcp.autostart=false)");

        var (success, message) = Start(null);
        return (success, success ? $"自启动: {message}" : $"自启动失败: {message}");
    }

    public (bool Success, string Message) Start(int? port)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
                return (false, $"MCP 服务已在运行(端口 {Port}),先 mcp.stop");

            Port = port
                   ?? (int.TryParse(
                           _settings.Get(KeyPort), System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out var p)
                       ? p
                       : DefaultPort);
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
                _settings.Set(KeyPort, port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
        try
        {
            using var reader = new StreamReader(
                request.InputStream, StrictUtf8, detectEncodingFromByteOrderMarks: true);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            await WriteJsonAsync(
                context, RpcError(null, -32700, "请求体不是有效的 UTF-8 JSON"), 400).ConfigureAwait(false);
            return;
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
                ["name"] = _identity.Name,
                ["version"] = _identity.Version,
            },
        });
    }

    private JsonObject HandleToolsList(JsonNode? id)
    {
        var tools = new JsonArray();
        foreach (var t in VisibleTools())
        {
            var description = t.Dangerous
                ? t.Description + "\n⚠ 危险操作:调用会请求宿主端人工确认(mcp.confirm=host),批准后才执行。"
                : t.Description;
            tools.Add(new JsonObject
            {
                ["name"] = t.ToolName,
                ["description"] = description,
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

        var bus = _busAccessor();
        if (bus == null)
            return RpcError(id, -32603, "宿主总线未就绪");

        var commandText = CommandSchemaExporter.BuildCommandText(tool.CommandName, arguments);

        // 危险指令(总线确认闸口类):按 mcp.confirm 处置(CX-02 / MS-04)
        if (tool.Dangerous)
        {
            if (ConfirmMode != "host")
            {
                _history.RecordMcp(_clientName, tool.ToolName, argsText, "拒绝", 0);
                _log.Warn("mcp", $"拒绝危险工具调用: {tool.ToolName}(mcp.confirm=deny)");
                return RpcResult(id, ToolText(
                    $"已拒绝: {tool.CommandName} 是需二次确认的危险指令。当前 mcp.confirm=deny;" +
                    "宿主执行 app.set key=mcp.confirm value=host 后,远程请求将弹框由人工裁决。",
                    isError: true));
            }

            if (Policy == "readonly")
            {
                _history.RecordMcp(_clientName, tool.ToolName, argsText, "拒绝", 0);
                return RpcResult(id, ToolText(
                    $"已拒绝: 当前策略为 readonly,不受理危险指令;宿主切 standard 后方可经中继确认执行。",
                    isError: true));
            }

            var decision = await RelayConfirmAsync(bus, tool.CommandName, arguments, commandText)
                .ConfigureAwait(false);
            if (decision == false)
            {
                _history.RecordMcp(_clientName, tool.ToolName, argsText, "远程拒绝", 0);
                _log.Warn("mcp", $"中继确认:宿主拒绝 {tool.ToolName}");
                return RpcResult(id, ToolText("宿主已拒绝该远程请求(人工点否)。", isError: true));
            }

            if (decision == null)
            {
                _history.RecordMcp(_clientName, tool.ToolName, argsText, "确认超时", 0);
                _log.Warn("mcp", $"中继确认:超时拒绝 {tool.ToolName}");
                return RpcResult(id, ToolText(
                    $"确认超时({ConfirmTimeout}s 内无人操作),已按拒绝处理。", isError: true));
            }

            _log.Info("mcp", $"中继确认:宿主批准 {tool.ToolName},执行中");
            return await ExecuteToolAsync(id, bus, tool, commandText, argsText,
                preApproved: true, relayNote: "远程确认通过").ConfigureAwait(false);
        }

        // §6.2:readonly 档优先读取命令自描述；名称白名单仅为迁移兼容层。
        if (Policy == "readonly"
            && (!bus.Registry.TryGet(tool.CommandName, out var descriptor)
                || (!descriptor.Readonly && !McpExposurePolicy.IsReadonlyAllowed(tool.CommandName))))
        {
            _history.RecordMcp(_clientName, tool.ToolName, argsText, "拒绝", 0);
            _log.Warn("mcp", $"拒绝调用(策略 readonly 未暴露): {tool.ToolName}");
            return RpcResult(id, ToolText(
                $"已拒绝: 当前暴露策略为 readonly,{tool.CommandName} 未开放;宿主执行 app.set key=mcp.policy value=standard 可放开动作类指令",
                isError: true));
        }

        return await ExecuteToolAsync(id, bus, tool, commandText, argsText,
            preApproved: false, relayNote: null).ConfigureAwait(false);
    }

    /// <summary>组装指令经总线执行并映射为 MCP 结果;preApproved=true 时置确认预批准域(CX-03)。</summary>
    private async Task<JsonObject> ExecuteToolAsync(
        JsonNode? id, CommandBus bus, McpToolInfo tool, string commandText, string argsText,
        bool preApproved, string? relayNote)
    {
        var timeoutSeconds = int.TryParse(
            _settings.Get(KeyTimeout), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var t)
            ? Math.Clamp(t, 5, 3600)
            : 120;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Task<CommandResult> Run() => bus.ExecuteAsync(commandText, $"MCP:{_clientName}");
        var execTask = preApproved
            ? McpConfirmationScope.RunPreApprovedAsync(Run)
            : Run();

        var finished = await Task.WhenAny(execTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)))
            .ConfigureAwait(false);

        Interlocked.Increment(ref _callCount);

        if (finished != execTask)
        {
            // MG-07:超时只切断响应,不撕裂总线执行——指令继续跑完并留痕
            _lastCall = $"{tool.ToolName}(超时 {timeoutSeconds}s)";
            _history.RecordMcp(_clientName, tool.ToolName, argsText, Note(relayNote, "超时"), sw.ElapsedMilliseconds);
            return RpcResult(id, ToolText(
                $"执行超时({timeoutSeconds}s): 指令仍在宿主内继续执行并留痕,可稍后经只读指令查询结果",
                isError: true));
        }

        var result = await execTask.ConfigureAwait(false);
        sw.Stop();
        _lastCall = $"{tool.ToolName} → {(result.Success ? "成功" : "失败")}({sw.ElapsedMilliseconds}ms)";
        _history.RecordMcp(_clientName, tool.ToolName, argsText,
            Note(relayNote, result.Success ? "成功" : "失败"), sw.ElapsedMilliseconds);

        // AppShell 0.5.0:保留既有文本块以兼容旧客户端,并加发 structuredContent.data。
        // 结构化字段从同一份 legacyJson 派生,确保新旧载荷语义一致。
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = result.Message },
        };
        JsonNode? structuredData = null;
        if (result.Success && result.Data != null)
        {
            try
            {
                var legacyJson = JsonSerializer.Serialize(result.Data, DataJson);
                content.Add(new JsonObject { ["type"] = "text", ["text"] = legacyJson });
                structuredData = JsonNode.Parse(legacyJson);
            }
            catch (Exception)
            {
                // Data 序列化失败不影响文本结果(如含 WPF 类型的对象);结构化字段同时不发
                structuredData = null;
            }
        }

        var payload = new JsonObject
        {
            ["content"] = content,
            ["isError"] = !result.Success,
        };
        if (structuredData != null)
        {
            // 规范要求 structuredContent 为 JSON 对象;Data 有数组/对象/字符串三态,统一 data 信封
            payload["structuredContent"] = new JsonObject { ["data"] = structuredData };
        }

        return RpcResult(id, payload);

        static string Note(string? note, string outcome)
            => note == null ? outcome : $"{note}·{outcome}";
    }

    /// <summary>
    /// 宿主确认中继(CX-02):复用描述符 ConfirmPrompt 文案,标注 MCP 客户端弹框由人裁决。
    /// 返回 true=批准 / false=拒绝 / null=超时。ConfirmPrompt 对本次输入返回 null
    /// (如受保护分支)时直接放行,交由指令处理器按业务规则拒绝。
    /// </summary>
    private async Task<bool?> RelayConfirmAsync(
        CommandBus bus, string commandName, JsonElement? arguments, string commandText)
    {
        if (_remoteConfirm == null)
            return false; // 无对话通道 → 安全缺省拒绝

        var prompt = BuildConfirmPrompt(bus, commandName, arguments);
        if (prompt == null)
            return true; // 本次输入无需人工确认(处理器自行判定)

        var full = $"【MCP 客户端 “{_clientName}” 的远程请求】\n\n{prompt}\n\n等价指令: {commandText}";
        await _confirmGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _remoteConfirm(_clientName, full, ConfirmTimeout);
        }
        finally
        {
            _confirmGate.Release();
        }
    }

    private string? BuildConfirmPrompt(CommandBus bus, string commandName, JsonElement? arguments)
    {
        if (!bus.Registry.TryGet(commandName, out var descriptor) || descriptor.ConfirmPrompt == null)
            return null;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                values[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        try
        {
            var ctx = new CommandContext(descriptor, values, $"MCP:{_clientName}", null, CancellationToken.None);
            return descriptor.ConfirmPrompt(ctx);
        }
        catch (Exception)
        {
            // 文案构建失败不影响中继:回落到通用提示,人工仍能裁决
            return $"远程请求执行危险指令: {commandName}\n(参数: {(arguments?.GetRawText() ?? "{}")})";
        }
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
