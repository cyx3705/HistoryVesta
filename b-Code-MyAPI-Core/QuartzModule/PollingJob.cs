//QuartzModule/PollingJob.cs
using BaseVariable;
using Quartz;

namespace QuartzModule
{
    // Quartz 轮询任务
    public class PollingJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                // 确保全局 HttpClient 已初始化
                if (BSV.httpClient == null)
                {
                    BSV.httpClient = new HttpClient();
                    BSV.httpClient.Timeout = TimeSpan.FromSeconds(10);
                }

                // 轮询目标地址
                string url = "http://localhost:5100/api/MyUtilsModule/One/One1";
                var response = await BSV.httpClient.GetAsync(url);


                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"轮询成功：{DateTime.Now:HH:mm:ss} | 响应：{content}");
                }
                else
                {
                    Console.WriteLine($"轮询失败：{DateTime.Now:HH:mm:ss} | 状态码：{(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"轮询异常：{DateTime.Now:HH:mm:ss} | {ex.Message}");
            }
        }
    }
}
