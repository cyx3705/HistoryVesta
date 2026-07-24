//UpdateModule/Update.cs
using BaseVariable;
using MyAPIBuilder;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;

namespace UpdateModule
{
    public class Update
    {
        private readonly WatchDog _watchDog;

        public Update(WatchDog watchDog)
        {
            _watchDog = watchDog ?? throw new ArgumentNullException(nameof(watchDog));
        }

        public async Task ExecuteUpdate()
        {
            try
            {
                Console.WriteLine("=== 开始执行更新（CodePush 手动推送模式） ===");

                string pushUrl = $"http://127.0.0.1:{BSV.mstPort}/api/CodePushModule/CodePushService/ExecutePushAsync";

                Console.WriteLine($"正在请求 CodePush 服务 → {pushUrl}");

                var response = await BSV.httpClient.GetAsync(pushUrl);

                Console.WriteLine($"收到响应状态码: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    throw new Exception($"CodePush 服务调用失败，状态码：{response.StatusCode}\n错误内容：{errorBody}");
                }

                var pushResult = await response.Content.ReadFromJsonAsync<CodePushResult>();

                if (pushResult == null || !pushResult.Success || pushResult.Files.Count == 0)
                    throw new Exception($"CodePush 执行失败或无文件：{pushResult?.Message ?? "未知错误"}");

                Console.WriteLine($"成功获取到 {pushResult.Files.Count} 个文件，开始更新...");

                // 关键：先关闭当前核心程序，释放文件锁
                Console.WriteLine("正在关闭当前核心程序...");
                await _watchDog.CloseCurrentCoreAsync();

                await Task.Delay(2000);   // 增加延迟，确保进程完全退出

                int savedCount = 0;
                int skippedCount = 0;

                foreach (var file in pushResult.Files)
                {
                    string relativePath = file.Key;
                    byte[] fileBytes = file.Value;

                    string targetPath = Path.Combine(Location.InternalDir, relativePath);

                    try
                    {
                        string? dir = Path.GetDirectoryName(targetPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // 覆盖写入
                        await File.WriteAllBytesAsync(targetPath, fileBytes);
                        savedCount++;

                        Console.WriteLine($"已更新文件：{relativePath} ({fileBytes.Length / 1024} KB)");
                    }
                    catch (Exception ex)
                    {
                        skippedCount++;
                        Console.WriteLine($"跳过文件 {relativePath}：{ex.Message}");
                    }
                }

                Console.WriteLine($"文件写入完成！成功更新 {savedCount} 个文件，跳过 {skippedCount} 个");

                // 启动新的主程序
                Console.WriteLine("正在启动新的主程序...");
                await _watchDog.StartNewCoreAsync();

                Console.WriteLine("=== 更新流程全部完成！===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新失败：{ex.Message}");
                if (ex.StackTrace != null)
                    Console.WriteLine($"异常详情：{ex.StackTrace}");

                // 异常时尝试恢复看门狗
                try
                {
                    _watchDog.Resume();
                }
                catch { }
            }
        }

    }
}