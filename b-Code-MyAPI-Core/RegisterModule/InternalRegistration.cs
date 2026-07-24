using BaseVariable;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace BaseRegister
{
    /// <summary>
    /// 内部的模块扫描和注册逻辑类，负责：
    /// </summary>
    public class InternalRegistration
    {
        // 线程安全的模块实例缓存（全局唯一）
        private static readonly ConcurrentDictionary<Type, ModuleInfoBase> _moduleInstanceCache = new();
        /// <summary>
        /// 完整的模块注册方法
        /// </summary>
        public static async Task OpenModuleAsync()
        {
            Console.WriteLine("[Startup] 开始加载所有模块...");
            List<ModuleInfoBase> moduleList;
            try
            {
                moduleList = await InternalRegistration.ScanAndRegisterAllModulesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Startup] 模块扫描注册失败: {ex.Message}");
                throw;
            }

            // 打印已加载的模块信息（美化输出）
            Console.WriteLine("\n[Startup] 模块加载完成，以下模块已成功加载：");
            Console.WriteLine("══════════════════════════════════════════════════════════════");

            foreach (var module in moduleList.OrderBy(m => m.InitializeOrder))
            {
                Console.WriteLine($"模块名称：{module.ModuleName}  ({module.Version})");
                Console.WriteLine($"   作者：{module.Author}");
                Console.WriteLine($"   描述：{module.Description}");
                if (module.MainClassType != null)
                {
                    Console.WriteLine($"   核心类：{module.MainClassType.FullName}");
                }
                Console.WriteLine($"   初始化顺序：{module.InitializeOrder}");
                Console.WriteLine("──────────────────────────────────────────────────────────────");
            }
            Console.WriteLine($"共加载 {moduleList.Count} 个模块。\n");
        }

        /// <summary>
        /// 统一扫描并注册所有模块（完整流程）
        /// </summary>
        public static async Task<List<ModuleInfoBase>> ScanAndRegisterAllModulesAsync()
        {
            Console.WriteLine("[BaseRegister] 开始扫描并注册所有模块...");

            List<ModuleInfoBase> moduleList = await ScanModules.GetAllModulesAsync();

            Console.WriteLine($"[BaseRegister] 扫描到 {moduleList.Count} 个模块，开始统一注册...");

            int registeredCount = 0;

            foreach (var module in moduleList.ToList())
            {
                try
                {
                    // 处理单个模块：注册核心类单例 + 注册接口（已适配全暴露和精准暴露）
                    if (module == null) return moduleList;
                    try
                    {
                        //注册核心类单例到 DynamicController
                        RegisterModuleMainClassSingleton(module);
                        //全暴露或精准暴露模式下注册接口到 DynamicEndpointRegistry
                        RegisterModuleEndpointsToRegistry(module);

                        Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 处理完成（单例 + 接口注册）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BaseRegister] 处理模块 {module.ModuleName} 失败: {ex.Message}");
                    }
                    registeredCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BaseRegister] 处理模块 {module.ModuleName} 时发生异常: {ex.Message}");
                }
            }

            Console.WriteLine($"[BaseRegister] 所有模块注册完成，共处理 {registeredCount} 个模块（总计 {moduleList.Count} 个）");
            return moduleList;
        }

        /// <summary>
        /// 根据 ModuleInfoBase.Open 属性进行分级暴露扫描
        /// - Open = true  → 全暴露：扫描模块所在程序集的所有 public 类和 public 方法
        /// - Open = false → 精准暴露：只扫描 MainClassType 中的 public 方法
        /// </summary>
        private static void RegisterModuleEndpointsToRegistry(ModuleInfoBase module)
        {
            var registeredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalRegistered = 0;

            // 获取当前模块所在的程序集
            var moduleAssembly = module.GetType().Assembly;

            try
            {
                if (module.Open)
                {
                    // ==================== 全暴露模式 ====================
                    Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 进入【全暴露】模式");

                    var types = moduleAssembly.GetTypes()
                        .Where(t => t.IsClass
                                 && !t.IsAbstract
                                 && !t.IsGenericTypeDefinition
                                 && t.IsPublic)
                        .Where(t => !IsModuleInfoClass(t))        // 排除 ModuleInfo 类本身
                        .ToList();

                    foreach (var type in types)
                    {
                        RegisterClassMethods(type, module, registeredPaths, ref totalRegistered);
                    }
                }
                else
                {
                    // ==================== 精准暴露模式 ====================
                    Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 进入【精准暴露】模式");

                    if (module.MainClassType != null)
                    {
                        RegisterClassMethods(module.MainClassType, module, registeredPaths, ref totalRegistered);
                    }
                    else
                    {
                        Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 未设置 MainClassType，精准暴露模式下无接口注册");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BaseRegister] 扫描模块 {module.ModuleName} 时发生异常: {ex.Message}");
            }

            Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 共注册 {totalRegistered} 个服务接口");
        }

        /// <summary>
        /// 统一注册一个类的所有符合条件的 public 方法
        /// </summary>
        private static void RegisterClassMethods(Type type, ModuleInfoBase module,
            HashSet<string> registeredPaths, ref int totalRegistered)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => !m.IsSpecialName)           // 排除属性、事件等特殊方法
                .ToList();

            string ns = type.Namespace ?? "Unknown";
            string cls = type.Name;

            int countForThisClass = 0;

            foreach (var method in methods)
            {
                if (ShouldExcludeMethod(method))
                    continue;

                var fullPath = $"/api/{ns}/{cls}/{method.Name}";

                if (!registeredPaths.Add(fullPath))
                    continue;

                DynamicEndpointRegistry.Register(ns, cls, method.Name);

                countForThisClass++;
                totalRegistered++;
            }

            if (countForThisClass > 0)
            {
                Console.WriteLine($"[BaseRegister] 类 {cls} 已注册 {countForThisClass} 个接口");
            }
        }

        private static bool ShouldExcludeMethod(MethodInfo method)
        {
            string name = method.Name;
            return name is "ToString" or "Equals" or "GetHashCode" or "GetType";
        }

        private static bool IsModuleInfoClass(Type type)
        {
            return typeof(ModuleInfoBase).IsAssignableFrom(type);
        }
        /// <summary>
        /// 注册单个模块的核心业务类单例到 DynamicController（已适配你当前的控制器命名空间）
        /// </summary>
        private static void RegisterModuleMainClassSingleton(ModuleInfoBase module)
        {
            var mainClassType = module.MainClassType;
            if (mainClassType == null) return;

            var mainClassSingleton = module.GetMainClassSingleton();
            if (mainClassSingleton == null)
            {
                Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 核心类 {mainClassType.FullName} 实例化失败");
                return;
            }

            string fullClassName = $"{mainClassType.Namespace}.{mainClassType.Name}";

            try
            {
                Type? controllerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.FullName == "Myapi.Controller.DynamicController");

                if (controllerType == null)
                {
                    Console.WriteLine($"[BaseRegister] 未找到 Myapi.Controller.DynamicController，无法注册单例");
                    return;
                }

                var registerMethod = controllerType.GetMethod("RegisterInstance", BindingFlags.Public | BindingFlags.Static);
                if (registerMethod == null)
                {
                    Console.WriteLine($"[BaseRegister] 未找到 RegisterInstance 方法");
                    return;
                }

                registerMethod.Invoke(null, new object[] { fullClassName, mainClassSingleton });
                Console.WriteLine($"[BaseRegister] 模块 {module.ModuleName} 核心类 {fullClassName} 单例已注册到控制器");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BaseRegister] 注册单例失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 获取已缓存的模块实例（供外部复用）
        /// 优先按 ModuleName 查询（推荐），兼容旧的 Type 查询
        /// </summary>
        public static ModuleInfoBase? GetCachedModuleInstance(string moduleName)
        {
            // 优先使用 ModuleName 查询（最可靠）
            if (!string.IsNullOrWhiteSpace(moduleName))
            {
                // 从内部缓存查找
                var internalModule = _moduleInstanceCache.Values
                    .FirstOrDefault(m => m.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));

                if (internalModule != null)
                    return internalModule;
            }

            return null;
        }

        /// <summary>
        /// 兼容旧代码：仍然支持按 Type 查询（内部模块常用）
        /// </summary>
        public static ModuleInfoBase? GetCachedModuleInstance(Type moduleType)
        {
            if (moduleType == null) return null;

            _moduleInstanceCache.TryGetValue(moduleType, out var instance);
            return instance;
        }
    }
}
