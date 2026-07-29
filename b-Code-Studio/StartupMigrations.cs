using System.IO;
using System.Windows;
using AppShell.Core.Data;
using AppShell.Core.Docking;
using AppShell.Core.Logging;
using AppShell.Core.Storage;
using AppShell.Services;
using AppShell.Shell;

namespace OneHistoryStudio;

/// <summary>
/// 版本化一次性迁移：升级清理集中于此。
/// settings 键 app.migrated 记录已完成版号,达标则整段跳过——二次启动零探测零日志。
/// 各步幂等；动数据库前自动备份 main.db。新增一次性动作只在此追加，不进入装配点。
/// </summary>
public static class StartupMigrations
{
    public const string KeyMigrated = "app.migrated";

    /// <summary>迁移序号只描述执行顺序；已执行到第二阶段。</summary>
    private const int CurrentVersion = 2;

    /// <summary>
    /// 指令详情布局初始化状态位。沿用独立键，不并入 app.migrated：
    /// 它同时是“布局已初始化”的标志，全新数据目录也要执行一次。
    /// </summary>
    private const string CommandDetailLayoutKey = "layout.commanddetail.v215.tab32";

    /// <summary>窗口创建前执行(文件与数据库侧;需在表窗口/留痕器接触 main 库之前)。</summary>
    public static void Run(ISettingsService settings, AppPaths paths, IShellLog log)
    {
        if (int.TryParse(settings.Get(KeyMigrated), out var done) && done >= CurrentVersion)
            return;

        try
        {
            var panels = paths.PanelsDir;
            if (done < 1)
            {
                DeleteIfExists(Path.Combine(panels, "motor.json"), "已停用的演示面板", log);
                DeleteIfExists(Path.Combine(panels, "projmod.json"), "功能已并入模块管理页", log);
                DeleteIfExists(Path.Combine(panels, "projops.json"), "功能已并入项目操作页", log);
                DropLegacyDemoFiles(paths, log);
            }

            if (done < 2)
                DeleteIfExists(Path.Combine(panels, "projpush.json"), "功能已归入项目窗口", log);

            settings.Set(KeyMigrated, CurrentVersion.ToString());
            log.Info("app", $"一次性迁移完成(app.migrated = {CurrentVersion})");
        }
        catch (Exception ex)
        {
            // 不落版号,下次启动重试;迁移失败不阻断启动
            log.Error("app", $"一次性迁移失败(下次启动重试): {ex.Message}");
        }
    }

    /// <summary>
    /// 指令详情窗口默认布局初始化（窗口创建后挂 Loaded，仅执行一次）。
    /// 在窗口 Loaded 后执行，以确保停靠目标已创建。
    /// </summary>
    public static void InitCommandDetailLayoutOnce(ShellWindow window, ISettingsService settings, IShellLog log)
    {
        if (string.Equals(settings.Get(CommandDetailLayoutKey), "true", StringComparison.OrdinalIgnoreCase))
            return;

        window.Loaded += ResetOnce;
        void ResetOnce(object sender, RoutedEventArgs args)
        {
            window.Loaded -= ResetOnce;
            window.Docking.Dock("commanddetail", DockSide.Tab, targetId: "projops");
            window.Docking.SetRatio("commanddetail", 0.32);
            settings.Set(CommandDetailLayoutKey, "true");
            log.Info("layout", "已初始化指令详情窗口默认布局");
        }
    }

    private static void DeleteIfExists(string file, string reason, IShellLog log)
    {
        if (!File.Exists(file))
            return;
        File.Delete(file);
        log.Info("app", $"迁移回收 {Path.GetFileName(file)}({reason})");
    }

    /// <summary>数据库退出后仅保留旧 main.db，不再打开或改写。</summary>
    private static void DropLegacyDemoFiles(AppPaths paths, IShellLog log)
    {
        var db = Path.Combine(paths.DataDir, "main.db");
        if (File.Exists(db))
        {
            var backup = db + ".retired-backup";
            if (!File.Exists(backup))
            {
                File.Copy(db, backup);
                log.Info("app", $"旧 SQLite 已保留只读备份: data/{Path.GetFileName(backup)}");
            }
        }
    }
}
