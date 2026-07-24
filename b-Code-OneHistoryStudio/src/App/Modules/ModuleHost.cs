using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using AppShell.Core.Commands;
using AppShell.Core.Logging;

namespace OneHistoryStudio.Modules;

public sealed record ModuleMeta(
    string ModuleName, string Description, string Author, string Version,
    bool Open, string AssemblyFile, int CommandCount, string Slot = "");

/// <summary>
/// 模块宿主(MD-01~07):进程内移植自 b-Code-MyAPI-Lite 的 ModuleHost/Invoker 机制(D3)。
/// 监听 Modules 目录,把含 BaseVariable.ModuleInfoBase 子类(鸭子类型,MD-02)的 DLL
/// 的业务方法注册为总线指令「模块名.方法名」;XML 注释成为帮助文本(MD-03)。
/// DLL 从内存流加载不锁文件,覆盖/新增/删除触发整体热重载(800ms 防抖,MD-01);
/// 与内置指令重名的方法拒绝注册并告警(MD-07);模块异常由总线兜底(MD-06)。
/// 与 Lite 的差异:端点表 → CommandRegistry 注册/注销;Console → IShellLog;
/// 注册表变更经 WPF Dispatcher 序列化到 UI 线程。
/// </summary>
public sealed class ModuleHost : IDisposable
{
    private readonly IShellLog _log;
    private readonly object _reloadLock = new();
    private string _dir;
    private CommandRegistry? _registry;
    private Snapshot _current = Snapshot.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ModuleHost(string modulesDir, IShellLog log)
    {
        _dir = modulesDir;
        _log = log;
    }

    public string ModulesDirectory => _dir;

    public IReadOnlyList<ModuleMeta> Modules => _current.Modules;

    /// <summary>每次整体重载完成后触发(在重载线程上);MD-08 面板同步等旁路逻辑挂此处。</summary>
    public event Action? ReloadCompleted;

    /// <summary>接入指令注册表(ShellWindow 创建后调用,再 Start)。</summary>
    public void Attach(CommandRegistry registry) => _registry = registry;

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        Reload();
        StartWatcher();
        _log.Info("module", $"正在监听模块目录: {_dir}");
    }

    /// <summary>module.dir path=:切换模块目录并整体重载。</summary>
    public void ChangeDirectory(string newDir)
    {
        _watcher?.Dispose();
        _watcher = null;
        _dir = newDir;
        Directory.CreateDirectory(_dir);
        Reload();
        StartWatcher();
        _log.Info("module", $"模块目录已切换: {_dir}");
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                           | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = true, // V2.2 MH-03:模块槽子目录同样触发热重载
        };
        _watcher.Created += (_, e) => OnFileEvent(e.Name);
        _watcher.Changed += (_, e) => OnFileEvent(e.Name);
        _watcher.Deleted += (_, e) => OnFileEvent(e.Name);
        _watcher.Renamed += (_, e) => OnFileEvent(e.Name);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(string? file)
    {
        // 只关心模块本体、XML 注释文档与模块旁面板(MD-08)
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        if (ext is ".dll" or ".xml"
            || (file?.EndsWith(".panel.json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            ScheduleReload(file);
        }
    }

    /// <summary>文件事件防抖:拷贝大 DLL 会触发多次 Changed,静默 800ms 后才真正重载。</summary>
    private void ScheduleReload(string? file)
    {
        _log.Info("module", $"检测到模块变化: {file ?? "?"},准备热重载...");
        _debounce ??= new Timer(_ =>
        {
            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                _log.Error("module", $"热重载失败: {ex.Message}");
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(800, Timeout.Infinite);
    }

    /// <summary>整体重载:构建新快照 → 注册表换血(UI 线程) → 卸载旧 ALC。</summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            var next = Build();

            // 注册表是 UI 线程消费的普通字典,变更必须经 Dispatcher 序列化
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return; // 应用退出中

            dispatcher.Invoke(() => SwapRegistrations(_current, next));

            var old = _current;
            _current = next;
            foreach (var alc in old.Contexts)
                alc.Unload();

            _log.Info("module",
                $"模块装载完成: {next.Modules.Count} 个模块,{next.RegisteredNames.Count} 条指令");
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        ReloadCompleted?.Invoke();
    }

    private void SwapRegistrations(Snapshot old, Snapshot next)
    {
        if (_registry == null)
            return;

        foreach (var name in old.RegisteredNames)
            _registry.Unregister(name);

        foreach (var (descriptor, moduleName) in next.PendingCommands)
        {
            try
            {
                _registry.Register(descriptor, $"module:{moduleName}");
                next.RegisteredNames.Add(descriptor.Name);
                next.CountCommand(moduleName);
            }
            catch (InvalidOperationException ex)
            {
                // MD-07:与内置指令(或其他模块)重名,仅拒绝该方法,不静默覆盖
                _log.Warn("module", $"模块 {moduleName} 的指令被拒绝注册: {ex.Message}");
            }
        }

        next.FinalizeMetas();
    }

    // ---------------------------------------------------------------- 快照构建

    private Snapshot Build()
    {
        var snap = new Snapshot();
        if (!Directory.Exists(_dir))
            return snap;

        // 根目录平铺 DLL(V2-M3 既有行为):共享一个 ALC
        LoadGroup(snap, _dir, slot: "");

        // 模块槽(V2.2 MH-01):每个一级子目录一个独立可回收 ALC,
        // 槽内依赖只在槽内解析(MH-02),槽间同名依赖不同版互不冲突
        foreach (var slotDir in Directory.GetDirectories(_dir))
            LoadGroup(snap, slotDir, Path.GetFileName(slotDir));

        return snap;
    }

    private void LoadGroup(Snapshot snap, string dir, string slot)
    {
        var dlls = Directory.GetFiles(dir, "*.dll");
        if (dlls.Length == 0)
            return;

        var alc = new ModuleLoadContext(dir);
        snap.Contexts.Add(alc);

        foreach (var dll in dlls)
        {
            try
            {
                var asm = LoadAssembly(alc, dll);
                ScanAssembly(snap, asm, dll, slot);
            }
            catch (Exception ex)
            {
                // MD-06:坏 DLL 只自身下线并告警,不影响宿主与其他模块
                _log.Warn("module", $"跳过 {(slot.Length > 0 ? slot + "/" : "")}{Path.GetFileName(dll)}: {ex.Message}");
            }
        }
    }

    private static Assembly LoadAssembly(ModuleLoadContext alc, string dll)
    {
        // 按程序集名加载可命中 ALC 缓存,避免同名程序集(被其他模块作为依赖引用过)二次加载
        try
        {
            return alc.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(dll)));
        }
        catch
        {
            return alc.LoadFromStream(new MemoryStream(ReadFileWithRetry(dll)));
        }
    }

    private void ScanAssembly(Snapshot snap, Assembly asm, string dllPath, string slot)
    {
        var fileName = Path.GetFileName(dllPath);

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        // 只托管含 ModuleInfoBase 子类的程序集;其余 DLL 视为纯依赖库(MD-02)
        var infoTypes = types.Where(t => t.IsPublic && !t.IsAbstract && IsModuleInfo(t)).ToList();
        if (infoTypes.Count == 0)
            return;

        var docs = XmlDocs.TryLoad(dllPath, _log);

        foreach (var infoType in infoTypes)
        {
            object info;
            try
            {
                info = Activator.CreateInstance(infoType)!;
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"实例化 {infoType.FullName} 失败: {ex.Message}");
                continue;
            }

            if (GetProp(info, "Enabled") is false)
                continue;

            var open = GetProp(info, "Open") is true;
            var moduleName = GetProp(info, "ModuleName") as string ?? asm.GetName().Name ?? fileName;
            snap.Metas.Add((moduleName,
                GetProp(info, "Description") as string ?? "",
                GetProp(info, "Author") as string ?? "",
                GetProp(info, "Version") as string ?? "",
                open, fileName, slot));

            if (open)
            {
                foreach (var t in types.Where(t =>
                             t.IsClass && t.IsPublic && !t.IsAbstract
                             && !t.IsGenericTypeDefinition && !IsModuleInfo(t)))
                    CollectType(snap, moduleName, t, docs);
            }
            else if (GetProp(info, "MainClassType") is Type main)
            {
                CollectType(snap, moduleName, main, docs);
            }

            _log.Info("module",
                $"✓ 模块 {moduleName} {GetProp(info, "Version")} ({(open ? "全暴露" : "精准暴露")}) " +
                $"← {(slot.Length > 0 ? slot + "/" : "")}{fileName}");
        }
    }

    /// <summary>把一个业务类的公共方法收集为待注册指令(MD-03/04)。</summary>
    private void CollectType(Snapshot snap, string moduleName, Type type, XmlDocs? docs)
    {
        var ns = type.Namespace ?? "Global";
        foreach (var m in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.IsGenericMethodDefinition)
                continue;

            var commandName = $"{moduleName}.{m.Name}";
            if (snap.PendingCommands.Any(p =>
                    p.Descriptor.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Warn("module", $"模块 {moduleName} 内方法重名,跳过 {type.Name}.{m.Name}");
                continue;
            }

            var (summary, paramDocs) = docs?.ForMethod(ns, type.Name, m.Name) ?? ("", EmptyDocs);
            snap.PendingCommands.Add((BuildDescriptor(snap, commandName, moduleName, type, m, summary, paramDocs), moduleName));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDocs = new Dictionary<string, string>();

    private static CommandDescriptor BuildDescriptor(
        Snapshot snap, string commandName, string moduleName, Type type, MethodInfo method,
        string summary, IReadOnlyDictionary<string, string> paramDocs)
    {
        var parameters = new List<ParameterSpec>();
        var example = commandName;
        var ps = method.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var paramType = MapType(p.ParameterType);
            parameters.Add(new ParameterSpec
            {
                Name = p.Name ?? $"arg{i}",
                Description = paramDocs.GetValueOrDefault(p.Name ?? "", ""),
                Type = paramType,
                Required = !p.HasDefaultValue,
                Default = p.HasDefaultValue ? DefaultText(p.DefaultValue) : null,
                Position = i,
            });
            example += $" {p.Name}={SampleValue(paramType)}";
        }

        return new CommandDescriptor
        {
            Name = commandName,
            Summary = summary.Length > 0 ? summary : $"{moduleName} 模块 {type.Name}.{method.Name} 方法",
            Example = example,
            Parameters = parameters,
            Handler = async ctx =>
            {
                var args = BindArgs(method, ctx);
                var target = method.IsStatic ? null : snap.GetInstance(type);
                object? result;
                try
                {
                    result = method.Invoke(target, args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // MD-06:剥掉反射包装,把模块自身异常清晰上抛(总线红字兜底)
                    throw new InvalidOperationException($"模块方法异常: {tie.InnerException.Message}");
                }

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result?.GetType().Name == "VoidTaskResult")
                        result = null;
                }

                return CommandResult.Ok(Render(result), result);
            },
        };
    }

    // ---------------------------------------------------------------- 参数绑定与结果渲染(MD-04)

    private static object?[] BindArgs(MethodInfo method, CommandContext ctx)
    {
        var ps = method.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var raw = p.Name != null ? ctx.GetString(p.Name) : null;
            if (raw == null)
            {
                args[i] = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
                continue;
            }

            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            try
            {
                args[i] = t == typeof(string) ? raw
                    : t.IsEnum ? Enum.Parse(t, raw, ignoreCase: true)
                    : t == typeof(bool) ? ctx.GetBool(p.Name!)
                    : Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
            }
            catch
            {
                throw new ArgumentException($"参数 {p.Name} 类型转换失败,期望 {t.Name},实际 \"{raw}\"");
            }
        }

        return args;
    }

    private static readonly JsonSerializerOptions RenderOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Render(object? result) => result switch
    {
        null => "(无返回值)",
        string s => s,
        _ when result.GetType().IsPrimitive || result is decimal || result is DateTime
            => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "",
        _ => JsonSerializer.Serialize(result, RenderOpts),
    };

    private static ParamType MapType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            return ParamType.Int;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return ParamType.Double;
        if (t == typeof(bool))
            return ParamType.Bool;
        return ParamType.String;
    }

    private static string SampleValue(ParamType t) => t switch
    {
        ParamType.Int => "1",
        ParamType.Double => "1.5",
        ParamType.Bool => "true",
        _ => "文本",
    };

    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- 鸭子类型契约(MD-02)

    /// <summary>按基类全名判断,模块编译时引用哪个版本的 BaseVariable.dll 都能识别。</summary>
    private static bool IsModuleInfo(Type t)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType)
        {
            if (b.FullName == "BaseVariable.ModuleInfoBase")
                return true;
        }

        return false;
    }

    private static object? GetProp(object o, string name)
    {
        try
        {
            return o.GetType().GetProperty(name)?.GetValue(o);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>文件可能正在拷贝中,重试读取。</summary>
    internal static byte[] ReadFileWithRetry(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(300);
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    // ---------------------------------------------------------------- 快照与加载上下文

    /// <summary>一次加载的不可变快照:可卸载 ALC 组(根 + 每槽一个) + 指令表 + 单例缓存。热重载整体替换。</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();

        /// <summary>本快照持有的全部加载上下文(MH-01:根平铺一个 + 每模块槽一个)。</summary>
        public List<AssemblyLoadContext> Contexts { get; } = new();

        /// <summary>扫描出的待注册指令(注册在 UI 线程完成,冲突者被剔除)。</summary>
        public List<(CommandDescriptor Descriptor, string ModuleName)> PendingCommands { get; } = new();

        /// <summary>实际注册成功的指令名(热重载时按此注销)。</summary>
        public List<string> RegisteredNames { get; } = new();

        /// <summary>模块元信息(module.list);CommandCount 在注册完成后定稿。</summary>
        public List<(string Name, string Desc, string Author, string Version, bool Open, string File, string Slot)> Metas { get; } = new();

        public List<ModuleMeta> Modules { get; } = new();

        private readonly Dictionary<string, int> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _instances = new();

        public object GetInstance(Type t) => _instances.GetOrAdd(t, x => Activator.CreateInstance(x)!);

        public void CountCommand(string moduleName)
            => _commandCounts[moduleName] = _commandCounts.GetValueOrDefault(moduleName) + 1;

        public void FinalizeMetas()
        {
            Modules.Clear();
            foreach (var (name, desc, author, version, open, file, slot) in Metas)
                Modules.Add(new ModuleMeta(name, desc, author, version, open, file,
                    _commandCounts.GetValueOrDefault(name), slot));
        }
    }

    /// <summary>可回收的加载上下文:模块及其依赖全部从内存流加载,不锁磁盘文件。</summary>
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public ModuleLoadContext(string dir)
            : base($"Modules-{DateTime.Now:HHmmssfff}", isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 模块目录里有同名 DLL 就从内存加载;否则返回 null 回落到默认上下文(框架程序集)
            var path = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(path))
                return LoadFromStream(new MemoryStream(ReadFileWithRetry(path)));
            return null;
        }
    }
}

/// <summary>
/// 读取编译器生成的 XML 文档文件(模块 DLL 同目录同名 .xml),
/// 把方法的 &lt;summary&gt; / &lt;param&gt; 注释变成指令帮助文本(MD-03)。
/// </summary>
internal sealed class XmlDocs
{
    private readonly List<XElement> _members;

    private XmlDocs(List<XElement> members) => _members = members;

    public static XmlDocs? TryLoad(string dllPath, IShellLog log)
    {
        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        if (!File.Exists(xmlPath))
            return null;
        try
        {
            var doc = XDocument.Load(xmlPath);
            return new XmlDocs(doc.Descendants("member").ToList());
        }
        catch (Exception ex)
        {
            log.Warn("module", $"读取 XML 注释失败 ({Path.GetFileName(xmlPath)}): {ex.Message}");
            return null;
        }
    }

    public (string Summary, IReadOnlyDictionary<string, string> Params) ForMethod(string ns, string cls, string method)
    {
        // 成员名形如 M:Ns.Cls.Method 或 M:Ns.Cls.Method(System.Int32,...)
        var exact = $"M:{ns}.{cls}.{method}";
        var member = _members.FirstOrDefault(m =>
        {
            var n = m.Attribute("name")?.Value;
            return n == exact || (n != null && n.StartsWith(exact + "(", StringComparison.Ordinal));
        });
        if (member == null)
            return ("", new Dictionary<string, string>());

        var summary = Clean(member.Element("summary")?.Value);
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
