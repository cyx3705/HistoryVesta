using System.Reflection;
using System.Runtime.Loader;
using MyAPI.Abstractions;
using MyAPI.Runtime;

namespace MyAPI.Host;

public sealed record ModuleLoadReport(
    DateTimeOffset LoadedAt,
    IReadOnlyList<ModuleDescriptor> Modules,
    int CommandCount,
    IReadOnlyList<string> Warnings);

public sealed class ModuleHost : IDisposable
{
    private readonly string _directory;
    private readonly CommandCatalog _catalog;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private ModuleLoadContext? _loadContext;
    private bool _disposed;

    public ModuleHost(string directory, CommandCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(catalog);
        _directory = Path.GetFullPath(directory);
        _catalog = catalog;
        LastReport = new ModuleLoadReport(DateTimeOffset.MinValue, Array.Empty<ModuleDescriptor>(), 0, Array.Empty<string>());
    }

    public IReadOnlyList<ModuleDescriptor> Modules => _catalog.Modules;
    public ModuleLoadReport LastReport { get; private set; }

    public void Start(bool hotReload)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(_directory);
        Reload();
        if (!hotReload) return;

        _debounce = new Timer(_ => ReloadSafely(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(_directory, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    public ModuleLoadReport Reload()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            var nextContext = new ModuleLoadContext(_directory);
            try
            {
                var warnings = new List<string>();
                var modules = LoadModules(nextContext, warnings);
                _catalog.ReplaceAll(modules);

                var oldContext = _loadContext;
                _loadContext = nextContext;
                oldContext?.Unload();
                LastReport = new ModuleLoadReport(DateTimeOffset.UtcNow, _catalog.Modules, _catalog.Commands.Count, warnings);
                return LastReport;
            }
            catch
            {
                nextContext.Unload();
                throw;
            }
        }
    }

    private IReadOnlyList<IMyApiModule> LoadModules(ModuleLoadContext context, List<string> warnings)
    {
        var modules = new List<IMyApiModule>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*Module.dll").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadRoot(path);
            }
            catch (BadImageFormatException)
            {
                throw new InvalidOperationException($"Module '{Path.GetFileName(path)}' is not a valid .NET assembly.");
            }
            catch (FileLoadException ex)
            {
                throw new InvalidOperationException($"Module '{Path.GetFileName(path)}' could not be loaded: {ex.Message}", ex);
            }

            var exportedTypes = GetLoadableTypes(assembly).ToArray();
            foreach (var type in exportedTypes)
            {
                if (type.GetInterfaces().Any(x => x.FullName == typeof(IMyApiModule).FullName)
                    && !typeof(IMyApiModule).IsAssignableFrom(type))
                    warnings.Add($"Module '{type.FullName}' uses an incompatible MyAPI.Abstractions assembly.");
                if (!typeof(IMyApiModule).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null)
                    throw new InvalidOperationException($"Module '{type.FullName}' must have a public parameterless constructor.");
                if (Activator.CreateInstance(type) is not IMyApiModule module)
                    throw new InvalidOperationException($"Module '{type.FullName}' could not be created.");
                modules.Add(module);
            }
        }

        return modules;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            var details = string.Join("; ", ex.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message));
            throw new InvalidOperationException($"Module assembly '{assembly.GetName().Name}' has unresolved types: {details}", ex);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (_debounce is null) return;
        _debounce.Change(500, Timeout.Infinite);
    }

    private void ReloadSafely()
    {
        try { Reload(); }
        catch (Exception ex) { Console.Error.WriteLine($"[MyAPI] Module reload failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
        lock (_gate)
        {
            _catalog.ReplaceAll(Array.Empty<IMyApiModule>());
            _loadContext?.Unload();
            _loadContext = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ModuleHost));
    }

    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly HashSet<string> _loadedRoots = new(StringComparer.OrdinalIgnoreCase);

        public ModuleLoadContext(string directory) : base($"MyAPI.Modules.{Guid.NewGuid():N}", isCollectible: true) => _directory = directory;

        public Assembly LoadRoot(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!_loadedRoots.Add(name)) return LoadFromAssemblyName(new AssemblyName(name));
            return LoadFromAssemblyName(new AssemblyName(name));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IMyApiModule).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase))
                return typeof(IMyApiModule).Assembly;

            var path = Path.Combine(_directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
        }
    }
}
