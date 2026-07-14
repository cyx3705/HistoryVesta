using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MyApiLite;

/// <summary>对外可见的端点元信息（/api/meta/endpoints 与 MCP tools/list 共用）</summary>
public sealed record EndpointInfo(
    string Module, string Namespace, string Class, string Method,
    string FullPath, string McpTool, string Description);

public sealed record ModuleMeta(string ModuleName, string Description, string Author, string Version, bool Open, string AssemblyFile);

/// <summary>端点完整条目：元信息 + 反射调用所需的 MethodInfo / 类型 / 参数注释</summary>
internal sealed record EndpointEntry(
    EndpointInfo Info, MethodInfo Method, Type Type,
    IReadOnlyDictionary<string, string> ParamDocs);

/// <summary>
/// 一次加载的不可变快照：一个可卸载的 AssemblyLoadContext + 端点表 + 单例缓存。
/// 热重载时整体替换快照，旧的 ALC 随即卸载。
/// </summary>
internal sealed class Snapshot
{
    public static readonly Snapshot Empty = new();

    public AssemblyLoadContext? Alc;
    public List<EndpointEntry> Entries { get; } = new();
    public List<ModuleMeta> Modules { get; } = new();
    /// <summary>按 "命名空间/类/方法" 查找（HTTP 路由用）</summary>
    public Dictionary<string, EndpointEntry> Lookup { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>按 MCP 工具名查找（tools/call 用）</summary>
    public Dictionary<string, EndpointEntry> ByTool { get; } = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Type, object> _instances = new();
    public object GetInstance(Type t) => _instances.GetOrAdd(t, x => Activator.CreateInstance(x)!);
}

/// <summary>
/// 模块宿主：监听模块目录，把目录里含 ModuleInfoBase 子类的 DLL 托管为 HTTP 接口和 MCP 工具。
/// DLL 从内存流加载，不锁文件，覆盖 / 新增 / 删除 DLL 都会触发整体热重载。
/// 模块旁若有同名 .xml 文档文件（GenerateDocumentationFile 产物），
/// 方法的 &lt;summary&gt; 和 &lt;param&gt; 注释会成为接口描述 / MCP 工具提示词。
/// </summary>
public sealed class ModuleHost : IDisposable
{
    private readonly string _dir;
    private readonly object _reloadLock = new();
    private volatile Snapshot _current = Snapshot.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ModuleHost(string modulesDir) => _dir = modulesDir;

    internal Snapshot Current => _current;
    public IReadOnlyList<ModuleMeta> Modules => _current.Modules;
    public IReadOnlyList<EndpointInfo> Endpoints => _current.Entries.Select(e => e.Info).ToList();

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        Reload();

        _watcher = new FileSystemWatcher(_dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _watcher.Created += (_, e) => OnFileEvent(e.Name);
        _watcher.Changed += (_, e) => OnFileEvent(e.Name);
        _watcher.Deleted += (_, e) => OnFileEvent(e.Name);
        _watcher.Renamed += (_, e) => OnFileEvent(e.Name);
        _watcher.EnableRaisingEvents = true;

        Console.WriteLine($"[ModuleHost] 正在监听模块目录: {_dir}");
    }

    private void OnFileEvent(string? file)
    {
        // 只关心模块本体和它的 XML 注释文档
        string ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        if (ext is ".dll" or ".xml") ScheduleReload(file);
    }

    /// <summary>文件事件防抖：拷贝大 DLL 会触发多次 Changed，静默 800ms 后才真正重载</summary>
    private void ScheduleReload(string? file)
    {
        Console.WriteLine($"[ModuleHost] 检测到模块变化: {file ?? "?"}，准备热重载...");
        _debounce ??= new Timer(_ =>
        {
            try { Reload(); }
            catch (Exception ex) { Console.WriteLine($"[ModuleHost] 热重载失败: {ex.Message}"); }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(800, Timeout.Infinite);
    }

    public void Reload()
    {
        lock (_reloadLock)
        {
            var next = Build();
            var old = _current;
            _current = next;
            old.Alc?.Unload();
            Console.WriteLine($"[ModuleHost] 加载完成: {next.Modules.Count} 个模块, {next.Entries.Count} 个接口");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private Snapshot Build()
    {
        var snap = new Snapshot();
        var dlls = Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.dll") : Array.Empty<string>();
        if (dlls.Length == 0) return snap;

        var alc = new ModuleLoadContext(_dir);
        snap.Alc = alc;

        foreach (var dll in dlls)
        {
            try
            {
                var asm = LoadAssembly(alc, dll);
                ScanAssembly(snap, asm, dll);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleHost] 跳过 {Path.GetFileName(dll)}: {ex.Message}");
            }
        }
        return snap;
    }

    private static Assembly LoadAssembly(ModuleLoadContext alc, string dll)
    {
        // 按程序集名加载可命中 ALC 缓存，避免同名程序集（被其他模块作为依赖引用过）二次加载
        try { return alc.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(dll))); }
        catch { return alc.LoadFromStream(new MemoryStream(ReadFileWithRetry(dll))); }
    }

    private static void ScanAssembly(Snapshot snap, Assembly asm, string dllPath)
    {
        string fileName = Path.GetFileName(dllPath);

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        // 只托管含 ModuleInfoBase 子类的程序集；其余 DLL 视为纯依赖库
        var infoTypes = types.Where(t => t.IsPublic && !t.IsAbstract && IsModuleInfo(t)).ToList();
        if (infoTypes.Count == 0) return;

        var docs = XmlDocs.TryLoad(dllPath);

        foreach (var infoType in infoTypes)
        {
            object info;
            try { info = Activator.CreateInstance(infoType)!; }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleHost] 实例化 {infoType.FullName} 失败: {ex.Message}");
                continue;
            }

            if (GetProp(info, "Enabled") is false) continue;

            bool open = GetProp(info, "Open") is true;
            var meta = new ModuleMeta(
                GetProp(info, "ModuleName") as string ?? asm.GetName().Name ?? fileName,
                GetProp(info, "Description") as string ?? "",
                GetProp(info, "Author") as string ?? "",
                GetProp(info, "Version") as string ?? "",
                open, fileName);
            snap.Modules.Add(meta);

            if (open)
            {
                foreach (var t in types.Where(t =>
                             t.IsClass && t.IsPublic && !t.IsAbstract && !t.IsGenericTypeDefinition && !IsModuleInfo(t)))
                    RegisterType(snap, meta.ModuleName, t, docs);
            }
            else if (GetProp(info, "MainClassType") is Type main)
            {
                RegisterType(snap, meta.ModuleName, main, docs);
            }

            Console.WriteLine($"[ModuleHost] ✓ 模块 {meta.ModuleName} {meta.Version} ({(open ? "全暴露" : "精准暴露")}) ← {fileName}");
        }
    }

    private static void RegisterType(Snapshot snap, string moduleName, Type type, XmlDocs? docs)
    {
        string ns = type.Namespace ?? "Global";
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.IsGenericMethodDefinition) continue;

            string key = $"{ns}/{type.Name}/{m.Name}";
            if (snap.Lookup.ContainsKey(key)) continue;

            var (summary, paramDocs) = docs?.ForMethod(ns, type.Name, m.Name) ?? ("", EmptyDocs);
            string description = summary.Length > 0 ? summary : $"{moduleName} 模块 {type.Name}.{m.Name} 方法";
            string tool = MakeToolName(snap, ns, type.Name, m.Name);

            var entry = new EndpointEntry(
                new EndpointInfo(moduleName, ns, type.Name, m.Name, $"/api/{ns}/{type.Name}/{m.Name}", tool, description),
                m, type, paramDocs);

            snap.Lookup[key] = entry;
            snap.ByTool[tool] = entry;
            snap.Entries.Add(entry);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDocs = new Dictionary<string, string>();

    /// <summary>生成合法且唯一的 MCP 工具名（^[a-zA-Z0-9_-]{1,64}$）</summary>
    private static string MakeToolName(Snapshot snap, string ns, string cls, string method)
    {
        string name = Regex.Replace($"{ns}_{cls}_{method}", "[^a-zA-Z0-9_-]", "_");
        if (name.Length > 60) name = name[^60..].TrimStart('_');
        if (name.Length == 0) name = "tool";
        string candidate = name;
        for (int i = 2; snap.ByTool.ContainsKey(candidate); i++)
            candidate = $"{name}_{i}";
        return candidate;
    }

    /// <summary>按基类全名做鸭子类型判断，模块编译时引用哪个版本的 BaseVariable.dll 都能识别</summary>
    private static bool IsModuleInfo(Type t)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType)
            if (b.FullName == "BaseVariable.ModuleInfoBase") return true;
        return false;
    }

    private static object? GetProp(object o, string name)
    {
        try { return o.GetType().GetProperty(name)?.GetValue(o); }
        catch { return null; }
    }

    /// <summary>文件可能正在拷贝中，重试读取</summary>
    internal static byte[] ReadFileWithRetry(string path)
    {
        for (int i = 0; ; i++)
        {
            try { return File.ReadAllBytes(path); }
            catch (IOException) when (i < 5) { Thread.Sleep(300); }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    /// <summary>可回收的加载上下文：模块及其依赖全部从内存流加载，不锁磁盘文件</summary>
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public ModuleLoadContext(string dir)
            : base($"Modules-{DateTime.Now:HHmmssfff}", isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 模块目录里有同名 DLL 就从内存加载；否则返回 null 回落到默认上下文（框架程序集）
            string path = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(path))
                return LoadFromStream(new MemoryStream(ReadFileWithRetry(path)));
            return null;
        }
    }
}

/// <summary>
/// 读取编译器生成的 XML 文档文件（模块 DLL 同目录同名 .xml），
/// 把方法的 &lt;summary&gt; / &lt;param&gt; 注释变成接口描述和 MCP 工具提示词。
/// </summary>
internal sealed class XmlDocs
{
    private readonly List<XElement> _members;

    private XmlDocs(List<XElement> members) => _members = members;

    public static XmlDocs? TryLoad(string dllPath)
    {
        string xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath)) return null;
        try
        {
            var doc = XDocument.Load(xmlPath);
            return new XmlDocs(doc.Descendants("member").ToList());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModuleHost] 读取 XML 注释失败 ({Path.GetFileName(xmlPath)}): {ex.Message}");
            return null;
        }
    }

    public (string Summary, IReadOnlyDictionary<string, string> Params) ForMethod(string ns, string cls, string method)
    {
        // 成员名形如 M:Ns.Cls.Method 或 M:Ns.Cls.Method(System.Int32,...)
        string exact = $"M:{ns}.{cls}.{method}";
        var member = _members.FirstOrDefault(m =>
        {
            var n = m.Attribute("name")?.Value;
            return n == exact || (n != null && n.StartsWith(exact + "(", StringComparison.Ordinal));
        });
        if (member == null) return ("", new Dictionary<string, string>());

        string summary = Clean(member.Element("summary")?.Value);
        var prms = member.Elements("param")
            .Where(p => p.Attribute("name")?.Value is { Length: > 0 })
            .ToDictionary(p => p.Attribute("name")!.Value, p => Clean(p.Value));
        return (summary, prms);
    }

    private static string Clean(string? text) =>
        text == null ? "" : string.Join(" ",
            text.Split('\r', '\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                .Where(l => l.Length > 0));
}
