using BaseVariable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BaseRegister
{
    public class Initialize
    {
        /// <summary>
        /// 按顺序统一扫描并调用地址（不再调用 InitializeAsync）
        /// </summary>
        public static async Task InitializeAllModulesAsync()
        {
            // 使用新的纯扫描方法获取 moduleList
            List<ModuleInfoBase> moduleList = await ScanModules.GetAllModulesAsync();

            var orderedModules = moduleList
                .Where(m => m.Enabled)
                .OrderBy(m => m.InitializeOrder)
                .ToList();

            Console.WriteLine($"[BaseRegister] 开始执行 {orderedModules.Count} 个模块的初始化任务...");
            await ExecuteInitAddressesAsync(orderedModules);
            Console.WriteLine("[BaseRegister] 所有模块初始化任务执行完成");
        }
        /// <summary>
        /// 执行所有模块声明的初始化地址自调用（带详细诊断日志）
        /// </summary>
        private static async Task ExecuteInitAddressesAsync(List<ModuleInfoBase> modules)
        {
            var client = BSV.httpClient;

            // 强制重新设置 BaseAddress（最重要）
            string baseUrl = $"http://localhost:{BSV.mstPort}";
            client.BaseAddress = new Uri(baseUrl);
            foreach (var module in modules)
            {
                if (module.InitAddresses == null || module.InitAddresses.Count == 0)
                    continue;
                foreach (var address in module.InitAddresses)
                {
                    try
                    {
                        // 打印完整请求地址
                        var fullUri = new Uri(client.BaseAddress, address.TrimStart('/'));
                        var response = await client.GetAsync(address);
                        Console.WriteLine($"[{module.ModuleName}] ✓ 初始化调用成功: {address} 状态码: {response.StatusCode}");
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.WriteLine($"[{module.ModuleName}] ✗ 请求失败: {address}");
                        Console.WriteLine($"   错误类型: {ex.GetType().Name}");
                        Console.WriteLine($"   消息: {ex.Message}");
                        if (ex.InnerException != null)
                            Console.WriteLine($"   内部异常: {ex.InnerException.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{module.ModuleName}] ✗ 未知异常: {address} - {ex.Message}");
                    }
                }
            }
        }
    }
}
