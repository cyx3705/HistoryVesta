using BaseVariable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BaseRegister
{
    public class ScanModules
    {
        private static readonly List<ModuleInfoBase> _allModules = new();
        private static bool _hasScanned = false;
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public static async Task<List<ModuleInfoBase>> GetAllModulesAsync()
        {
            if (_hasScanned)
                return _allModules;

            await _semaphore.WaitAsync();
            try
            {
                if (_hasScanned)
                    return _allModules;

                Console.WriteLine("[ScanModules] 开始首次扫描 Internal 目录下的所有模块...");

                _allModules.Clear();

                string internalDir = BSV.pushDir;

                if (!Directory.Exists(internalDir))
                {
                    Console.WriteLine($"[ScanModules] Internal 目录不存在: {internalDir}");
                    _hasScanned = true;
                    return _allModules;
                }

                var dllFiles = Directory.GetFiles(internalDir, "*.dll", SearchOption.TopDirectoryOnly);

                foreach (var dllPath in dllFiles)
                {
                    await ProcessSingleDllAsync(dllPath, _allModules);
                }

                _hasScanned = true;
                Console.WriteLine($"[ScanModules] 首次扫描完成，共加载 {_allModules.Count} 个模块");
            }
            finally
            {
                _semaphore.Release();
            }

            return _allModules;
        }

        // ProcessSingleDllAsync 保持不变
        private static async Task ProcessSingleDllAsync(string dllPath, List<ModuleInfoBase> moduleList)
        {
            try
            {
                string fileName = Path.GetFileName(dllPath);
                var assembly = Assembly.LoadFrom(dllPath);

                var moduleTypes = assembly.GetTypes()
                    .Where(t => typeof(ModuleInfoBase).IsAssignableFrom(t)
                             && !t.IsAbstract
                             && !t.IsInterface
                             && t.IsPublic)
                    .ToList();

                foreach (var type in moduleTypes)
                {
                    if (Activator.CreateInstance(type) is ModuleInfoBase module)
                    {
                        if (!moduleList.Any(m => m.ModuleName == module.ModuleName))
                        {
                            moduleList.Add(module);
                            Console.WriteLine($"[ScanModules] ✓ 发现模块: {module.ModuleName} (来自 {fileName})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScanModules] 加载 DLL {Path.GetFileName(dllPath)} 失败: {ex.Message}");
            }
        }

        public static void ResetScan()
        {
            _allModules.Clear();
            _hasScanned = false;
        }
    }
}
