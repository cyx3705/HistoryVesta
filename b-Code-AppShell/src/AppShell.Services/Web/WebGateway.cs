using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppShell.Core.Commands;
using AppShell.Core.Logging;
using AppShell.Core.Mcp;
using AppShell.Core.Storage;

namespace AppShell.Services.Web;

/// <summary>命令总线的 HTTP/WS 接入点，供本机 Shell 与受鉴权 Web 客户端共用。</summary>
public sealed class WebGateway : IDisposable
{
    public const string KeyPort = "web.port";
    public const string KeyBind = "web.bind";
    public const string KeyToken = "web.token";
    public const string KeyCors = "web.cors";
    public const string KeyConfirm = "web.confirm";
    public const string KeyRateLimit = "web.ratelimit";

    private const int DefaultPort = 8738;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
    };

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ISettingsService _settings;
    private readonly IShellLog _log;
    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<string, EventClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingConfirmations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public WebGateway(Func<CommandBus?> busAccessor, ISettingsService settings, IShellLog log)
    {
        _busAccessor = busAccessor;
        _settings = settings;
        _log = log;
        _log.EntryAdded += OnLogEntry;
    }

    public bool IsRunning => _listener is { IsListening: true };

    public int Port { get; private set; }

    public string BindAddress => NormalizeBind(_settings.Get(KeyBind));

    public int ConnectedClients => _clients.Count;

    public int ConnectedShells => _clients.Values.Count(client => client.Session.Kind == ClientKind.Shell);

    public (bool Success, string Message) Start(int? port = null)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
                return (false, $"Web 服务已在运行(端口 {Port})");

            Port = port ?? _settings.GetInt(KeyPort, DefaultPort);
            if (Port is < 1024 or > 65535)
                return (false, $"端口无效: {Port}(允许 1024~65535)");

            var bind = BindAddress;
            var token = _settings.Get(KeyToken);
            if (!IsLoopback(bind) && string.IsNullOrWhiteSpace(token))
                return (false, "非 localhost 绑定必须先配置非空 web.token");

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://{bind}:{Port}/");
                if (!IsLoopback(bind))
                    _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener?.Close();
                _listener = null;
                return (false, $"监听失败: {ex.Message}");
            }

            if (port.HasValue)
                _settings.Set(KeyPort, port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            _log.Info("web", $"Web 服务已启动: http://{bind}:{Port}/");
            return (true, $"Web 服务已启动: http://{bind}:{Port}/");
        }
    }

    public (bool Success, string Message) Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
                return (false, "Web 服务未在运行");

            _cts?.Cancel();
            _listener?.Close();
            _listener = null;
            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
            foreach (var pending in _pending.Values)
                pending.TrySetResult(CommandResult.Fail("前端连接已断开"));
            _pending.Clear();
            foreach (var confirmation in _pendingConfirmations.Values)
                confirmation.TrySetResult(false);
            _pendingConfirmations.Clear();
            _log.Info("web", "Web 服务已停止");
            return (true, $"Web 服务已停止(端口 {Port} 已释放)");
        }
    }

    public async Task<CommandResult> RelayFrontendCommandAsync(
        string text,
        string source,
        CancellationToken cancellationToken)
    {
        var frontend = _clients.Values.FirstOrDefault(client =>
            client.Session.Kind == ClientKind.Shell && client.Socket.State == WebSocketState.Open);
        if (frontend == null)
            return CommandResult.Fail("前端未连接,请启动应用前端");

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            return CommandResult.Fail("无法创建前端命令关联 ID");

        try
        {
            await SendAsync(frontend, new JsonObject
            {
                ["type"] = "uiCommand",
                ["id"] = id,
                ["text"] = text,
                ["source"] = source,
            }, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail(cancellationToken.IsCancellationRequested
                ? "前端命令已取消"
                : "前端命令响应超时");
        }
        catch (WebSocketException)
        {
            return CommandResult.Fail("前端连接已断开");
        }
        catch (ObjectDisposedException)
        {
            return CommandResult.Fail("前端连接已断开");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public bool RequestWebConfirmation(string prompt, string source, TimeSpan timeout)
    {
        var webClients = _clients.Values.Where(client =>
            client.Session.Kind == ClientKind.Web && client.Socket.State == WebSocketState.Open).ToList();
        if (webClients.Count == 0)
            return false;

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingConfirmations.TryAdd(id, completion))
            return false;
        try
        {
            var payload = new JsonObject
            {
                ["type"] = "confirmation",
                ["id"] = id,
                ["prompt"] = prompt,
                ["source"] = source,
            };
            foreach (var client in webClients)
                _ = SendIgnoringErrorsAsync(client, payload);
            return completion.Task.Wait(timeout) && completion.Task.Result;
        }
        finally
        {
            _pendingConfirmations.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _log.EntryAdded -= OnLogEntry;
        if (IsRunning)
            Stop();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            ApplyCors(context);
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            var session = CreateSession(context.Request);
            if (!Authorize(context.Request, session))
            {
                await WriteJsonAsync(context, new { error = "unauthorized" }, 401).ConfigureAwait(false);
                return;
            }

            if (!AllowRequest(session.Id))
            {
                await WriteJsonAsync(context, new { error = "rate limit exceeded" }, 429).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase)
                && context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, session, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(context, new
                {
                    status = "ok",
                    port = Port,
                    bind = BindAddress,
                    clients = ConnectedClients,
                    shells = ConnectedShells,
                }, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/commands", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "GET")
            {
                var bus = _busAccessor();
                if (bus == null)
                {
                    await WriteJsonAsync(context, new { error = "command bus not ready" }, 503).ConfigureAwait(false);
                    return;
                }

                var schemas = new CommandSchemaExporter(bus.Registry).ExportTools()
                    .ToDictionary(tool => tool.CommandName, StringComparer.OrdinalIgnoreCase);
                var commands = bus.Registry.All().Select(descriptor => new
                {
                    descriptor.Name,
                    descriptor.Summary,
                    descriptor.Example,
                    source = bus.Registry.GetSource(descriptor.Name),
                    descriptor.Readonly,
                    dangerous = descriptor.ConfirmPrompt != null,
                    executionSite = descriptor.ExecutionSite.ToString(),
                    mcpState = McpExposurePolicy.State(descriptor),
                    inputSchema = schemas.GetValueOrDefault(descriptor.Name)?.InputSchema,
                });
                await WriteJsonAsync(context, new { commands }, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/command", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "POST")
            {
                var request = await ReadJsonAsync<CommandRequest>(context.Request).ConfigureAwait(false);
                if (request == null || string.IsNullOrWhiteSpace(request.Text))
                {
                    await WriteJsonAsync(context, new { error = "text is required" }, 400).ConfigureAwait(false);
                    return;
                }

                var bus = _busAccessor();
                if (bus == null)
                {
                    await WriteJsonAsync(context, new { error = "command bus not ready" }, 503).ConfigureAwait(false);
                    return;
                }

                var result = await bus.ExecuteAsync(
                    request.Text,
                    $"{session.Kind}:{session.Name}",
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(context, result, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/confirm", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "POST")
            {
                if (session.Kind != ClientKind.Web)
                {
                    await WriteJsonAsync(context, new { error = "web session required" }, 403).ConfigureAwait(false);
                    return;
                }
                var answer = await ReadJsonAsync<ConfirmRequest>(context.Request).ConfigureAwait(false);
                if (answer == null || string.IsNullOrWhiteSpace(answer.Id)
                    || !_pendingConfirmations.TryGetValue(answer.Id, out var pending))
                {
                    await WriteJsonAsync(context, new { error = "confirmation not found" }, 404).ConfigureAwait(false);
                    return;
                }
                pending.TrySetResult(answer.Approved);
                await WriteJsonAsync(context, new { accepted = true }, 200).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context, new { error = "not found" }, 404).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("web", $"请求处理失败: {ex.Message}");
            TryClose(context, 500);
        }
    }

    private async Task HandleWebSocketAsync(
        HttpListenerContext context,
        ClientSession session,
        CancellationToken cancellationToken)
    {
        var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var client = new EventClient(session, accepted.WebSocket);
        _clients[session.Id] = client;
        await SendAsync(client, new JsonObject
        {
            ["type"] = "connected",
            ["sessionId"] = session.Id,
            ["kind"] = session.Kind.ToString(),
        }, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        try
        {
            while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var received = await client.Socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
                if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage)
                    continue;

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type)
                    && type.GetString() == "commandResult"
                    && root.TryGetProperty("id", out var id)
                    && _pending.TryGetValue(id.GetString() ?? "", out var pending))
                {
                    var success = root.TryGetProperty("success", out var ok) && ok.GetBoolean();
                    var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    object? data = root.TryGetProperty("data", out var payload)
                        ? JsonSerializer.Deserialize<object>(payload.GetRawText(), JsonOptions)
                        : null;
                    pending.TrySetResult(success
                        ? CommandResult.Ok(message, data)
                        : CommandResult.Fail(message));
                }
            }
        }
        finally
        {
            _clients.TryRemove(session.Id, out _);
            client.Dispose();
        }
    }

    private ClientSession CreateSession(HttpListenerRequest request)
    {
        var kind = request.Headers["X-AppShell-Client"]?.Equals("Shell", StringComparison.OrdinalIgnoreCase) == true
            ? ClientKind.Shell
            : ClientKind.Web;
        var name = request.Headers["X-Client-Name"];
        var id = request.Headers["X-Session-Id"];
        id ??= $"{kind}:{request.RemoteEndPoint?.Address}:{name ?? kind.ToString()}";
        return ClientSession.Create(kind, name ?? kind.ToString(), id: id);
    }

    private bool Authorize(HttpListenerRequest request, ClientSession session)
    {
        if (session.Kind == ClientKind.Shell && request.RemoteEndPoint != null
            && IPAddress.IsLoopback(request.RemoteEndPoint.Address))
        {
            return true;
        }

        var token = _settings.Get(KeyToken);
        return !string.IsNullOrWhiteSpace(token)
               && request.Headers["Authorization"] == $"Bearer {token}";
    }

    private bool AllowRequest(string sessionId)
    {
        var limit = Math.Clamp(_settings.GetInt(KeyRateLimit, 120), 10, 10_000);
        var now = DateTimeOffset.UtcNow;
        var window = _rateWindows.AddOrUpdate(
            sessionId,
            _ => new RateWindow(now, 1),
            (_, current) => now - current.Start >= TimeSpan.FromMinutes(1)
                ? new RateWindow(now, 1)
                : current with { Count = current.Count + 1 });
        return window.Count <= limit;
    }

    private void ApplyCors(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        var configured = _settings.Get(KeyCors);
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(configured))
            return;

        var allowed = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return;

        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
        context.Response.Headers["Access-Control-Allow-Headers"] =
            "Authorization, Content-Type, X-Client-Name, X-Session-Id";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    private void OnLogEntry(object? sender, ShellLogEntry entry)
    {
        var payload = new JsonObject
        {
            ["type"] = "log",
            ["time"] = entry.Time.ToString("O"),
            ["level"] = entry.Level.ToString(),
            ["category"] = entry.Category,
            ["message"] = entry.Message,
        };
        foreach (var client in _clients.Values)
            _ = SendIgnoringErrorsAsync(client, payload);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object value, int status)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task SendAsync(EventClient client, JsonNode payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await client.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private static async Task SendIgnoringErrorsAsync(EventClient client, JsonNode payload)
    {
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await SendAsync(client, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string NormalizeBind(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : value.Trim();

    private static bool IsLoopback(string value)
        => value is "127.0.0.1" or "::1" || value.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static void TryClose(HttpListenerContext context, int status)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class EventClient : IDisposable
    {
        private int _disposed;

        public EventClient(ClientSession session, WebSocket socket)
        {
            Session = session;
            Socket = socket;
        }

        public ClientSession Session { get; }

        public WebSocket Socket { get; }

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Socket.Dispose();
            SendLock.Dispose();
        }
    }

    private sealed record CommandRequest(string Text);

    private sealed record ConfirmRequest(string Id, bool Approved);

    private sealed record RateWindow(DateTimeOffset Start, int Count);
}
