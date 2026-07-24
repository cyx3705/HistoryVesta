//UpdateModule/AutoStart.cs
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace UpdateModule
{
    public class AutoStart
    {
        // 开机启动项名称（自定义，建议唯一，比如程序名）
        private const string AutoStartName = "MyAPI自动启动";
        // 1. 当前用户开机启动（无需管理员权限，仅当前用户登录时启动，推荐）
        private const string CurrentUserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // 2. 所有用户开机启动（需要管理员权限，任意用户登录都启动）
        private const string LocalMachineRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// 设置开机自动启动
        /// </summary>
        /// <param name="exePath">要启动的程序完整路径（如你的newExePath）</param>
        /// <param name="forAllUsers">是否为所有用户启动（true需要管理员权限）</param>
        /// <returns>是否设置成功</returns>
        [SupportedOSPlatform("windows")]
        public static bool SetAutoStart(string exePath, bool forAllUsers = false)
        {
            try
            {
                // 校验程序路径是否存在
                if (!File.Exists(exePath))
                {
                    Console.WriteLine($"程序路径不存在：{exePath}");
                    return false;
                }

                // 选择注册表根项和键路径
                RegistryKey rootKey = forAllUsers ? Registry.LocalMachine : Registry.CurrentUser;
                string runKeyPath = forAllUsers ? LocalMachineRunKey : CurrentUserRunKey;

                // 打开注册表项（可写模式），操作完成自动释放资源
                using (RegistryKey? runKey = rootKey.OpenSubKey(runKeyPath, true))
                {
                    if (runKey == null)
                    {
                        Console.WriteLine($"无法打开注册表项：{runKeyPath}");
                        return false;
                    }
                    // 写入键值：名称=AutoStartName，值=程序完整路径（必须是完整路径，否则开机找不到）
                    runKey.SetValue(AutoStartName, exePath);
                }

                Console.WriteLine($"开机启动设置成功！{(forAllUsers ? "所有用户" : "当前用户")}生效，程序路径：{exePath}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"权限不足！{(forAllUsers ? "所有用户启动需要管理员权限" : "当前用户启动无需管理员权限，请检查注册表权限")}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"开机启动设置失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 移除开机自动启动
        /// </summary>
        /// <param name="forAllUsers">是否移除所有用户的启动项（需和设置时一致）</param>
        /// <returns>是否移除成功</returns>
        [SupportedOSPlatform("windows")]
        public static bool RemoveAutoStart(bool forAllUsers = false)
        {
            try
            {
                RegistryKey rootKey = forAllUsers ? Registry.LocalMachine : Registry.CurrentUser;
                string runKeyPath = forAllUsers ? LocalMachineRunKey : CurrentUserRunKey;

                using (RegistryKey? runKey = rootKey.OpenSubKey(runKeyPath, true))
                {
                    if (runKey == null) return true; // 项不存在，视为移除成功
                    if (runKey.GetValue(AutoStartName) == null) return true; // 键值不存在，视为移除成功

                    runKey.DeleteValue(AutoStartName); // 删除指定启动项
                }

                Console.WriteLine($"开机启动项已移除！{(forAllUsers ? "所有用户" : "当前用户")}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"权限不足！移除所有用户启动项需要管理员权限");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"移除开机启动项失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查是否已设置开机启动
        /// </summary>
        /// <param name="forAllUsers">是否检查所有用户的启动项</param>
        /// <returns>是否已设置</returns>
        [SupportedOSPlatform("windows")]
        public static bool IsAutoStartSet(bool forAllUsers = false)
        {
            try
            {
                RegistryKey rootKey = forAllUsers ? Registry.LocalMachine : Registry.CurrentUser;
                string runKeyPath = forAllUsers ? LocalMachineRunKey : CurrentUserRunKey;

                using (RegistryKey? runKey = rootKey.OpenSubKey(runKeyPath, false)) // 只读模式
                {
                    if (runKey == null) return false;
                    // 检查指定名称的键值是否存在
                    return runKey.GetValue(AutoStartName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

    }
}
