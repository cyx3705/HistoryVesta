using BaseVariable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MyAPIBuilder;

[SupportedOSPlatform("windows")]
public class InstallTool
{
    // ==================== Windows API ====================
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
    public class InstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
    /// <summary>
    /// 执行安装（使用统一的推送接口）
    /// </summary>
    /// <summary>
    /// 执行安装（使用统一的推送接口 + 自动创建文件夹结构）
    /// </summary>
    public async Task<InstallResult> ExecuteInstallAsync(string targetInstallDir = null)
    {
        var result = new InstallResult();

        // 如果没有传入安装目录，使用默认 InternalDir
        string installDir = Location.InternalDir;

        try
        {
            Console.WriteLine("=== 开始执行安装（InstallTool） ===");

            // ==================== 关键：安装前先确保所有文件夹结构存在 ====================
            Console.WriteLine("正在检查并创建所需文件夹结构...");
            EnsureAllDirectories();   // 调用你现有的自动创建方法

            Console.WriteLine($"安装目标目录: {installDir}");

            string pushUrl = $"http://192.168.137.4:{BSV.mstPort}/api/CodePushModule/CodePushService/ExecutePushAsync";

            Console.WriteLine($"正在请求推送接口 → {pushUrl}");

            var response = await BSV.httpClient.GetAsync(pushUrl);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"推送接口调用失败，状态码：{response.StatusCode}\n{errorBody}");
            }

            var pushResult = await response.Content.ReadFromJsonAsync<CodePushResult>();

            if (pushResult == null || !pushResult.Success || pushResult.Files.Count == 0)
                throw new Exception($"推送接口返回失败或无文件：{pushResult?.Message ?? "未知错误"}");

            Console.WriteLine($"成功获取到 {pushResult.Files.Count} 个安装文件");

            // 确保安装目录存在（双重保险）
            if (!Directory.Exists(installDir))
                Directory.CreateDirectory(installDir);

            int installedCount = 0;

            foreach (var file in pushResult.Files)
            {
                string relativePath = file.Key;
                byte[] fileBytes = file.Value;

                string targetPath = Path.Combine(installDir, relativePath);

                // 创建子目录
                string? dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.WriteAllBytesAsync(targetPath, fileBytes);
                installedCount++;

                Console.WriteLine($"已安装文件：{relativePath}");
            }

            result.Success = true;
            result.Message = $"安装成功！共安装 {installedCount} 个文件，目标目录：{installDir}";
            Console.WriteLine(result.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"安装失败：{ex.Message}";
            Console.WriteLine(result.Message);
        }

        return result;
    }


    /// <summary>
    /// 自动创建所有需要的文件夹结构（通过反射读取 Location 类中的所有路径常量）
    /// </summary>
    public static void EnsureAllDirectories()
    {
        Console.WriteLine("开始创建文件夹结构...");

        try
        {
            var fields = typeof(Location)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType == typeof(string));

            int createdCount = 0;

            foreach (var field in fields)
            {
                string? path = field.GetValue(null) as string;
                if (string.IsNullOrEmpty(path)) continue;

                try
                {
                    // 确保父目录存在
                    string? parentDir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                        Console.WriteLine($"已创建父目录: {parentDir}");
                    }

                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                        Console.WriteLine($"已创建目录: {path}  (字段: {field.Name})");
                        createdCount++;
                    }
                    else
                    {
                        Console.WriteLine($"目录已存在: {path}  (字段: {field.Name})");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"创建目录失败 {path}: {ex.Message}");
                }
            }

            Console.WriteLine($"文件夹结构创建完成！共创建/确认 {createdCount} 个目录。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EnsureAllDirectories 执行失败: {ex.Message}");
        }
    }
}