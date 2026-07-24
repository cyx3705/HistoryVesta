using BaseRegister;                    
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Myapi.Controller;

[ApiController]
[Route("api/{ns}/{cls}/{method}")]
public class DynamicController : ControllerBase
{
    // 单例注册缓存
    private static readonly ConcurrentDictionary<string, object> _registeredSingletons = new();

    /// <summary>
    /// 注册模块的核心业务类单例（由 ScanAndRegister 在启动时调用）
    /// </summary>
    public static void RegisterInstance(string fullClassName, object instance)
    {
        if (string.IsNullOrWhiteSpace(fullClassName) || instance == null)
            return;

        _registeredSingletons[fullClassName] = instance;
        Console.WriteLine($"[DynamicController] 单例注册成功 → {fullClassName}");
    }

    [HttpGet]
    public Task<IActionResult> GetAsync(string ns, string cls, string method)
        => ExecuteAsync(ns, cls, method, isPost: false);

    [HttpPost]
    public Task<IActionResult> PostAsync(string ns, string cls, string method)
        => ExecuteAsync(ns, cls, method, isPost: true);

    /// <summary>
    /// 核心执行逻辑 - 完全依赖 DynamicEndpointRegistry，不再自行扫描程序集
    /// </summary>
    private async Task<IActionResult> ExecuteAsync(string ns, string cls, string method, bool isPost)
    {
        string fullPath = $"/api/{ns}/{cls}/{method}";

        // ==================== 白名单检查（核心安全门） ====================
        var registeredEndpoint = DynamicEndpointRegistry.AllEndpoints.FirstOrDefault(e =>
            string.Equals(e.Namespace, ns, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Class, cls, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase));

        if (registeredEndpoint == null)
        {
            return NotFound(new
            {
                Error = $"接口未注册: {fullPath}",
                Message = "该接口不在当前允许暴露的动态接口列表中"
            });
        }

        string fullClassName = $"{ns}.{cls}";

        try
        {
            // 查找目标类型
            Type? targetType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullClassName))
                .FirstOrDefault(t => t != null);

            if (targetType == null)
                return NotFound(new { Error = $"未找到类型：{fullClassName}" });

            // 查找目标方法（更严格，只查找本类声明的方法）
            MethodInfo? targetMethod = targetType.GetMethod(method,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            if (targetMethod == null)
                return NotFound(new { Error = $"未找到方法：{method}" });

            // 参数解析
            object?[] paramValues = isPost
                ? await ParseBodyParametersAsync(targetMethod)
                : await ParseParametersAsync(targetMethod);

            // 获取实例（优先使用已注册的单例）
            object instance = _registeredSingletons.TryGetValue(fullClassName, out var registeredInstance)
                ? registeredInstance
                : Activator.CreateInstance(targetType)!;

            // 执行方法并处理异步
            object? result = await InvokeMethodAsync(targetMethod, instance, paramValues);

            // 如果方法返回 IActionResult，则直接返回，否则包装成 Ok
            return result is IActionResult actionResult ? actionResult : Ok(result);
        }
        catch (TargetInvocationException ex)
        {
            var realEx = ex.InnerException ?? ex;
            return StatusCode(500, new
            {
                Error = $"方法执行失败：{realEx.Message}",
                InnerError = realEx.InnerException?.Message,
                Path = fullPath
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                Error = $"执行失败：{ex.Message}",
                Path = fullPath
            });
        }
    }

    /// <summary>
    /// 统一的方法调用（支持 Task 和 Task<>）
    /// </summary>
    private static async Task<object?> InvokeMethodAsync(MethodInfo method, object instance, object?[]? parameters)
    {
        var result = method.Invoke(instance, parameters);

        if (result is Task task)
        {
            await task.ConfigureAwait(false);

            // 处理 Task<T>
            var resultProperty = task.GetType().GetProperty("Result");
            return resultProperty?.GetValue(task);
        }

        return result;
    }

    #region 参数解析（保留你原来的逻辑，仅做少量清理）
    private async Task<object?[]> ParseParametersAsync(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var paramValues = new List<object?>();

        foreach (var param in parameters)
        {
            if (param.Name == null)
            {
                paramValues.Add(param.HasDefaultValue ? param.DefaultValue : null);
                continue;
            }

            var value = Request.Query[param.Name].FirstOrDefault();
            if (string.IsNullOrEmpty(value))
            {
                paramValues.Add(param.HasDefaultValue ? param.DefaultValue : null);
                continue;
            }

            try
            {
                var converted = Convert.ChangeType(value, param.ParameterType, CultureInfo.InvariantCulture);
                paramValues.Add(converted);
            }
            catch
            {
                throw new ArgumentException($"参数 {param.Name} 类型转换失败，期望类型：{param.ParameterType.Name}");
            }
        }

        return paramValues.ToArray();
    }

    private async Task<object?[]> ParseBodyParametersAsync(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var paramValues = new List<object?>();

        string body;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
            Request.Body.Position = 0; // 重置流位置
        }

        var bodyData = string.IsNullOrEmpty(body)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(body);

        foreach (var param in parameters)
        {
            if (param.Name == null)
            {
                paramValues.Add(param.HasDefaultValue ? param.DefaultValue : null);
                continue;
            }

            if (bodyData != null && bodyData.TryGetValue(param.Name, out var value))
            {
                try
                {
                    // 简单 JSON 转换
                    var json = JsonSerializer.Serialize(value);
                    var converted = JsonSerializer.Deserialize(json, param.ParameterType);
                    paramValues.Add(converted);
                }
                catch
                {
                    throw new ArgumentException($"参数 {param.Name} JSON 转换失败，期望类型：{param.ParameterType.Name}");
                }
            }
            else
            {
                paramValues.Add(param.HasDefaultValue ? param.DefaultValue : null);
            }
        }

        return paramValues.ToArray();
    }
    #endregion
}
