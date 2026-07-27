using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppShell.Core.Commands;

namespace AppShell.Services.Web;

/// <summary>WPF Shell 使用的命令 API 与 UI 命令中继客户端。</summary>
public sealed class ShellServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _name;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket? _events;

    public Func<string, JsonElement, object?>? DataDeserializer { get; set; }

    public ShellServiceClient(Uri baseUri, string name)
    {
        _baseUri = baseUri;
        _name = name;
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("X-AppShell-Client", "Shell");
        _http.DefaultRequestHeaders.Add("X-Client-Name", name);
        _http.DefaultRequestHeaders.Add("X-Session-Id", _sessionId);
    }

    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                using var response = await _http.GetAsync("api/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<CommandResult> ExecuteAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "api/command",
                new { text, source },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return CommandResult.Fail($"服务命令请求失败: HTTP {(int)response.StatusCode}");

            var result = await response.Content.ReadFromJsonAsync<ResultEnvelope>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (result == null)
                return CommandResult.Fail("服务返回了空结果");
            object? data = null;
            if (result.Data is { } element)
            {
                var commandName = CommandName(text);
                data = DataDeserializer?.Invoke(commandName, element) ?? element.Clone();
            }
            return new CommandResult
            {
                Success = result.Success,
                Message = result.Message ?? "",
                Data = data,
            };
        }
        catch (HttpRequestException ex)
        {
            return CommandResult.Fail($"后台服务未连接: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Fail("后台服务请求超时");
        }
    }

    public Task RunEventLoopAsync(CommandBus localBus, CancellationToken cancellationToken = default)
        => RunEventLoopCoreAsync(localBus, CancellationTokenSource
            .CreateLinkedTokenSource(_lifetime.Token, cancellationToken).Token);

    private async Task RunEventLoopCoreAsync(CommandBus localBus, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _events?.Dispose();
                _events = new ClientWebSocket();
                _events.Options.SetRequestHeader("X-AppShell-Client", "Shell");
                _events.Options.SetRequestHeader("X-Client-Name", _name);
                _events.Options.SetRequestHeader("X-Session-Id", _sessionId);
                var builder = new UriBuilder(_baseUri)
                {
                    Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
                    Path = "/api/events",
                };
                await _events.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
                await ReceiveEventsAsync(_events, localBus, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException)
            {
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveEventsAsync(
        ClientWebSocket socket,
        CommandBus localBus,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                continue;

            using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "uiCommand")
                continue;

            var id = root.GetProperty("id").GetString() ?? "";
            var text = root.GetProperty("text").GetString() ?? "";
            var commandResult = await localBus.ExecuteAsync(
                text,
                "Service:Relay",
                cancellationToken).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["type"] = "commandResult",
                ["id"] = id,
                ["success"] = commandResult.Success,
                ["message"] = commandResult.Message,
                ["data"] = ToNode(commandResult.Data),
            };
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _events?.Dispose();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private static JsonNode? ToNode(object? data)
    {
        if (data == null)
            return null;
        try
        {
            return JsonSerializer.SerializeToNode(data, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string CommandName(string text)
    {
        try
        {
            return CommandParser.Parse(text).Name;
        }
        catch (CommandSyntaxException)
        {
            return text.Trim().Split(' ', 2)[0].ToLowerInvariant();
        }
    }

    private sealed record ResultEnvelope(bool Success, string? Message, JsonElement? Data);
}
