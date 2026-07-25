using AppShell.Core;
using System.IO;
using System.Security.Cryptography;
using AppShell.Core.Data;
using AppShell.Core.Logging;

namespace OneHistoryStudio.Git;

/// <summary>tool.scan 的一行结果(Data 载荷,MCP 可读)。</summary>
public sealed record ToolScanRow(
    string Name,
    string Version,
    string SourceProject,
    string DeployState,
    string McpExposure,
    string ArtifactPath,
    string ManifestPath,
    string? Error);

/// <summary>
/// 项目库自扩展飞轮的业务层(V2.2 §3/§4):
/// M1 = 清单发现与部署状态判定(tool.scan,只读);M2 追加 sync/remove 与模块槽写入。
/// 溯源表 tool_registry 常驻 main 库(建表幂等,沿 HistoryRecorder 先例在构造期确保,
/// 不占用 StartupMigrations 版号——迁移器只管一次性清理,17 号文档 §9-7 按此修正)。
/// </summary>
public sealed class ToolSyncService
{
    private readonly ProjectService _projects;
    private readonly IDataService _data;
    private readonly IShellLog _log;
    private readonly Func<string> _modulesDir;

    public ToolSyncService(ProjectService projects, IDataService data, IShellLog log, Func<string> modulesDir)
    {
        _projects = projects;
        _data = data;
        _log = log;
        _modulesDir = modulesDir;
        try
        {
            _data.ExecuteSql(
                """
                CREATE TABLE IF NOT EXISTS tool_registry (
                    name         TEXT PRIMARY KEY,
                    branch       TEXT NOT NULL,
                    version      TEXT,
                    sha256       TEXT NOT NULL,
                    synced_at    TEXT NOT NULL,
                    source_path  TEXT NOT NULL,
                    mcp_exposure TEXT NOT NULL DEFAULT 'standard'
                )
                """);
        }
        catch (Exception ex)
        {
            _log.Error("tool", $"tool_registry 建表失败: {ex.Message}");
        }

        try
        {
            // M2 期建的旧表补列(重复添加抛错即忽略,幂等)
            _data.ExecuteSql("ALTER TABLE tool_registry ADD COLUMN mcp_exposure TEXT NOT NULL DEFAULT 'standard'");
        }
        catch (Exception)
        {
            // 列已存在
        }
    }

    // ---------------------------------------------------------------- mcpExposure(CX-01 / Q211-2)

    private volatile Dictionary<string, string>? _exposureCache;

    /// <summary>模块名 → 清单声明的 mcpExposure(来自溯源表);根平铺等无记录 → null(=standard)。</summary>
    public string? GetExposure(string moduleName)
    {
        var cache = _exposureCache;
        if (cache == null)
        {
            cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var (result, _) = _data.ExecuteSql("SELECT name, mcp_exposure FROM tool_registry");
                if (result != null)
                {
                    foreach (var row in result.Rows)
                    {
                        if (row[0]?.ToString() is { Length: > 0 } n)
                            cache[n] = row[1]?.ToString() ?? "standard";
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn("tool", $"读取暴露档失败: {ex.Message}");
            }

            _exposureCache = cache;
        }

        return cache.GetValueOrDefault(moduleName);
    }

    private void InvalidateExposureCache() => _exposureCache = null;

    /// <summary>
    /// TL-02:沿 worktree 清单发现全部 module.manifest.json,
    /// 逐项校验(TL-03)并标注部署状态;单项错误不中断整次扫描。
    /// </summary>
    public async Task<(bool Success, string Message, List<ToolScanRow> Rows)> ScanAsync()
    {
        var (git, metas, warnings) = await _projects.ListMetaFoldersAsync();
        if (!git.Success)
            return (false, $"获取工作树清单失败:\n{git.Output}", []);

        var rows = new List<ToolScanRow>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in metas)
        {
            var manifestPath = Path.Combine(meta.FullPath, ToolManifestLoader.FileName);
            if (!File.Exists(manifestPath))
                continue;

            var worktreeRoot = Path.GetDirectoryName(meta.FullPath)!;
            var entry = ToolManifestLoader.Load(manifestPath, worktreeRoot, meta.ProjectName);

            if (entry.Manifest == null)
            {
                rows.Add(new ToolScanRow("(无效)", "", entry.SourceProject, "错误", "",
                    "", entry.ManifestPath, entry.Error));
                continue;
            }

            var m = entry.Manifest;
            if (seen.TryGetValue(m.Name, out var firstOwner))
            {
                rows.Add(new ToolScanRow(m.Name, m.Version, m.SourceProject, "错误", m.McpExposure,
                    m.ArtifactPath, m.ManifestPath, $"模块名重复(已被 {firstOwner} 声明)"));
                continue;
            }

            seen[m.Name] = m.SourceProject;
            rows.Add(new ToolScanRow(m.Name, m.Version, m.SourceProject, DeployStateOf(m),
                m.McpExposure, m.ArtifactPath, m.ManifestPath, null));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"共发现 {rows.Count} 份工具清单:");
        if (rows.Count == 0)
            sb.Append("\n  (无。工具项目在 z 级元文件夹放 module.manifest.json 即可被发现)");
        foreach (var row in rows)
        {
            sb.Append($"\n  {row.Name,-20} {(row.Version.Length > 0 ? row.Version : "-"),-10} " +
                      $"[{row.DeployState}]  ← {row.SourceProject}(mcp:{row.McpExposure})");
            if (row.Error != null)
                sb.Append($"\n      ✗ {row.Error}");
        }

        foreach (var warning in warnings)
            sb.Append($"\n  ⚠ {warning}");
        sb.Append("\n部署状态: 未同步=从未入库 | 最新=产物哈希与溯源一致 | 已过期=产物已重建,tool.sync 可更新");

        return (true, sb.ToString(), rows);
    }

    /// <summary>A220-2:按产物 SHA-256 与 tool_registry 对比判定部署状态。</summary>
    private string DeployStateOf(ToolManifest manifest)
    {
        if (!File.Exists(manifest.ArtifactPath))
            return "产物缺失";

        string sha;
        try
        {
            sha = Sha256Of(manifest.ArtifactPath);
        }
        catch (Exception ex)
        {
            _log.Warn("tool", $"计算产物哈希失败 {manifest.Name}: {ex.Message}");
            return "产物不可读";
        }

        var registered = RegisteredSha(manifest.Name);
        if (registered == null)
            return "未同步";
        return registered.Equals(sha, StringComparison.OrdinalIgnoreCase) ? "最新" : "已过期";
    }

    private string? RegisteredSha(string name)
    {
        try
        {
            var (result, _) = _data.ExecuteSql(
                $"SELECT sha256 FROM tool_registry WHERE name={SqlText.Quote(name)}");
            if (result == null || result.Rows.Count == 0)
                return null;
            var col = result.Columns.ToList().FindIndex(c =>
                c.Equals("sha256", StringComparison.OrdinalIgnoreCase));
            return col >= 0 ? result.Rows[0][col]?.ToString() : null;
        }
        catch (Exception ex)
        {
            _log.Warn("tool", $"读取溯源记录失败 {name}: {ex.Message}");
            return null;
        }
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---------------------------------------------------------------- 同步(TS-01/04/05)

    /// <summary>
    /// tool.sync:把清单产物复制入模块槽 Modules\&lt;name&gt;\ 并落溯源;热重载自动接手。
    /// all=true 只动「未同步/已过期」项;显式动作,无自动装载(TS-04)。
    /// </summary>
    public async Task<(bool Success, string Message)> SyncAsync(string? name, bool all, IProgress<string>? progress)
    {
        var (ok, _, rows) = await ScanAsync();
        if (!ok)
            return (false, "扫描失败,无法同步(详见上一条回显)");

        List<ToolScanRow> targets;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var row = rows.FirstOrDefault(r => r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return (false, $"清单中没有名为 {name} 的工具(tool.scan 查看全部)");
            if (row.Error != null)
                return (false, $"{row.Name} 清单有错,拒绝同步: {row.Error}");
            if (row.DeployState == "产物缺失")
                return (false, $"{row.Name} 产物缺失,先在工具项目中构建: {row.ArtifactPath}");
            targets = [row];
        }
        else if (all)
        {
            targets = rows.Where(r => r.Error == null && r.DeployState is "未同步" or "已过期").ToList();
            if (targets.Count == 0)
                return (true, "没有需要同步的工具(全部为最新或不可同步)");
        }
        else
        {
            return (false, "请指定 name=<工具名> 或 all=true");
        }

        var sb = new System.Text.StringBuilder();
        var synced = 0;
        foreach (var row in targets)
        {
            progress?.Report($"同步 {row.Name} ...");
            var (success, detail) = SyncOne(row);
            sb.Append($"\n  {row.Name}: {detail}");
            if (success)
                synced++;
        }

        sb.Insert(0, $"同步完成: {synced}/{targets.Count} 个工具");
        if (synced > 0)
            sb.Append("\n提示: 新指令即刻可用;MCP 客户端(Codex)需新任务才能看到新工具(tools/list 任务级快照)");
        return (synced == targets.Count, sb.ToString());
    }

    private (bool Success, string Detail) SyncOne(ToolScanRow row)
    {
        try
        {
            // 找回完整清单(路径已校验);name 经正则约束,槽路径必在 Modules 内(TS-05)
            var worktreeRoot = Path.GetDirectoryName(Path.GetDirectoryName(row.ManifestPath)!)!;
            var entry = ToolManifestLoader.Load(row.ManifestPath, worktreeRoot, row.SourceProject);
            if (entry.Manifest is not { } manifest)
                return (false, $"清单重读失败: {entry.Error}");

            var modulesRoot = Path.GetFullPath(_modulesDir());
            var slot = Path.GetFullPath(Path.Combine(modulesRoot, manifest.Name));
            if (!slot.StartsWith(modulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return (false, "槽路径越界,已拒绝");

            var sourceSha = Sha256Of(manifest.ArtifactPath);

            // 重建槽内容(单槽覆盖,Q220-2 版本共存留 V2.3)
            if (Directory.Exists(slot))
                Directory.Delete(slot, recursive: true);
            Directory.CreateDirectory(slot);

            var artifactTarget = Path.Combine(slot, Path.GetFileName(manifest.ArtifactPath));
            File.Copy(manifest.ArtifactPath, artifactTarget);

            if (manifest.DocsPath != null && File.Exists(manifest.DocsPath))
                File.Copy(manifest.DocsPath, Path.ChangeExtension(artifactTarget, ".xml"), overwrite: true);

            if (manifest.PanelPath != null && File.Exists(manifest.PanelPath))
                File.Copy(manifest.PanelPath, Path.Combine(slot, $"{manifest.Name}.panel.json"), overwrite: true);

            foreach (var dep in manifest.DepPaths)
            {
                if (File.Exists(dep))
                    File.Copy(dep, Path.Combine(slot, Path.GetFileName(dep)), overwrite: true);
                else
                    return (false, $"依赖缺失: {dep}");
            }

            // TS-05:复制后哈希复核
            var copiedSha = Sha256Of(artifactTarget);
            if (!copiedSha.Equals(sourceSha, StringComparison.OrdinalIgnoreCase))
                return (false, "复制后哈希不一致,已中止(槽内容不可信,请重试)");

            _data.ExecuteSql(
                "INSERT OR REPLACE INTO tool_registry (name, branch, version, sha256, synced_at, source_path, mcp_exposure) VALUES (" +
                $"{SqlText.Quote(manifest.Name)},{SqlText.Quote(manifest.SourceProject)}," +
                $"{SqlText.Quote(manifest.Version)},{SqlText.Quote(sourceSha)}," +
                $"'{DateTime.Now:yyyy-MM-dd HH:mm:ss}',{SqlText.Quote(manifest.ArtifactPath)}," +
                $"{SqlText.Quote(manifest.McpExposure)})");
            InvalidateExposureCache();

            _log.Info("tool", $"工具已入槽: {manifest.Name} ← {manifest.SourceProject}(sha {sourceSha[..12]}…)");
            return (true, $"✓ 入槽 Modules\\{manifest.Name}\\(v{(manifest.Version.Length > 0 ? manifest.Version : "?")},来源 {manifest.SourceProject})");
        }
        catch (Exception ex)
        {
            return (false, $"同步失败: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 移除与清单(TS-02/03)

    /// <summary>tool.remove:删槽 + 注销溯源(面板副本随 MD-08 在下次重载回收)。</summary>
    public (bool Success, string Message) Remove(string name)
    {
        name = name.Trim();
        var modulesRoot = Path.GetFullPath(_modulesDir());
        var slot = Path.GetFullPath(Path.Combine(modulesRoot, name));
        if (!slot.StartsWith(modulesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (false, "槽路径越界,已拒绝");

        var hadSlot = Directory.Exists(slot);
        if (hadSlot)
            Directory.Delete(slot, recursive: true);

        int removedRows;
        try
        {
            (_, removedRows) = _data.ExecuteSql(
                $"DELETE FROM tool_registry WHERE name={SqlText.Quote(name)}");
        }
        catch (Exception ex)
        {
            return (false, $"溯源注销失败: {ex.Message}");
        }

        InvalidateExposureCache();
        if (!hadSlot && removedRows == 0)
            return (false, $"没有名为 {name} 的已同步工具(tool.list 查看)");

        _log.Info("tool", $"工具已移除: {name}(槽 {(hadSlot ? "已删" : "不存在")},溯源 {removedRows} 行)");
        return (true, $"已移除 {name}: 模块槽{(hadSlot ? "已删除(热重载注销指令)" : "不存在")},溯源记录清理 {removedRows} 行");
    }

    /// <summary>tool.list:溯源清单 + 槽在位状态(与 module.list 的运行时视角互补)。</summary>
    public (bool Success, string Message, List<ToolRegistryRow> Rows) ListRegistered()
    {
        var rows = new List<ToolRegistryRow>();
        try
        {
            var (result, _) = _data.ExecuteSql(
                "SELECT name, branch, version, sha256, synced_at FROM tool_registry ORDER BY name");
            if (result != null)
            {
                var modulesRoot = _modulesDir();
                foreach (var r in result.Rows)
                {
                    var name = r[0]?.ToString() ?? "";
                    rows.Add(new ToolRegistryRow(
                        name,
                        r[1]?.ToString() ?? "",
                        r[2]?.ToString() ?? "",
                        r[3]?.ToString() ?? "",
                        r[4]?.ToString() ?? "",
                        Directory.Exists(Path.Combine(modulesRoot, name)) ? "在位" : "槽缺失"));
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"读取溯源失败: {ex.Message}", rows);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"已同步工具 {rows.Count} 个:");
        if (rows.Count == 0)
            sb.Append("\n  (无。tool.scan 发现清单后 tool.sync 入库)");
        foreach (var row in rows)
        {
            sb.Append($"\n  {row.Name,-20} {(row.Version.Length > 0 ? row.Version : "-"),-10} " +
                      $"[{row.SlotState}]  ← {row.Branch}  @{row.SyncedAt}");
        }

        return (true, sb.ToString(), rows);
    }
}

/// <summary>tool.list 的一行(溯源视角)。</summary>
public sealed record ToolRegistryRow(
    string Name, string Branch, string Version, string Sha256, string SyncedAt, string SlotState);
