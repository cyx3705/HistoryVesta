using AppShell.Core.Commands;
using AppShell.Core.Data;
using AppShell.Core.Files;
using AppShell.Shell.Panels;

namespace AppShell.Shell;

/// <summary>
/// 服务端前端代理的框架目录。描述符由真实 <see cref="BuiltinCommands.Register"/>
/// 装配结果投影，名称分类只在此保留一份。
/// </summary>
public static class FrontendCommandCatalog
{
    public const string Source = "framework:frontend";

    private static readonly Lazy<CatalogSnapshot> FrameworkSnapshot =
        new(CreateFrameworkSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<CommandDescriptor> FrameworkSourceDescriptors => FrameworkSnapshot.Value.Frontend;

    /// <summary>Actual desktop registrations for metadata shared with a service host.</summary>
    public static IReadOnlyList<CommandDescriptor> SharedBuiltinSourceDescriptors =>
        FrameworkSnapshot.Value.SharedBuiltins;

    public static IReadOnlyList<CommandDescriptor> CreateFrameworkProxies()
        => FrameworkSnapshot.Value.Frontend.Select(CreateProxy).ToList();

    public static CommandDescriptor CreateProxy(CommandDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new CommandDescriptor
        {
            Name = source.Name,
            Summary = source.Summary,
            Example = source.Example,
            Parameters = source.Parameters.Select(Clone).ToList(),
            SupportsUndo = source.SupportsUndo,
            Readonly = source.Readonly,
            Dangerous = source.IsDangerous,
            ExecutionSite = CommandExecutionSite.Frontend,
            Handler = _ => Task.FromResult(CommandResult.Fail("前端命令未被中继")),
        };
    }

    private static CatalogSnapshot CreateFrameworkSnapshot()
    {
        var registry = new CommandRegistry();
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = null!,
            Docking = null!,
            Console = null!,
            History = null!,
            Settings = null!,
            Log = null!,
            Bus = null!,
            DataDirectory = "",
            Data = CatalogDataService.Instance,
            Workspace = CatalogWorkspaceService.Instance,
            Panels = new PanelManager(),
        });

        var all = registry.All();
        var frontend = all
            .Where(descriptor => registry.GetSource(descriptor.Name)
                .Equals(Source, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sharedBuiltins = all
            .Where(descriptor => BuiltinCommandDefinitions.Contains(descriptor.Name))
            .ToList();
        return new CatalogSnapshot(frontend, sharedBuiltins);
    }

    private static ParameterSpec Clone(ParameterSpec source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        Type = source.Type,
        Required = source.Required,
        Default = source.Default,
        Position = source.Position,
        AllowedValues = source.AllowedValues?.ToArray(),
    };

    private sealed class CatalogDataService : IDataService
    {
        public static readonly CatalogDataService Instance = new();

        public event Action<string, string?>? DataChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> ListConnections() => throw CatalogOnly();
        public IReadOnlyList<string> ListTables(string? connection = null) => throw CatalogOnly();
        public IReadOnlyList<ColumnInfo> GetSchema(string table, string? connection = null) => throw CatalogOnly();
        public QueryResult Query(string table, string? where = null, string? order = null, int limit = 500,
            int page = 1, string? connection = null) => throw CatalogOnly();
        public int Insert(string table, string set, string? connection = null) => throw CatalogOnly();
        public int Update(string table, string set, string? where, string? connection = null) => throw CatalogOnly();
        public int Delete(string table, string? where, string? connection = null) => throw CatalogOnly();
        public long ExportCsv(string table, string filePath, string? where = null, string? connection = null)
            => throw CatalogOnly();
        public (QueryResult? Result, int Affected) ExecuteSql(string sql, string? connection = null)
            => throw CatalogOnly();
    }

    private sealed class CatalogWorkspaceService : IWorkspaceService
    {
        public static readonly CatalogWorkspaceService Instance = new();

        public string Root => throw CatalogOnly();

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void SetRoot(string path) => throw CatalogOnly();
        public IReadOnlyList<WorkspaceEntry> List(string? relativePath = null) => throw CatalogOnly();
        public void CreateDirectory(string relativePath) => throw CatalogOnly();
        public void Rename(string relativePath, string newName) => throw CatalogOnly();
        public void DeleteToRecycleBin(string relativePath) => throw CatalogOnly();
        public string ResolveFull(string relativePath) => throw CatalogOnly();
    }

    private static InvalidOperationException CatalogOnly()
        => new("前端目录依赖仅用于生成描述符，不可执行");

    private sealed record CatalogSnapshot(
        IReadOnlyList<CommandDescriptor> Frontend,
        IReadOnlyList<CommandDescriptor> SharedBuiltins);
}
