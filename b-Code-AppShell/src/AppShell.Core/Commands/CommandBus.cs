using System.Globalization;
using System.Text.RegularExpressions;
using AppShell.Core.Logging;

namespace AppShell.Core.Commands;

/// <summary>
/// 指令总线(§5.2 生命周期):
/// 解析 → 查注册表 → 参数校验 → 拦截(二次确认) → 执行(可 UI 线程编组) → 结果回显。
/// 任何指令抛出的异常都被捕获:程序不崩溃,错误进控制台与日志文件(P0)。
/// 指令回显与普通日志共用 IShellLog 管道、不同类别(L-03):
///   回显 = "cmd:来源",结果 = "cmd:result",进度 = "cmd:progress"。
/// </summary>
public sealed class CommandBus
{
    /// <summary>回显类别前缀;控制台按此前缀识别指令行。</summary>
    public const string EchoCategoryPrefix = "cmd:";

    public const string ResultCategory = "cmd:result";
    public const string ProgressCategory = "cmd:progress";

    private readonly CommandRegistry _registry;
    private readonly IShellLog _log;

    public CommandBus(CommandRegistry registry, IShellLog log)
    {
        _registry = registry;
        _log = log;
    }

    public CommandRegistry Registry => _registry;

    /// <summary>
    /// Validates a command text against the current registry without routing, logging, confirmation, or execution.
    /// UI surfaces use this to reject stale menu references at construction time.
    /// </summary>
    public string? Validate(string text)
    {
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException ex)
        {
            return $"语法错误: {ex.Message}";
        }

        if (!_registry.TryGet(parsed.Name, out var descriptor))
            return $"未知指令: {parsed.Name}";

        return BindArguments(descriptor, parsed, out _);
    }

    /// <summary>二次确认通道;未注入时带确认位的指令一律拒绝执行(安全缺省)。</summary>
    public IConfirmationService? Confirmation { get; set; }

    /// <summary>需要按客户端来源选择确认通道时使用；设置后优先于 Confirmation。</summary>
    public Func<CommandContext, string, bool>? ConfirmationRouter { get; set; }

    /// <summary>UI 线程上下文;RequiresUiThread 的指令经此编组。</summary>
    public SynchronizationContext? UiContext { get; set; }

    /// <summary>
    /// 前端命令中继。服务宿主为其注入传输实现；未连接前端时保持 null，
    /// 总线返回明确失败结果，不等待网络超时。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? FrontendExecutor { get; set; }

    /// <summary>
    /// 客户端模式下的远程总线。ShouldUseRemote 返回 true 时整条命令交给服务，
    /// 服务经前端中继发回的 UI 命令可用来源标签绕过此路由并在本地执行。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? RemoteExecutor { get; set; }

    public Func<string, bool>? ShouldUseRemote { get; set; }

    /// <summary>按命令文本和来源决定是否走远端；设置后优先于仅按来源的兼容委托。</summary>
    public Func<string, string, bool>? ShouldUseRemoteCommand { get; set; }

    /// <summary>每条指令执行完毕后触发(状态栏摘要,S-03);在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

    /// <summary>
    /// 执行一行指令文本。source 为来源标签(C-01):UI / 手动 / 脚本:文件名 / layout。
    /// 返回值在指令(含异步长任务)完成后才落定;方法自身不抛异常。
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(
        string text,
        string source,
        CancellationToken cancellation = default)
    {
        // 1. 回显
        var trimmed = text.Trim();
        var displayText = RedactSensitiveArguments(trimmed);
        _log.Log(ShellLogLevel.Info, EchoCategoryPrefix + source, displayText);

        CommandResult result;
        try
        {
            result = await ExecuteCoreAsync(trimmed, source, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 最后一道兜底(N-05):总线自身缺陷也不允许击穿宿主
            result = CommandResult.Fail($"总线内部错误: {ex.Message}");
            _log.Log(ShellLogLevel.Error, "cmd", $"总线内部错误: {ex}");
        }

        // 2. 结果回显(错误红色高亮由控制台按级别渲染,C-02)
        _log.Log(
            result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
            ResultCategory,
            (result.Success ? "✓ " : "✗ ") + result.Message);

        Executed?.Invoke(displayText, source, result);
        return result;
    }

    private static string RedactSensitiveArguments(string text)
        => Regex.Replace(
            text,
            "(?i)(\\b(?:code|token|password|passwd|secret)\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s]+)",
            "$1[REDACTED]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    private async Task<CommandResult> ExecuteCoreAsync(
        string text,
        string source,
        CancellationToken cancellation)
    {
        var remote = RemoteExecutor;
        if (remote != null
            && (ShouldUseRemoteCommand?.Invoke(text, source)
                ?? ShouldUseRemote?.Invoke(source)
                ?? true))
            return await remote(text, source, cancellation).ConfigureAwait(false);

        // 解析
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException ex)
        {
            return CommandResult.Fail($"语法错误: {ex.Message}");
        }

        // 查注册表(未知指令给出候选,§5.2 P1)
        if (!_registry.TryGet(parsed.Name, out var descriptor))
        {
            var suggestions = _registry.Suggest(parsed.Name);
            var hint = suggestions.Count > 0
                ? $"\n你是不是想输入: {string.Join(" / ", suggestions)} ?"
                : "\n输入 help 查看全部指令。";
            return CommandResult.Fail($"未知指令: {parsed.Name}{hint}");
        }

        // 参数校验
        var bindError = BindArguments(descriptor, parsed, out var values);
        if (bindError != null)
            return CommandResult.Fail($"{bindError}\n{FormatUsage(descriptor)}");

        var progress = new Progress<string>(line =>
            _log.Log(ShellLogLevel.Info, ProgressCategory, line));
        var context = new CommandContext(descriptor, values, source, progress, cancellation);

        // 拦截:二次确认(§5.2;T-08/R-06 危险操作在“手输指令路径”的统一闸口)
        var prompt = descriptor.ConfirmPrompt?.Invoke(context);
        if (prompt != null)
        {
            if (ConfirmationRouter != null)
            {
                if (!ConfirmationRouter(context, prompt))
                    return CommandResult.Fail("已取消(未获确认)");
            }
            else if (Confirmation == null)
                return CommandResult.Fail("该指令需要二次确认,但当前环境没有确认通道,已拒绝执行");
            else if (!Confirmation.Confirm(prompt))
                return CommandResult.Fail("已取消(用户未确认)");
        }

        // 执行(必要时编组 UI 线程)
        try
        {
            if (descriptor.ExecutionSite == CommandExecutionSite.Frontend)
            {
                var frontend = FrontendExecutor;
                return frontend == null
                    ? CommandResult.Fail("前端未连接,请启动应用前端")
                    : await frontend(text, source, cancellation).ConfigureAwait(false);
            }

            if (descriptor.RequiresUiThread && UiContext != null
                && SynchronizationContext.Current != UiContext)
            {
                return await OnUiThreadAsync(() => descriptor.Handler(context)).ConfigureAwait(false);
            }

            return await descriptor.Handler(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            _log.Log(ShellLogLevel.Error, "cmd", $"{descriptor.Name} 执行异常: {ex}");
            return CommandResult.Fail($"{descriptor.Name} 执行异常: {ex.Message}");
        }
    }

    private Task<CommandResult> OnUiThreadAsync(Func<Task<CommandResult>> action)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiContext!.Post(
            async _ =>
            {
                try
                {
                    tcs.SetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            },
            null);
        return tcs.Task;
    }

    // ---------------------------------------------------------------- 参数绑定与校验

    private static string? BindArguments(
        CommandDescriptor descriptor,
        ParsedCommand parsed,
        out IReadOnlyDictionary<string, string> values)
    {
        if (descriptor.AllowUnspecifiedParameters)
        {
            values = parsed.Named.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            return null;
        }

        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values = bound;

        // 位置参数 → 声明了 Position 的参数(按序)
        var positionalSpecs = descriptor.Parameters
            .Where(p => p.Position.HasValue)
            .OrderBy(p => p.Position!.Value)
            .ToList();
        if (parsed.Positionals.Count > positionalSpecs.Count)
            return $"多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positionalSpecs.Count))}";
        for (var i = 0; i < parsed.Positionals.Count; i++)
            bound[positionalSpecs[i].Name] = parsed.Positionals[i];

        // 键=值 参数
        foreach (var (key, value) in parsed.Named)
        {
            var spec = descriptor.Parameters.FirstOrDefault(
                p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                var known = string.Join(" ", descriptor.Parameters.Select(p => p.Name + "="));
                return $"未知参数: {key}=" + (known.Length > 0 ? $"(可用: {known})" : "(该指令不接受参数)");
            }

            bound[spec.Name] = value;
        }

        // 必填与类型
        foreach (var spec in descriptor.Parameters)
        {
            if (!bound.TryGetValue(spec.Name, out var value))
            {
                if (spec.Required)
                    return $"缺少必填参数: {spec.Name}=";
                continue;
            }

            var typeError = spec.Type switch
            {
                ParamType.Int when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为整数,实际: {value}",
                ParamType.Double when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为数值,实际: {value}",
                ParamType.Bool when !IsBoolText(value)
                    => $"参数 {spec.Name} 应为 true/false,实际: {value}",
                _ => null,
            };
            if (typeError != null)
                return typeError;

            if (spec.AllowedValues is { Length: > 0 }
                && !spec.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"参数 {spec.Name} 取值应为 {string.Join("/", spec.AllowedValues)},实际: {value}";
            }
        }

        return null;
    }

    private static bool IsBoolText(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("false", StringComparison.OrdinalIgnoreCase)
           || value is "1" or "0"
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value.Equals("no", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>用法行,如 "用法: win.dock name= pos=left/right/top/bottom/tab [target=] [ratio=]"。</summary>
    public static string FormatUsage(CommandDescriptor d)
    {
        var parts = d.Parameters.Select(p =>
        {
            var core = p.AllowedValues is { Length: > 0 }
                ? $"{p.Name}={string.Join("/", p.AllowedValues)}"
                : $"{p.Name}=";
            return p.Required ? core : $"[{core}]";
        });
        return $"用法: {d.Name} {string.Join(" ", parts)}".TrimEnd();
    }
}
