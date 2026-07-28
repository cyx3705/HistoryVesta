using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppShell.Core.Commands;

namespace OneHistoryStudio.Git;

/// <summary>
/// 工具清单(V2.2 TL-01):工具项目 z 级元文件夹中 module.manifest.json 的解析结果。
/// 路径字段均已解析为绝对路径并通过 worktree 内界校验(M1.5 守卫等级)。
/// </summary>
public sealed class ToolManifest
{
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public required string ArtifactPath { get; init; }
    public string? DocsPath { get; init; }
    public string? PanelPath { get; init; }
    public IReadOnlyList<string> DepPaths { get; init; } = [];

    public bool Ui { get; init; }

    /// <summary>standard / readonly / hidden(Q211-2,V22-M3 生效)。</summary>
    public string McpExposure { get; init; } = "standard";

    /// <summary>来源项目(分支名)。</summary>
    public required string SourceProject { get; init; }

    public required string ManifestPath { get; init; }
}

/// <summary>一份清单文件的装载结果:成功携 Manifest,失败携错误说明(TL-03 逐项报错不中断)。</summary>
public sealed record ToolManifestEntry(
    ToolManifest? Manifest, string SourceProject, string ManifestPath, string? Error);

/// <summary>module.manifest.json 装载与校验(TL-01/03)。</summary>
public static partial class ToolManifestLoader
{
    public const string FileName = "module.manifest.json";

    /// <summary>
    /// 尚未装配注册表时仍需保留的根命令。带点号的内置域必须来自运行时注册表，
    /// 不再在清单解析器中复制一份域名单。
    /// </summary>
    private static readonly IReadOnlySet<string> PermanentBuiltinDomains =
        new HashSet<string>(["help", "history", "cls"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record ManifestDto(
        string? Name, string? Version, string? Description, string? Artifact,
        string? Docs, string? Panel, string[]? Deps, string? McpExposure, bool Ui = false);

    /// <summary>
    /// 启动期自检：返回注册表已有、但保留清单尚未覆盖的一级指令域。
    /// 调用方应在模块装载前传入内置注册表快照；根命令不构成点号域，故不参与比较。
    /// </summary>
    public static IReadOnlyList<string> FindUnreservedBuiltinDomains(
        IEnumerable<string> commandNames,
        IEnumerable<string>? reservedCommandNames = null)
    {
        var reserved = reservedCommandNames == null
            ? new HashSet<string>(PermanentBuiltinDomains, StringComparer.OrdinalIgnoreCase)
            : CommandRegistry.DomainsOf(reservedCommandNames)
                .Union(PermanentBuiltinDomains, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return CommandRegistry.DomainsOf(commandNames)
            .Where(domain => !reserved.Contains(domain))
            .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ToolManifestEntry Load(
        string manifestPath,
        string worktreeRoot,
        string branch,
        IEnumerable<string>? reservedCommandNames = null)
    {
        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex)
        {
            return new ToolManifestEntry(null, branch, manifestPath, $"JSON 解析失败: {ex.Message}");
        }

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
            return new ToolManifestEntry(null, branch, manifestPath, "缺少必填字段 name");

        var name = dto.Name.Trim();
        if (!NamePattern().IsMatch(name))
            return new ToolManifestEntry(null, branch, manifestPath,
                $"模块名不合法(须 ^[A-Za-z_][A-Za-z0-9_-]{{0,63}}$): {name}");
        var reservedDomains = CommandRegistry.DomainsOf(reservedCommandNames ?? [])
            .Union(PermanentBuiltinDomains, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reservedDomains.Contains(name))
            return new ToolManifestEntry(null, branch, manifestPath, $"模块名与内置指令域冲突: {name}");

        if (string.IsNullOrWhiteSpace(dto.Artifact))
            return new ToolManifestEntry(null, branch, manifestPath, "缺少必填字段 artifact");

        var exposure = string.IsNullOrWhiteSpace(dto.McpExposure) ? "standard" : dto.McpExposure.Trim().ToLowerInvariant();
        if (exposure is not ("standard" or "readonly" or "hidden"))
            return new ToolManifestEntry(null, branch, manifestPath,
                $"mcpExposure 取值应为 standard/readonly/hidden: {dto.McpExposure}");

        if (Resolve(worktreeRoot, dto.Artifact, out var artifact) is { } artifactError)
            return new ToolManifestEntry(null, branch, manifestPath, $"artifact {artifactError}");

        string? docs = null;
        if (!string.IsNullOrWhiteSpace(dto.Docs))
        {
            if (Resolve(worktreeRoot, dto.Docs, out docs) is { } docsError)
                return new ToolManifestEntry(null, branch, manifestPath, $"docs {docsError}");
        }
        else
        {
            var defaultDocs = Path.ChangeExtension(artifact, ".xml");
            docs = File.Exists(defaultDocs) ? defaultDocs : null; // 缺省取 artifact 同名 .xml(存在才带)
        }

        string? panel = null;
        if (!string.IsNullOrWhiteSpace(dto.Panel)
            && Resolve(worktreeRoot, dto.Panel, out panel) is { } panelError)
        {
            return new ToolManifestEntry(null, branch, manifestPath, $"panel {panelError}");
        }

        var deps = new List<string>();
        foreach (var dep in dto.Deps ?? [])
        {
            if (string.IsNullOrWhiteSpace(dep))
                continue;
            if (Resolve(worktreeRoot, dep, out var depPath) is { } depError)
                return new ToolManifestEntry(null, branch, manifestPath, $"deps[{dep}] {depError}");
            deps.Add(depPath!);
        }

        return new ToolManifestEntry(new ToolManifest
        {
            Name = name,
            Version = dto.Version?.Trim() ?? "",
            Description = dto.Description?.Trim() ?? "",
            ArtifactPath = artifact!,
            DocsPath = docs,
            PanelPath = panel,
            DepPaths = deps,
            Ui = dto.Ui,
            McpExposure = exposure,
            SourceProject = branch,
            ManifestPath = manifestPath,
        }, branch, manifestPath, null);
    }

    /// <summary>相对路径 → worktree 内绝对路径;越界/带盘符一律拒绝(TL-03)。返回错误文本或 null。</summary>
    private static string? Resolve(string worktreeRoot, string relative, out string? full)
    {
        full = null;
        if (Path.IsPathRooted(relative))
            return "必须是 worktree 内相对路径";

        var root = Path.GetFullPath(worktreeRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return $"越出所属 worktree,已拒绝: {relative}";

        full = candidate;
        return null;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();
}
