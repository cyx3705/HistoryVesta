using System.Text.Json;
using MyAPI.Abstractions;
using MyAPI.Runtime;

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var builder = WebApplication.CreateBuilder(args);
var options = MyAPI.Host.MyApiHostOptions.From(
    key =>
    {
        var scoped = builder.Configuration[$"MyAPI:{key}"];
        return string.IsNullOrWhiteSpace(scoped) ? builder.Configuration[key] : scoped;
    },
    AppContext.BaseDirectory);

if (!options.EnableHttp)
{
    Console.WriteLine("MyAPI is idle. EnableHttp=true is required to start a transport.");
    return;
}

builder.WebHost.UseUrls(options.ListenUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
var catalog = new CommandCatalog();
MyAPI.Host.ModuleHost? moduleHost = null;

if (options.EnableModules)
{
    moduleHost = new MyAPI.Host.ModuleHost(options.ModulesDirectory, catalog);
    moduleHost.Start(options.EnableHotReload);
    app.Lifetime.ApplicationStopping.Register(moduleHost.Dispose);
}

app.MapGet("/", () => Results.Json(new
{
    name = "MyAPI",
    version = "4.0.0-exploration",
    listenUrl = options.ListenUrl,
    modulesDirectory = options.ModulesDirectory,
    modulesEnabled = options.EnableModules,
    mcpEnabled = options.EnableMcp,
    commands = "/api/meta/commands"
}));

app.MapGet("/api/meta/modules", () => Results.Json(new
{
    total = catalog.Modules.Count,
    data = catalog.Modules,
    warnings = moduleHost?.LastReport.Warnings ?? Array.Empty<string>()
}));
app.MapGet("/api/meta/commands", () => Results.Json(new { total = catalog.Commands.Count, data = catalog.Commands }));

if (moduleHost is not null)
{
    app.MapPost("/api/meta/reload", () => Results.Json(moduleHost.Reload()));
}

app.MapPost("/api/commands/{commandId}", async (string commandId, HttpContext context) =>
{
    Dictionary<string, JsonElement> arguments;
    try { arguments = await MyAPI.Host.HttpCommandBinding.ReadArgumentsAsync(context.Request).ConfigureAwait(false); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }

    var caller = context.Request.Headers["X-MyAPI-Caller"].FirstOrDefault();
    var request = new CommandRequest(commandId, arguments);
    var result = await catalog.DispatchAsync(request, CommandContext.Create(caller), context.RequestAborted).ConfigureAwait(false);
    return Results.Json(result, statusCode: MyAPI.Host.HttpCommandResults.StatusCode(result.Status));
});

if (options.EnableMcp) MyAPI.Host.Mcp.Map(app, catalog);

Console.WriteLine($"MyAPI listening on {options.ListenUrl}; modules={options.EnableModules}; mcp={options.EnableMcp}");
await app.RunAsync();
