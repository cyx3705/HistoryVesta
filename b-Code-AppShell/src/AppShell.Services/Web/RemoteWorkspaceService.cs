using AppShell.Core.Commands;
using AppShell.Core.Files;
using System.Text.Json;

namespace AppShell.Services.Web;

/// <summary>通过服务命令读取和修改服务器工作区，不持有客户端本机业务目录。</summary>
public sealed class RemoteWorkspaceService : IWorkspaceService
{
    private readonly ShellServiceClient _client;
    private string _root = "服务器工作区";

    public RemoteWorkspaceService(ShellServiceClient client) => _client = client;

    public bool CanSelectLocalRoot => false;

    public string Root => _root;

    public event Action? Changed;

    public void SetRoot(string path)
        => Mutate($"res.root path={CommandParser.QuoteArg(path)}");

    public IReadOnlyList<WorkspaceEntry> List(string? relativePath = null)
    {
        var result = Execute(Command("res.list", ("path", relativePath)));
        var listing = result.Data switch
        {
            WorkspaceListing typed => typed,
            JsonElement json => JsonSerializer.Deserialize<WorkspaceListing>(json.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
            _ => null,
        } ?? throw new InvalidOperationException("服务器未返回结构化工作区列表");
        _root = listing.Root;
        return listing.Entries;
    }

    public void CreateDirectory(string relativePath)
        => Mutate(Command("res.mkdir", ("path", relativePath)));

    public void Rename(string relativePath, string newName)
        => Mutate(Command("res.rename", ("path", relativePath), ("to", newName)));

    public void DeleteToRecycleBin(string relativePath)
        => Mutate(Command("res.delete", ("path", relativePath)));

    public string ResolveFull(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath ?? ""));
        var root = Path.GetFullPath(_root);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"路径越出服务器工作区根目录,已拒绝: {relativePath}");
        }
        return full;
    }

    private void Mutate(string command)
    {
        Execute(command);
        Changed?.Invoke();
    }

    private CommandResult Execute(string command)
    {
        var requestTimeout = _client.RemoteRequestTimeout;
        using var timeout = new CancellationTokenSource(requestTimeout);
        CommandResult result;
        try
        {
            result = _client.ExecuteAsync(command, "Shell:Resource", timeout.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"服务器工作区请求超过 {requestTimeout.TotalSeconds:0} 秒，已取消");
        }
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    private static string Command(string name, params (string Name, string? Value)[] parameters)
    {
        var parts = new List<string> { name };
        foreach (var (parameterName, value) in parameters)
        {
            if (value != null)
                parts.Add($"{parameterName}={CommandParser.QuoteArg(value)}");
        }
        return string.Join(' ', parts);
    }
}
