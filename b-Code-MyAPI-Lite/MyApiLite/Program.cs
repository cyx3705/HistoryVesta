using System.Reflection;
using MyApiLite;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
var builder = WebApplication.CreateBuilder(args);

// 端口和模块目录都可通过配置覆盖：--Port 8080 / --ModulesDir D:\xxx（或环境变量、appsettings.json）
int port = int.TryParse(builder.Configuration["Port"], out var p) ? p : 5100;
string modulesDir = builder.Configuration["ModulesDir"] ?? Path.Combine(AppContext.BaseDirectory, "Modules");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

var host = new ModuleHost(modulesDir);
host.Start();
app.Lifetime.ApplicationStopping.Register(host.Dispose);

// ==================== 元信息 ====================
app.MapGet("/", () => Results.Json(new
{
    name = "MyAPI Lite",
    modulesDir,
    usage = new[]
    {
        "GET  /api/meta/modules            已加载的模块",
        "GET  /api/meta/endpoints          全部可调用接口",
        "POST /api/meta/reload             手动触发重载",
        "GET  /api/{命名空间}/{类}/{方法}?参数=值",
        "POST /api/{命名空间}/{类}/{方法}  (JSON body 传参)",
        "POST /mcp                         MCP 服务端点 (Streamable HTTP)"
    }
}));

app.MapGet("/api/meta/modules", () => Results.Json(new { total = host.Modules.Count, data = host.Modules }));

app.MapGet("/api/meta/endpoints", () => Results.Json(new
{
    total = host.Endpoints.Count,
    data = host.Endpoints.OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
}));

app.MapPost("/api/meta/reload", () =>
{
    host.Reload();
    return Results.Json(new { success = true, modules = host.Modules.Count, endpoints = host.Endpoints.Count });
});

// ==================== 动态接口 ====================
app.MapMethods("/api/{ns}/{cls}/{method}", new[] { "GET", "POST" },
    async (string ns, string cls, string method, HttpContext ctx) =>
    {
        var snap = host.Current;
        if (!snap.Lookup.TryGetValue($"{ns}/{cls}/{method}", out var ep))
            return Results.NotFound(new
            {
                error = $"接口未注册: /api/{ns}/{cls}/{method}",
                hint = "GET /api/meta/endpoints 查看全部可用接口"
            });

        try
        {
            var callArgs = await Invoker.BindArgsAsync(ep.Method, ctx);
            object? target = ep.Method.IsStatic ? null : snap.GetInstance(ep.Type);
            var result = await Invoker.InvokeAsync(ep.Method, target, callArgs);
            return Results.Json(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (TargetInvocationException ex)
        {
            var real = ex.InnerException ?? ex;
            return Results.Json(new { error = $"方法执行失败: {real.Message}" }, statusCode: 500);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = $"执行失败: {ex.Message}" }, statusCode: 500);
        }
    });

// ==================== MCP 服务端点 ====================
Mcp.Map(app, host);

Console.WriteLine($"[MyAPI Lite] http://localhost:{port}  |  MCP: http://localhost:{port}/mcp  |  模块目录: {modulesDir}");
app.Run();
