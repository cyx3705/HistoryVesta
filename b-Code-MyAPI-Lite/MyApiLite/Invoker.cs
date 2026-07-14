using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MyApiLite;

/// <summary>参数绑定与反射调用：GET 走 query，POST 走 JSON body（body 缺的字段回落到 query）</summary>
internal static class Invoker
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<object?[]> BindArgsAsync(MethodInfo mi, HttpContext ctx)
    {
        var ps = mi.GetParameters();
        if (ps.Length == 0) return Array.Empty<object?>();

        Dictionary<string, JsonElement>? body = null;
        if (HttpMethods.IsPost(ctx.Request.Method) && (ctx.Request.ContentLength ?? 0) > 0)
        {
            try
            {
                var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body, JsonOpts);
                if (raw != null) body = new Dictionary<string, JsonElement>(raw, StringComparer.OrdinalIgnoreCase);
            }
            catch { throw new ArgumentException("请求体不是合法的 JSON 对象"); }
        }

        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];

            if (p.Name != null && body != null && body.TryGetValue(p.Name, out var je))
            {
                try { args[i] = je.Deserialize(p.ParameterType, JsonOpts); }
                catch { throw new ArgumentException($"参数 {p.Name} JSON 转换失败，期望类型 {p.ParameterType.Name}"); }
                continue;
            }

            string? qv = p.Name != null ? ctx.Request.Query[p.Name].FirstOrDefault() : null;
            if (qv != null)
            {
                try
                {
                    var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                    args[i] = t.IsEnum ? Enum.Parse(t, qv, ignoreCase: true)
                                       : Convert.ChangeType(qv, t, CultureInfo.InvariantCulture);
                }
                catch { throw new ArgumentException($"参数 {p.Name} 类型转换失败，期望类型 {p.ParameterType.Name}"); }
                continue;
            }

            args[i] = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
        }
        return args;
    }

    /// <summary>从 JSON 键值字典绑定参数（MCP tools/call 用；键大小写不敏感）</summary>
    public static object?[] BindFromDict(MethodInfo mi, Dictionary<string, JsonElement>? dict)
    {
        var ps = mi.GetParameters();
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];

            if (p.Name != null && dict != null && dict.TryGetValue(p.Name, out var je))
            {
                try { args[i] = je.Deserialize(p.ParameterType, JsonOpts); }
                catch { throw new ArgumentException($"参数 {p.Name} JSON 转换失败，期望类型 {p.ParameterType.Name}"); }
                continue;
            }

            args[i] = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
        }
        return args;
    }

    public static async Task<object?> InvokeAsync(MethodInfo mi, object? target, object?[] args)
    {
        object? result = mi.Invoke(target, args);

        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            result = task.GetType().GetProperty("Result")?.GetValue(task);
            if (result?.GetType().Name == "VoidTaskResult") result = null;
        }
        return result;
    }
}
