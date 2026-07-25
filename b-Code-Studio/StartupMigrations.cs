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
/// 版本化一次性迁移(V2.1.6 QC-03):历代升级的一次性清理集中于此。
/// settings 键 app.migrated 记录已完成版号,达标则整段跳过——二次启动零探测零日志。
/// 各步幂等;动数据库前自动备份 main.db。今后每个版本的一次性动作只在此追加,不进装配点。
/// </summary>
public static class StartupMigrations
{
    public const string KeyMigrated = "app.migrated";

    /// <summary>v1 = V2.1.6 质量整备；v2 = V2.1.7 提交推送面板归位。</summary>
    private const int CurrentVersion = 2;

    /// <summary>
    /// V2.1.5 指令详情布局初始化状态位。沿用旧键不并入 app.migrated:
    /// 它同时是"布局已初始化"的标志,全新数据目录也要走一次(与历史清理的语义不同)。
    /// </summary>
    private const string CommandDetailLayoutKey = "layout.commanddetail.v215.tab32";

    /// <summary>窗口创建前执行(文件与数据库侧;需在表窗口/留痕器接触 main 库之前)。</summary>
    public static void Run(ISettingsService settings, AppPaths paths, IDataService data, IShellLog log)
    {
        if (int.TryParse(settings.Get(KeyMigrated), out var done) && done >= CurrentVersion)
            return;

        try
        {
            var panels = Path.Combine(paths.Root, "panels");
            if (done < 1)
            {
                DeleteIfExists(Path.Combine(panels, "motor.json"), "V2-M0 演示面板", log);
                DeleteIfExists(Path.Combine(panels, "projmod.json"), "V2.1.1 已并入模块管理页", log);
                DeleteIfExists(Path.Combine(panels, "projops.json"), "V2.1.4 已升级为项目操作页", log);
                DropDemoTables(paths, data, log);
            }

            if (done < 2)
                DeleteIfExists(Path.Combine(panels, "projpush.json"), "V2.1.7 功能已归入项目窗口", log);

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
    /// V2.1.5 指令详情窗口默认布局初始化(窗口创建后挂 Loaded,一次性)。
    /// 逻辑自 App.OnStartup 原样迁入,行为不变。
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
            log.Info("layout", "V2.1.5 已初始化指令详情窗口默认布局");
        }
    }

    private static void DeleteIfExists(string file, string reason, IShellLog log)
    {
        if (!File.Exists(file))
            return;
        File.Delete(file);
        log.Info("app", $"迁移回收 {Path.GetFileName(file)}({reason})");
    }

    /// <summary>模板验收遗产 users/bench 演示表回收;先整库备份再动手。</summary>
    private static void DropDemoTables(AppPaths paths, IDataService data, IShellLog log)
    {
        var db = Path.Combine(paths.Root, "data", "main.db");
        if (File.Exists(db))
        {
            var backup = db + ".bak-v216";
            if (!File.Exists(backup))
            {
                File.Copy(db, backup);
                log.Info("app", $"迁移前已备份数据库: data/{Path.GetFileName(backup)}");
            }
        }

        data.ExecuteSql("DROP TABLE IF EXISTS users");
        data.ExecuteSql("DROP TABLE IF EXISTS bench");
        log.Info("app", "演示表 users / bench 已回收(模板验收遗产)");
    }
}
