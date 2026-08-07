namespace AppShell.Core.Commands;

/// <summary>
/// 指令注册表(§5.3):框架内置组与派生应用自定义指令并入同一张表,
/// help 自动收录;名称冲突在注册时立即报错,禁止静默覆盖(P0)。
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Provides this AppShell public contract member.</summary>
    public event Action? Changed;

    /// <summary>Provides this AppShell public contract member.</summary>
    public void Register(CommandDescriptor descriptor, string source = "framework")
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("指令来源不能为空", nameof(source));
        lock (_gate)
        {
            if (!_commands.TryAdd(descriptor.Name, descriptor))
                throw new InvalidOperationException($"指令名冲突: {descriptor.Name} 已注册,禁止覆盖(§5.3)");
            _sources[descriptor.Name] = source.Trim();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 注销指令(模块热重载场景:模块 DLL 下线时其指令域随之移除)。
    /// 调用方须只注销自己注册过的名称;存在则移除并返回 true。
    /// [基线 0.4.2 新增,由派生应用 OneHistoryStudio V2-M3 反哺]
    /// </summary>
    public bool Unregister(string name)
    {
        bool removed;
        lock (_gate)
        {
            _sources.Remove(name);
            removed = _commands.Remove(name);
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>Provides this AppShell public contract member.</summary>
    public bool TryGet(string name, out CommandDescriptor descriptor)
    {
        lock (_gate)
            return _commands.TryGetValue(name, out descriptor!);
    }

    /// <summary>返回注册来源：framework / app / module:&lt;name&gt;。</summary>
    public string GetSource(string name)
    {
        lock (_gate)
            return _sources.GetValueOrDefault(name, "framework");
    }

    /// <summary>全部指令,按名称排序(help 列表)。</summary>
    public IReadOnlyList<CommandDescriptor> All()
    {
        lock (_gate)
            return _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 从完整命令名提取一级域。无点号命令本身也是保留域，避免模块以
    /// <c>help.foo</c> 或 <c>future.list</c> 的形式绕过根命令/现有域冲突检查。
    /// </summary>
    public static IReadOnlySet<string> DomainsOf(IEnumerable<string> commandNames)
    {
        ArgumentNullException.ThrowIfNull(commandNames);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in commandNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var trimmed = name.Trim();
            var dot = trimmed.IndexOf('.');
            domains.Add(dot > 0 ? trimmed[..dot] : trimmed);
        }

        return domains;
    }

    /// <summary>
    /// 未知指令时给出最接近的候选(§5.2 P1:“你是不是想输入…”)。
    /// 规则:编辑距离 ≤ 2,或同域(点号前缀相同)的全部动作。
    /// </summary>
    public IReadOnlyList<string> Suggest(string unknownName)
    {
        var candidates = new List<(string Name, int Distance)>();
        var dot = unknownName.IndexOf('.');
        var domain = dot > 0 ? unknownName[..(dot + 1)] : null;

        foreach (var name in All().Select(command => command.Name))
        {
            var d = Levenshtein(unknownName.ToLowerInvariant(), name);
            if (d <= 2)
                candidates.Add((name, d));
            else if (domain != null && name.StartsWith(domain, StringComparison.OrdinalIgnoreCase))
                candidates.Add((name, 3));
        }

        return candidates
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name)
            .Take(5)
            .ToList();
    }

    private static int Levenshtein(string a, string b)
    {
        var m = a.Length;
        var n = b.Length;
        var prev = new int[n + 1];
        var cur = new int[n + 1];
        for (var j = 0; j <= n; j++)
            prev[j] = j;

        for (var i = 1; i <= m; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[n];
    }
}
