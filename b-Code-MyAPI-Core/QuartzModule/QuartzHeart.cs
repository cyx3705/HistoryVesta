// QuartzModule/QuartzHeart.cs
using Quartz;
using Quartz.Impl;
using System;
using System.Threading.Tasks;

namespace QuartzModule
{
    /// <summary>
    /// Quartz 任务调度核心类（静态类，极简版）
    /// </summary>
    public class QuartzHeart
    {
        private static IScheduler? _scheduler;

        public static async Task InitializeAsync()
        {
            try
            {
                await StartAllJobsAsync();
                Console.WriteLine("Quartz 任务调度器已初始化并启动");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Quartz 初始化失败：{ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 启动所有 Quartz 任务
        /// </summary>
        public static async Task StartAllJobsAsync()
        {
            try
            {
                // 初始化并启动调度器
                var schedulerFactory = new StdSchedulerFactory();
                _scheduler = await schedulerFactory.GetScheduler();
                await _scheduler.Start();
                // 注册定时任务和轮询任务
                await RegisterScheduledJobAsync();
                await RegisterPollingJobAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Quartz 任务启动失败：{ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 停止所有 Quartz 任务
        /// </summary>
        public static async Task StopAllJobsAsync()
        {
            if (_scheduler != null && _scheduler.IsStarted)
            {
                await _scheduler.Shutdown(waitForJobsToComplete: true);
                Console.WriteLine("Quartz 所有任务已停止");
            }
            else
            {
                Console.WriteLine("Quartz 调度器未启动，无需停止");
            }
        }

        #region 任务注册方法

        private static async Task RegisterScheduledJobAsync()
        {
            try
            {
                if (_scheduler == null || !_scheduler.IsStarted) 
                {
                    throw new InvalidOperationException("调度器未启动，无法注册定时任务");
                }

                var jobKey = new JobKey("ScheduledJob", "ScheduledGroup");

                if (await _scheduler.CheckExists(jobKey))
                {
                    await _scheduler.DeleteJob(jobKey);
                    Console.WriteLine($"[Quartz] 已删除旧定时任务 {jobKey}");
                }

                var scheduledJob = JobBuilder.Create<ScheduledJob>()
                    .WithIdentity(jobKey)
                    .Build();

                var scheduledTrigger = TriggerBuilder.Create()
                    .WithIdentity("ScheduledTrigger", "ScheduledGroup")
                    .StartNow()
                    .WithCronSchedule("0 0 2 * * ?", x => x.WithMisfireHandlingInstructionFireAndProceed())
                    .Build();

                await _scheduler.ScheduleJob(scheduledJob, scheduledTrigger);
                Console.WriteLine("  已注册定时任务（每天凌晨2点执行）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"注册定时任务失败：{ex.Message}");
                throw;
            }
        }

        private static async Task RegisterPollingJobAsync()
        {
            try
            {
                if (_scheduler == null || !_scheduler.IsStarted) 
                {
                    throw new InvalidOperationException("调度器未启动，无法注册轮询任务");
                }

                var jobKey = new JobKey("ApiPollingJob", "PollingGroup");

                if (await _scheduler.CheckExists(jobKey))
                {
                    await _scheduler.DeleteJob(jobKey);
                    Console.WriteLine($"[Quartz] 已删除旧轮询任务 {jobKey}");
                }

                var pollingJob = JobBuilder.Create<PollingJob>()
                    .WithIdentity(jobKey)
                    .Build();

                var pollingTrigger = TriggerBuilder.Create()
                    .WithIdentity("ApiPollingTrigger", "PollingGroup")
                    .StartNow()
                    .WithSimpleSchedule(schedule => schedule
                        .WithIntervalInSeconds(5)
                        .RepeatForever()
                        .WithMisfireHandlingInstructionNextWithRemainingCount())
                    .Build();

                await _scheduler.ScheduleJob(pollingJob, pollingTrigger);
                Console.WriteLine("  已注册5秒轮询任务");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"注册轮询任务失败：{ex.Message}");
                throw;
            }
        }

        #endregion
    }
}