using Newtonsoft.Json; 
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

namespace MyAPIFramework.Controllers
{

    /// <summary>
    /// 动态智能控制器（.NET Framework Web API 2 版）
    /// 自动扫描特定类库，动态执行任意类的任意方法
    /// 路由：api/{ns}/{cls}/{method}
    /// </summary>
    [RoutePrefix("api/{ns}/{cls}/{method}")]
    public class DynamicController : ApiController
    {

        private readonly List<string> _scanAssemblyNames = new List<string>
        {
            "MyAPIFramework",
            "TestModule"
        };

        // 构造函数：初始化时预加载程序集
        public DynamicController()
        {
           
        }
        /// <summary>
        /// GET 请求：动态执行方法
        /// </summary>
        [HttpGet]
        [Route("")] // 匹配路由：api/{ns}/{cls}/{method}
        public async Task<IHttpActionResult> GetAsync(string ns, string cls, string method)
        {
            return await ExecuteAsync(ns, cls, method, isPost: false);
        }
        [HttpGet]
        [Route("~/api/test")] // 绝对路由，直接匹配 /api/test
        public IHttpActionResult Test()
        {
            return Ok("控制器正常工作！");
        }
        /// <summary>
        /// POST 请求：动态执行方法
        /// </summary>
        [HttpPost]
        [Route("")] // 匹配路由：api/{ns}/{cls}/{method}
        public async Task<IHttpActionResult> PostAsync(string ns, string cls, string method)
        {
            return await ExecuteAsync(ns, cls, method, isPost: true);
        }

        /// <summary>
        /// 核心执行逻辑：扫描指定类库 → 反射获取类型 → 执行方法
        /// </summary>
        private async Task<IHttpActionResult> ExecuteAsync(string ns, string cls, string method, bool isPost)
        {
            string fullClassName = $"{ns}.{cls}";
            try
            {
                // 1. 查找目标类型
                Type targetType = ScanAssembliesForType(fullClassName);
                if (targetType == null)
                {
                    return Content(HttpStatusCode.NotFound, new { Error = $"未找到类型：{fullClassName}" });
                }

                // 2. 查找目标方法
                MethodInfo targetMethod = targetType.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
                if (targetMethod == null)
                {
                    return Content(HttpStatusCode.NotFound, new { Error = $"类型 {fullClassName} 中未找到公共实例方法：{method}" });
                }

                // 3. 解析参数
                object[] paramValues = isPost
                    ? await ParseBodyParametersAsync(targetMethod)
                    : await ParseQueryParametersAsync(targetMethod);

                // 4. 实例化并执行方法
                object instance = Activator.CreateInstance(targetType);
                object result = await ExecuteMethodAsync(targetMethod, instance, paramValues);

                return Ok(result);
            }
            catch (TargetInvocationException ex)
            {
                var realEx = ex.InnerException ?? ex;
                return Content(HttpStatusCode.InternalServerError, new
                {
                    Error = $"方法执行失败：{realEx.Message}",
                    InnerError = realEx.InnerException?.Message
                });
            }
            catch (ArgumentException ex)
            {
                return Content(HttpStatusCode.BadRequest, new { Error = ex.Message });
            }
            catch (Exception ex)
            {
                return Content(HttpStatusCode.InternalServerError, new
                {
                    Error = $"执行失败：{ex.Message}",
                    StackTrace = ex.StackTrace
                });
            }
        }

        #region 核心辅助方法
        /// <summary>
        /// 扫描白名单内的程序集，查找指定类型
        /// </summary>
        private Type ScanAssembliesForType(string fullClassName)
        {
            foreach (var assemblyName in _scanAssemblyNames)
            {
                // 先从已加载的程序集里找
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase));

                if (assembly == null)
                {
                    string binPath = AppDomain.CurrentDomain.BaseDirectory;
                    // 优先加载 exe（你现在是控制台程序）
                    string assemblyPath = Path.Combine(binPath, $"{assemblyName}.exe");

                    // 找不到 exe 再试 dll
                    if (!File.Exists(assemblyPath))
                    {
                        assemblyPath = Path.Combine(binPath, $"{assemblyName}.dll");
                    }

                    if (File.Exists(assemblyPath))
                    {
                        assembly = Assembly.LoadFrom(assemblyPath);
                    }
                    else
                    {
                        throw new FileNotFoundException($"找不到程序集文件：{assemblyPath}");
                    }
                }

                Type type = assembly.GetType(fullClassName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        /// <summary>
        /// 解析 GET 请求的 Query 参数
        /// </summary>
        private async Task<object[]> ParseQueryParametersAsync(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var paramValues = new List<object>();
            var queryDict = Request.GetQueryNameValuePairs()
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var param in parameters)
            {
                if (!queryDict.TryGetValue(param.Name, out string value) || string.IsNullOrEmpty(value))
                {
                    // 无参数值时使用默认值
                    paramValues.Add(param.DefaultValue ?? DBNull.Value);
                    continue;
                }

                // 类型转换
                var convertedValue = await Task.Run(() =>
                {
                    try
                    {
                        return Convert.ChangeType(value, param.ParameterType, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        throw new ArgumentException($"参数 {param.Name} 类型转换失败：期望 {param.ParameterType.Name}，实际值 {value}");
                    }
                });
                paramValues.Add(convertedValue);
            }

            return paramValues.ToArray();
        }

        /// <summary>
        /// 解析 POST 请求的 Body 参数（JSON 格式）
        /// </summary>
        private async Task<object[]> ParseBodyParametersAsync(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var paramValues = new List<object>();

            // 读取 Body 内容
            string body = await Request.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(body))
            {
                // Body 为空时使用默认值
                foreach (var param in parameters)
                {
                    paramValues.Add(param.DefaultValue ?? DBNull.Value);
                }
                return paramValues.ToArray();
            }

            // 反序列化为字典
            var bodyData = JsonConvert.DeserializeObject<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();

            foreach (var param in parameters)
            {
                if (!bodyData.TryGetValue(param.Name, out object value))
                {
                    paramValues.Add(param.DefaultValue ?? DBNull.Value);
                    continue;
                }

                // JSON 类型转换
                var convertedValue = await Task.Run(() =>
                {
                    try
                    {
                        return JsonConvert.DeserializeObject(JsonConvert.SerializeObject(value), param.ParameterType);
                    }
                    catch
                    {
                        throw new ArgumentException($"参数 {param.Name} 类型转换失败：期望 {param.ParameterType.Name}");
                    }
                });
                paramValues.Add(convertedValue);
            }

            return paramValues.ToArray();
        }

        /// <summary>
        /// 执行方法（适配同步/异步方法）
        /// </summary>
        private async Task<object> ExecuteMethodAsync(MethodInfo method, object instance, object[] parameters)
        {
            // 异步方法（返回 Task/TTask<T>）
            if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var task = (Task)method.Invoke(instance, parameters);
                await task;
                return task.GetType().GetProperty("Result").GetValue(task);
            }
            else if (method.ReturnType == typeof(Task))
            {
                var task = (Task)method.Invoke(instance, parameters);
                await task;
                return null;
            }
            // 同步方法
            else
            {
                return await Task.Run(() => method.Invoke(instance, parameters));
            }
        }
        #endregion
    }
}