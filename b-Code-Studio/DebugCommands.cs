using System.Diagnostics;
using AppShell.Core.Commands;
using AppShell.Core.Logging;

namespace HistoryJanus;

/// <summary>
/// debug.* 工具指令：
/// logflood 用于承压回归；sleep 是 --exec 自动化脚本的等待原语。
/// </summary>
public static class DebugCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        IShellLog log,
        string source = "app")
    {
        registry.Register(BuildLogFlood(log), source);
        registry.Register(BuildSleep(), source);
    }

    /// <summary>
    /// debug.logflood：按指定速率注入日志以执行承压验证。
    /// 异步长任务示范:后台线程产出、经 Progress 上报进度、全程不阻塞 UI(§5.2)。
    /// </summary>
    private static CommandDescriptor BuildLogFlood(IShellLog log) => new()
    {
        Name = "debug.logflood",
        Summary = "日志承压测试:按指定速率注入日志",
        Example = "debug.logflood rate=1000 seconds=30",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "rate",
                Description = "每秒注入条数",
                Type = ParamType.Int,
                Default = "1000",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "seconds",
                Description = "持续秒数",
                Type = ParamType.Int,
                Default = "30",
                Position = 1,
            },
        ],
        Handler = async ctx =>
        {
            var rate = Math.Clamp(ctx.GetInt("rate", 1000), 1, 100_000);
            var seconds = Math.Clamp(ctx.GetInt("seconds", 30), 1, 600);
            var total = 0L;
            var sw = Stopwatch.StartNew();

            await Task.Run(async () =>
            {
                // 每 100ms 一批;按“目标累计 = 速率 × 已流逝时间”补齐,睡眠误差不累积
                var lastProgress = 0L;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var target = Math.Min(
                        (long)(sw.Elapsed.TotalSeconds * rate),
                        (long)rate * seconds);
                    while (total < target)
                    {
                        total++;
                        log.Log(ShellLogLevel.Debug, "flood",
                            $"承压测试消息 #{total} @{sw.ElapsedMilliseconds}ms");
                    }

                    if (sw.ElapsedMilliseconds - lastProgress >= 5000)
                    {
                        lastProgress = sw.ElapsedMilliseconds;
                        ctx.Progress?.Report($"{sw.Elapsed.TotalSeconds:0}s / {seconds}s,已注入 {total} 条");
                    }

                    await Task.Delay(100);
                }

                // 补齐尾差
                for (var expected = (long)rate * seconds; total < expected; total++)
                {
                    log.Log(ShellLogLevel.Debug, "flood",
                        $"承压测试消息 #{total + 1} @{sw.ElapsedMilliseconds}ms");
                }
            });

            return CommandResult.Ok(
                $"承压完成:{total} 条 / {sw.Elapsed.TotalSeconds:0.0}s,实际速率 {total / sw.Elapsed.TotalSeconds:0} 条/秒");
        },
    };

    /// <summary>debug.sleep:异步等待(自动化脚本用,如 --exec 序列中等待模块热重载生效)。</summary>
    private static CommandDescriptor BuildSleep() => new()
    {
        Name = "debug.sleep",
        Summary = "等待指定秒数(自动化脚本用;异步等待,不阻塞 UI)",
        Example = "debug.sleep seconds=5",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "seconds",
                Description = "等待秒数(1~120)",
                Type = ParamType.Int,
                Default = "3",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var s = Math.Clamp(ctx.GetInt("seconds", 3), 1, 120);
            await Task.Delay(TimeSpan.FromSeconds(s));
            return CommandResult.Ok($"等待 {s}s 完成");
        },
    };
}
