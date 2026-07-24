using System.Windows;
using AppShell.Core.Commands;

namespace OneHistoryStudio.Views;

/// <summary>
/// 视图通用小件(V2.1.6 R2)。
/// 说明:未做视图基类——WPF 要求 XAML 根元素类型与代码后置基类一致,
/// 换基类必须改 9 个 XAML 根元素,收益小于风险(DQ16-4),故取组合式助手。
/// </summary>
internal static class ViewKit
{
    public static string ResultSummary(CommandResult result)
        => result.Message.Split('\n')[0];

    /// <summary>
    /// Loaded 首次触发时执行一次(停靠重排会反复触发 Loaded,守卫防重复加载)。
    /// 适用于"标志只作守卫"的视图;标志兼作加载状态位的视图(如搜索框联动)自持字段。
    /// </summary>
    public static void RunOnceOnLoaded(FrameworkElement view, Func<Task> action)
    {
        var done = false;
        view.Loaded += async (_, _) =>
        {
            if (done)
                return;
            done = true;
            await action();
        };
    }
}
