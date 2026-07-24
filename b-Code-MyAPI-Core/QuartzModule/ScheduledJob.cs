//Quartz/ScheduledJob.cs
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuartzModule
{
    /// <summary>
    /// 定时执行的任务
    /// </summary>
    public class ScheduledJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                Console.WriteLine($"定时任务执行中 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                // 这里写定时要执行的业务逻辑（比如数据备份、报表生成、接口同步等）
                await Task.Delay(2000); // 模拟业务操作
                Console.WriteLine($"定时任务执行完成 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"定时任务执行失败：{ex.Message}");
                // 如需重试，可配置Quartz的重试策略，这里仅打印异常
            }
        }
    }
}
