using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneHistoryStudio.Git;

public sealed record GitFileRuleInfo(
    string Pattern,
    bool Track,
    bool Lfs,
    bool Lf,
    bool Managed,
    int FileCount,
    int TrackedCount,
    int IgnoredCount,
    int LfsAttributeCount,
    int LfsPointerCount,
    int LfAttributeCount,
    string Status);

/// <summary>项目根已声明的一条格式规则(V2.2.1 台账扫描消费)。</summary>
public sealed record DeclaredRule(string Pattern, bool Track, bool Lfs, bool Lf, bool Managed);

public sealed record GitFileRulePreview(
    string Project,
    string Pattern,
    bool? Track,
    bool? Lfs,
    bool? Lf,
    string GitIgnoreDiff,
    string GitAttributesDiff,
    int AffectedFiles,
    int AddToIndex,
    int RemoveFromIndex,
    int Renormalize,
    bool Changed,
    bool Applied);

/// <summary>
/// 项目根文件格式规则：.gitignore 决定是否跟踪，.gitattributes 只生成标准 LFS/LF 行。
/// 注释块外内容保持；索引操作只使用 Git 枚举出的精确相对路径。
/// </summary>
public sealed class GitFileRuleService
{
    private const string ManagedBegin = "# OneHistoryStudio managed begin";
    private const string ManagedEnd = "# OneHistoryStudio managed end";

    /// <summary>
    /// 基线块(V2.2.1 §3.4 / D221-8):由 git.rule.sync 自模板整块重刷,机器所有。
    /// 与 managed 块(本项目特例)分离,基线更新不伤项目自身决定;两者都在块外内容之外。
    /// </summary>
    private const string BaselineBegin = "# OneHistoryStudio baseline begin";
    private const string BaselineEnd = "# OneHistoryStudio baseline end";
    private const string LfsAttributes = "filter=lfs diff=lfs merge=lfs -text";
    private const string LfAttributes = "text eol=lf";
    private const int GitBatchSize = 100;

    private readonly ProjectService _projects;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public GitFileRuleService(ProjectService projects) => _projects = projects;

    /// <summary>
    /// 基线同步(V2.2.1 §3.4 / A221-9):把模板项目根规则文件的托管内容
    /// 整块刷入目标项目的 baseline 块;项目自身的 managed 块与块外手写内容不动。
    /// apply=false 只预览。省略 project 则同步全部工作树(模板自身除外)。
    /// </summary>
    public async Task<(bool Success, string Message)> SyncBaselineAsync(
        string? project,
        bool apply,
        IProgress<string>? progress,
        CancellationToken cancellation = default)
    {
        var templateName = _projects.BaseBranch;
        var template = await _projects.ResolveWorktreeAsync(templateName).ConfigureAwait(false);
        if (!template.Success || template.Worktree == null)
            return (false, $"未找到模板项目 {templateName}: {template.Message}");

        var templateRoot = template.Worktree.WorktreePath;
        var templateDocs = await ReadDocumentsAsync(templateRoot, cancellation).ConfigureAwait(false);
        var ignoreBaseline = ManagedLines(templateDocs.Ignore);
        var attrBaseline = ManagedLines(templateDocs.Attributes);
        if (ignoreBaseline.Count == 0 && attrBaseline.Count == 0)
            return (false, $"模板 {templateName} 的托管块为空,先用 git.rule.set 在模板上建立基线");

        List<WorktreeInfo> targets;
        if (!string.IsNullOrWhiteSpace(project))
        {
            var one = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
            if (!one.Success || one.Worktree == null)
                return (false, one.Message);
            targets = [one.Worktree];
        }
        else
        {
            var (git, worktrees) = await _projects.ListWorktreesAsync().ConfigureAwait(false);
            if (!git.Success)
                return (false, $"获取工作树清单失败:\n{git.Output}");
            targets = worktrees
                .Where(w => Directory.Exists(w.WorktreePath)
                            && !w.BranchName.Equals(templateName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var text = new StringBuilder();
        text.Append($"基线同步({(apply ? "已写入" : "预览")}): 源 = {templateName} " +
                    $"({ignoreBaseline.Count} 条忽略 / {attrBaseline.Count} 条属性),目标 {targets.Count} 个项目");

        var changed = 0;
        var failed = 0;
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            foreach (var target in targets)
            {
                progress?.Report($"基线同步 {target.BranchName} ...");
                var docs = await ReadDocumentsAsync(target.WorktreePath, cancellation).ConfigureAwait(false);
                var newIgnore = RewriteBaselineBlock(docs.Ignore, ignoreBaseline);
                var newAttr = RewriteBaselineBlock(docs.Attributes, attrBaseline);
                var ignoreDiffers = newIgnore != docs.Ignore.Text;
                var attrDiffers = newAttr != docs.Attributes.Text;

                if (!ignoreDiffers && !attrDiffers)
                    continue;

                changed++;
                if (!apply)
                {
                    text.Append($"\n  {target.BranchName}: 待更新" +
                                $"{(ignoreDiffers ? " .gitignore" : "")}{(attrDiffers ? " .gitattributes" : "")}");
                    continue;
                }

                var write = await WriteDocumentsAsync(docs, newIgnore, newAttr, cancellation).ConfigureAwait(false);
                if (write.Success)
                {
                    text.Append($"\n  {target.BranchName}: ✓ 基线块已刷新");
                }
                else
                {
                    failed++;
                    text.Append($"\n  {target.BranchName}: ✗ {write.Message}");
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        text.Append(changed == 0
            ? "\n全部项目基线已是最新,无需变更"
            : apply
                ? $"\n完成: {changed - failed}/{changed} 个项目已更新" +
                  (failed > 0 ? $",{failed} 个失败" : "") + ";项目自身 managed 块与块外内容未动"
                : "\napply=true 写入(经二次确认)");
        return (failed == 0, text.ToString());
    }

    /// <summary>
    /// 读取项目根已声明的格式规则(V2.2.1:供台账扫描复用,保持规则解析唯一实现)。
    /// 只回声明,不查索引——调用方若需归宿判定,应以 git ls-files 的三态为权威。
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, DeclaredRule>> ReadDeclaredRulesAsync(
        string root,
        CancellationToken cancellation = default)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        return ReadDefinitions(documents.Ignore, documents.Attributes).ToDictionary(
            entry => entry.Key,
            entry => new DeclaredRule(
                entry.Value.Pattern, entry.Value.Track, entry.Value.Lfs, entry.Value.Lf, entry.Value.Managed),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<(bool Success, string Message, IReadOnlyList<GitFileRuleInfo> Rules)> ListAsync(
        string project,
        CancellationToken cancellation = default)
    {
        var resolved = await ResolveAsync(project).ConfigureAwait(false);
        if (!resolved.Success)
            return (false, resolved.Message, []);

        var documents = await ReadDocumentsAsync(resolved.Root!, cancellation).ConfigureAwait(false);
        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        var repository = await ReadRepositoryStateAsync(resolved.Root!, definitions.Keys, cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, []);

        var rows = BuildRows(definitions, repository);
        return (true, $"{project}: {rows.Count} 条文件格式规则", rows);
    }

    public async Task<(bool Success, string Message, GitFileRulePreview? Preview)> SetAsync(
        string project,
        string pattern,
        bool track,
        bool lfs,
        bool lf,
        bool apply,
        CancellationToken cancellation = default)
    {
        try
        {
            pattern = ValidatePattern(pattern);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, null);
        }
        if (!track && (lfs || lf))
            return (false, "不纳入 Git 时 LFS 和 LF 必须同时关闭", null);
        if (lfs && lf)
            return (false, "LFS 与 LF 互斥，不能同时开启", null);
        // V2.2.1 §3.3:目录规则只表达"忽略",不生成 .gitattributes 行(目录级 LFS/LF 无意义)
        if (LooksLikeDirectoryPattern(pattern) && (track || lfs || lf))
            return (false, "目录规则只能用于忽略:请设 track=false lfs=false lf=false", null);

        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(project).ConfigureAwait(false);
            if (!resolved.Success)
                return (false, resolved.Message, null);
            return await SetCoreAsync(
                project, resolved.Root!, pattern, track, lfs, lf, apply, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(bool Success, string Message, GitFileRulePreview? Preview)> RemoveAsync(
        string project,
        string pattern,
        bool apply,
        CancellationToken cancellation = default)
    {
        try
        {
            pattern = ValidatePattern(pattern);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message, null);
        }
        await _writeGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var resolved = await ResolveAsync(project).ConfigureAwait(false);
            if (!resolved.Success)
                return (false, resolved.Message, null);
            return await RemoveCoreAsync(project, resolved.Root!, pattern, apply, cancellation)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<(bool Success, string Message, GitFileRulePreview? Preview)> SetCoreAsync(
        string project,
        string root,
        string pattern,
        bool track,
        bool lfs,
        bool lf,
        bool apply,
        CancellationToken cancellation)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        var ignoreManaged = ManagedLines(documents.Ignore)
            .Where(line => !IgnorePatternEquals(line, pattern)).ToList();
        if (!track)
            ignoreManaged.Add(pattern);

        var attributeManaged = ManagedLines(documents.Attributes)
            .Where(line => !AttributePatternEquals(line, pattern)).ToList();
        if (track && lfs)
            attributeManaged.Add($"{pattern} {LfsAttributes}");
        else if (track && lf)
            attributeManaged.Add($"{pattern} {LfAttributes}");

        var newIgnore = RewriteDocument(
            documents.Ignore, ignoreManaged,
            line => IgnorePatternEquals(line, pattern));
        var newAttributes = RewriteDocument(
            documents.Attributes, attributeManaged,
            line => IsCanonicalAttributeForPattern(line, pattern));

        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        definitions[pattern] = new RuleDefinition(pattern, track, lfs, lf, true);
        var repository = await ReadRepositoryStateAsync(root, definitions.Keys, cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, null);

        var affected = repository.AllFiles.Where(path => MatchesPattern(pattern, path)).ToList();
        var tracked = affected.Count(path => repository.Tracked.Contains(path));
        var add = track ? affected.Count - tracked : 0;
        var remove = track ? 0 : tracked;
        var renormalize = track ? tracked : 0;
        var filesChanged = documents.Ignore.Text != newIgnore || documents.Attributes.Text != newAttributes;
        var indexChanged = add > 0 || remove > 0 || (track && (lfs || lf) && renormalize > 0);
        var preview = new GitFileRulePreview(
            project, pattern, track, lfs, lf,
            BuildDiff(".gitignore", documents.Ignore.Text, newIgnore),
            BuildDiff(".gitattributes", documents.Attributes.Text, newAttributes),
            affected.Count, add, remove, renormalize,
            filesChanged || indexChanged, false);

        if (!apply || !preview.Changed)
            return (true, PreviewMessage(preview, apply: false), preview);

        var write = await WriteDocumentsAsync(
            documents, newIgnore, newAttributes, cancellation).ConfigureAwait(false);
        if (!write.Success)
            return (false, write.Message, preview);

        var sync = await SynchronizeSetAsync(root, pattern, track, cancellation).ConfigureAwait(false);
        if (!sync.Success)
            return (false, $"规则已写入，但索引同步失败：\n{sync.Output}", preview with { Applied = true });

        return (true, PreviewMessage(preview, apply: true), preview with { Applied = true });
    }

    private async Task<(bool Success, string Message, GitFileRulePreview? Preview)> RemoveCoreAsync(
        string project,
        string root,
        string pattern,
        bool apply,
        CancellationToken cancellation)
    {
        var documents = await ReadDocumentsAsync(root, cancellation).ConfigureAwait(false);
        var newIgnore = RewriteDocument(
            documents.Ignore,
            ManagedLines(documents.Ignore).Where(line => !IgnorePatternEquals(line, pattern)).ToList(),
            line => IgnorePatternEquals(line, pattern));
        var newAttributes = RewriteDocument(
            documents.Attributes,
            ManagedLines(documents.Attributes).Where(line => !AttributePatternEquals(line, pattern)).ToList(),
            line => IsCanonicalAttributeForPattern(line, pattern));

        var definitions = ReadDefinitions(documents.Ignore, documents.Attributes);
        var repository = await ReadRepositoryStateAsync(root, definitions.Keys.Append(pattern), cancellation)
            .ConfigureAwait(false);
        if (!repository.Success)
            return (false, repository.Message, null);
        var affected = repository.AllFiles.Where(path => MatchesPattern(pattern, path)).ToList();
        var tracked = affected.Count(path => repository.Tracked.Contains(path));
        var changed = documents.Ignore.Text != newIgnore || documents.Attributes.Text != newAttributes;
        var preview = new GitFileRulePreview(
            project, pattern, null, null, null,
            BuildDiff(".gitignore", documents.Ignore.Text, newIgnore),
            BuildDiff(".gitattributes", documents.Attributes.Text, newAttributes),
            affected.Count, 0, 0, tracked, changed, false);

        if (!apply || !changed)
            return (true, PreviewMessage(preview, apply: false), preview);

        var write = await WriteDocumentsAsync(
            documents, newIgnore, newAttributes, cancellation).ConfigureAwait(false);
        if (!write.Success)
            return (false, write.Message, preview);
        var trackedExisting = affected
            .Where(path => repository.Tracked.Contains(path) && File.Exists(ToAbsolute(root, path)))
            .ToList();
        var normalize = await RunBatchesAsync(
            root, ["add", "--renormalize", "--"], trackedExisting, cancellation).ConfigureAwait(false);
        if (!normalize.Success)
            return (false, $"规则已删除，但索引重新写入失败：\n{normalize.Output}", preview with { Applied = true });
        var stage = await StageRuleFilesAsync(root, cancellation).ConfigureAwait(false);
        if (!stage.Success)
            return (false, $"规则已删除，但规则文件暂存失败：\n{stage.Output}", preview with { Applied = true });

        return (true, PreviewMessage(preview, apply: true), preview with { Applied = true });
    }

    private async Task<GitResult> SynchronizeSetAsync(
        string root,
        string pattern,
        bool track,
        CancellationToken cancellation)
    {
        var trackedResult = await GitRunner.RunAsync(
            root, ["ls-files", "-z"], cancellation: cancellation).ConfigureAwait(false);
        if (!trackedResult.Success)
            return trackedResult;
        var tracked = ParseNullPaths(trackedResult.Output)
            .Where(path => MatchesPattern(pattern, path)).ToList();

        if (!track)
        {
            var remove = await RunBatchesAsync(
                root, ["rm", "--cached", "--ignore-unmatch", "--"], tracked, cancellation)
                .ConfigureAwait(false);
            if (!remove.Success)
                return remove;
        }
        else
        {
            var normalize = await RunBatchesAsync(
                root, ["add", "--renormalize", "--"],
                tracked.Where(path => File.Exists(ToAbsolute(root, path))).ToList(), cancellation)
                .ConfigureAwait(false);
            if (!normalize.Success)
                return normalize;

            var others = await GitRunner.RunAsync(
                root, ["ls-files", "-z", "--others", "--exclude-standard"], cancellation: cancellation)
                .ConfigureAwait(false);
            if (!others.Success)
                return others;
            var add = await RunBatchesAsync(
                root, ["add", "--"],
                ParseNullPaths(others.Output).Where(path => MatchesPattern(pattern, path)).ToList(), cancellation)
                .ConfigureAwait(false);
            if (!add.Success)
                return add;
        }

        return await StageRuleFilesAsync(root, cancellation).ConfigureAwait(false);
    }

    private static async Task<GitResult> StageRuleFilesAsync(string root, CancellationToken cancellation)
    {
        var files = new List<string>();
        if (File.Exists(Path.Combine(root, ".gitignore")))
            files.Add(".gitignore");
        if (File.Exists(Path.Combine(root, ".gitattributes")))
            files.Add(".gitattributes");
        return await RunBatchesAsync(root, ["add", "--"], files, cancellation).ConfigureAwait(false);
    }

    private static async Task<GitResult> RunBatchesAsync(
        string root,
        IReadOnlyList<string> prefix,
        IReadOnlyList<string> paths,
        CancellationToken cancellation)
    {
        if (paths.Count == 0)
            return new GitResult(0, string.Empty);
        var output = new StringBuilder();
        for (var offset = 0; offset < paths.Count; offset += GitBatchSize)
        {
            var arguments = prefix.Concat(paths.Skip(offset).Take(GitBatchSize)).ToList();
            var result = await GitRunner.RunAsync(root, arguments, cancellation: cancellation)
                .ConfigureAwait(false);
            if (result.Output.Length > 0)
                output.AppendLine(result.Output);
            if (!result.Success)
                return new GitResult(result.ExitCode, output.ToString().Trim());
        }
        return new GitResult(0, output.ToString().Trim());
    }

    private static IReadOnlyList<GitFileRuleInfo> BuildRows(
        IReadOnlyDictionary<string, RuleDefinition> definitions,
        RepositoryState repository)
    {
        return definitions.Values.OrderBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
            .Select(rule =>
            {
                var files = repository.AllFiles.Where(path => MatchesPattern(rule.Pattern, path)).ToList();
                var tracked = files.Count(path => repository.Tracked.Contains(path));
                var ignored = files.Count(path => repository.Ignored.Contains(path));
                var lfsAttr = files.Count(path => string.Equals(
                    repository.Attributes.GetValueOrDefault(path)?.GetValueOrDefault("filter"),
                    "lfs", StringComparison.OrdinalIgnoreCase));
                var lfsPointers = files.Count(path => repository.LfsPointers.Contains(path));
                var lfAttr = files.Count(path =>
                {
                    var attrs = repository.Attributes.GetValueOrDefault(path);
                    return string.Equals(attrs?.GetValueOrDefault("text"), "set", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(attrs?.GetValueOrDefault("eol"), "lf", StringComparison.OrdinalIgnoreCase);
                });
                var status = Status(rule, files.Count, tracked, ignored, lfsAttr, lfsPointers, lfAttr);
                return new GitFileRuleInfo(
                    rule.Pattern, rule.Track, rule.Lfs, rule.Lf, rule.Managed,
                    files.Count, tracked, ignored, lfsAttr, lfsPointers, lfAttr, status);
            }).ToList();
    }

    private static string Status(
        RuleDefinition rule,
        int files,
        int tracked,
        int ignored,
        int lfsAttr,
        int lfsPointers,
        int lfAttr)
    {
        var source = rule.Managed ? string.Empty : " [人工规则]";
        if (!rule.Track && (rule.Lfs || rule.Lf))
            return "冲突：已忽略但仍有属性规则" + source;
        if (!rule.Track)
            return tracked > 0 ? $"{tracked} 个仍在索引" + source : $"{ignored}/{files} 已忽略" + source;
        if (rule.Lfs)
            return lfsAttr == files && lfsPointers == tracked
                ? $"{lfsPointers}/{tracked} 已是 LFS 指针" + source
                : $"属性 {lfsAttr}/{files}，指针 {lfsPointers}/{tracked}" + source;
        if (rule.Lf)
            return lfAttr == files ? $"{lfAttr}/{files} LF 生效" + source : $"LF 生效 {lfAttr}/{files}" + source;
        if (ignored > 0)
            return $"仍有 {ignored} 个文件被人工规则忽略" + source;
        return $"Git 默认处理，已跟踪 {tracked}/{files}" + source;
    }

    private async Task<RepositoryState> ReadRepositoryStateAsync(
        string root,
        IEnumerable<string> patterns,
        CancellationToken cancellation)
    {
        var patternList = patterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var trackedTask = GitRunner.RunAsync(root, ["ls-files", "-z"], cancellation: cancellation);
        var othersTask = GitRunner.RunAsync(
            root, ["ls-files", "-z", "--others", "--exclude-standard"], cancellation: cancellation);
        var ignoredTask = GitRunner.RunAsync(
            root, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard"],
            cancellation: cancellation);
        var lfsTask = GitRunner.RunAsync(
            root, ["lfs", "ls-files", "--json"], cancellation: cancellation);
        await Task.WhenAll(trackedTask, othersTask, ignoredTask, lfsTask).ConfigureAwait(false);

        var trackedResult = await trackedTask;
        var othersResult = await othersTask;
        var ignoredResult = await ignoredTask;
        var lfsResult = await lfsTask;
        if (!trackedResult.Success || !othersResult.Success || !ignoredResult.Success || !lfsResult.Success)
        {
            var failures = new[]
            {
                (Name: "读取已跟踪文件", Result: trackedResult),
                (Name: "读取未跟踪文件", Result: othersResult),
                (Name: "读取已忽略文件", Result: ignoredResult),
                (Name: "读取 LFS 指针", Result: lfsResult),
            };
            var message = string.Join("\n", failures.Where(item => !item.Result.Success)
                .Select(item => $"{item.Name}失败: {item.Result.Output}"));
            return RepositoryState.Failed(message);
        }

        var tracked = ParseNullPaths(trackedResult.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = ParseNullPaths(othersResult.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ignored = ParseNullPaths(ignoredResult.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = tracked.Concat(others).Concat(ignored).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => patternList.Any(pattern => MatchesPattern(pattern, path))).ToList();
        var attributes = await ReadAttributesAsync(root, all, cancellation).ConfigureAwait(false);
        if (!attributes.Success)
            return RepositoryState.Failed(attributes.Message);
        HashSet<string> pointers;
        try
        {
            pointers = ParseLfsJson(lfsResult.Output);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return RepositoryState.Failed($"解析 Git LFS JSON 失败: {ex.Message}");
        }
        return RepositoryState.Ok(tracked, ignored, all, pointers, attributes.Values);
    }

    private static async Task<(bool Success, string Message, Dictionary<string, Dictionary<string, string>> Values)>
        ReadAttributesAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellation)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return (true, string.Empty, values);
        var input = string.Join('\0', paths) + '\0';
        var result = await GitRunner.RunWithInputAsync(
            root, ["check-attr", "-z", "--stdin", "filter", "text", "eol"], input,
            cancellation: cancellation).ConfigureAwait(false);
        if (!result.Success)
            return (false, result.Output, values);
        var parts = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 2 < parts.Length; index += 3)
        {
            var path = NormalizePath(parts[index]);
            if (!values.TryGetValue(path, out var attrs))
                values[path] = attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            attrs[parts[index + 1]] = parts[index + 2].Trim();
        }
        return (true, string.Empty, values);
    }

    private static Dictionary<string, RuleDefinition> ReadDefinitions(
        TextDocument ignore,
        TextDocument attributes)
    {
        var result = new Dictionary<string, RuleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries(ignore))
        {
            var pattern = ParseIgnorePattern(entry.Text);
            // V2.2.1:格式规则与目录规则都读回(目录规则只可能出现在 .gitignore)
            if (pattern == null || (!LooksLikeFormatPattern(pattern) && !LooksLikeDirectoryPattern(pattern)))
                continue;
            result[pattern] = Merge(result.GetValueOrDefault(pattern), pattern,
                track: false, lfs: null, lf: null, entry.Managed);
        }
        foreach (var entry in Entries(attributes))
        {
            var parsed = ParseCanonicalAttribute(entry.Text);
            if (parsed == null || !LooksLikeFormatPattern(parsed.Value.Pattern))
                continue;
            result[parsed.Value.Pattern] = Merge(result.GetValueOrDefault(parsed.Value.Pattern),
                parsed.Value.Pattern, track: null, parsed.Value.Lfs, parsed.Value.Lf, entry.Managed);
        }
        return result;
    }

    private static RuleDefinition Merge(
        RuleDefinition? current,
        string pattern,
        bool? track,
        bool? lfs,
        bool? lf,
        bool managed)
        => new(
            pattern,
            track ?? current?.Track ?? true,
            (lfs ?? current?.Lfs ?? false),
            (lf ?? current?.Lf ?? false),
            managed || current?.Managed == true);

    private async Task<(bool Success, string Message, string? Root)> ResolveAsync(string project)
    {
        var resolved = await _projects.ResolveWorktreeAsync(project).ConfigureAwait(false);
        return resolved.Success
            ? (true, resolved.Message, resolved.Worktree!.WorktreePath)
            : (false, resolved.Message, null);
    }

    private static async Task<DocumentPair> ReadDocumentsAsync(string root, CancellationToken cancellation)
        => new(
            await TextDocument.ReadAsync(Path.Combine(root, ".gitignore"), cancellation).ConfigureAwait(false),
            await TextDocument.ReadAsync(Path.Combine(root, ".gitattributes"), cancellation).ConfigureAwait(false));

    private static async Task<(bool Success, string Message)> WriteDocumentsAsync(
        DocumentPair documents,
        string ignoreText,
        string attributeText,
        CancellationToken cancellation)
    {
        try
        {
            await documents.Ignore.WriteAsync(ignoreText, cancellation).ConfigureAwait(false);
            try
            {
                await documents.Attributes.WriteAsync(attributeText, cancellation).ConfigureAwait(false);
            }
            catch
            {
                await documents.Ignore.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            return (true, "规则文件已写入");
        }
        catch (Exception ex)
        {
            return (false, $"写入规则文件失败: {ex.Message}");
        }
    }

    private static string RewriteDocument(
        TextDocument document,
        IReadOnlyList<string> managedLines,
        Func<string, bool> removeOutside)
    {
        var output = new List<string>();
        var inserted = false;
        var inside = false;
        foreach (var line in document.Lines)
        {
            if (line.Trim().Equals(ManagedBegin, StringComparison.Ordinal))
            {
                if (!inserted)
                {
                    AppendManaged(output, managedLines);
                    inserted = true;
                }
                inside = true;
                continue;
            }
            if (inside)
            {
                if (line.Trim().Equals(ManagedEnd, StringComparison.Ordinal))
                    inside = false;
                continue;
            }
            if (!removeOutside(line))
                output.Add(line);
        }
        if (!inserted && managedLines.Count > 0)
        {
            if (output.Count > 0 && output[^1].Length > 0)
                output.Add(string.Empty);
            AppendManaged(output, managedLines);
        }
        while (output.Count > 0 && output[^1].Length == 0 && !document.EndsWithNewline)
            output.RemoveAt(output.Count - 1);
        var text = string.Join(document.Newline, output);
        if (text.Length > 0 && document.EndsWithNewline)
            text += document.Newline;
        return text;
    }

    private static void AppendManaged(List<string> output, IReadOnlyList<string> lines)
    {
        var normalized = lines.Where(line => line.Trim().Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0)
            return;
        output.Add(ManagedBegin);
        output.AddRange(normalized);
        output.Add(ManagedEnd);
    }

    private static IReadOnlyList<string> ManagedLines(TextDocument document)
        => Entries(document).Where(entry => entry.Managed).Select(entry => entry.Text).ToList();

    private static IEnumerable<LineEntry> Entries(TextDocument document)
    {
        var inside = false;
        foreach (var line in document.Lines)
        {
            var trimmed = line.Trim();
            // 两类托管块的行都算"托管"(基线块与项目块);块外为用户手写
            if (trimmed.Equals(ManagedBegin, StringComparison.Ordinal)
                || trimmed.Equals(BaselineBegin, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }
            if (inside && (trimmed.Equals(ManagedEnd, StringComparison.Ordinal)
                           || trimmed.Equals(BaselineEnd, StringComparison.Ordinal)))
            {
                inside = false;
                continue;
            }
            yield return new LineEntry(line, inside);
        }
    }

    /// <summary>
    /// 只重刷基线块:managed 块与块外手写内容逐字保留(A221-9)。
    /// 基线块统一置于文件最前,便于人一眼看出"哪些是全库统一的"。
    /// </summary>
    private static string RewriteBaselineBlock(TextDocument document, IReadOnlyList<string> baselineLines)
    {
        var output = new List<string>();
        var inside = false;
        foreach (var line in document.Lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Equals(BaselineBegin, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                if (trimmed.Equals(BaselineEnd, StringComparison.Ordinal))
                    inside = false;
                continue;
            }

            output.Add(line);
        }

        var normalized = baselineLines.Where(line => line.Trim().Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > 0)
        {
            var block = new List<string> { BaselineBegin };
            block.AddRange(normalized);
            block.Add(BaselineEnd);
            if (output.Count > 0 && output[0].Trim().Length > 0)
                block.Add(string.Empty);
            output.InsertRange(0, block);
        }

        while (output.Count > 0 && output[^1].Length == 0 && !document.EndsWithNewline)
            output.RemoveAt(output.Count - 1);

        var text = string.Join(document.Newline, output);
        if (text.Length > 0 && document.EndsWithNewline)
            text += document.Newline;
        return text;
    }

    private static string? ParseIgnorePattern(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!')
            ? null
            : trimmed;
    }

    private static (string Pattern, bool Lfs, bool Lf)? ParseCanonicalAttribute(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;
        var tokens = Regex.Split(trimmed, @"\s+");
        if (tokens.Length < 2)
            return null;
        var attrs = tokens.Skip(1).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lfs = attrs.SetEquals(new[] { "filter=lfs", "diff=lfs", "merge=lfs", "-text" });
        var lf = attrs.SetEquals(new[] { "text", "eol=lf" });
        return lfs || lf ? (tokens[0], lfs, lf) : null;
    }

    private static bool IgnorePatternEquals(string line, string pattern)
        => string.Equals(ParseIgnorePattern(line), pattern, StringComparison.OrdinalIgnoreCase);

    private static bool AttributePatternEquals(string line, string pattern)
        => string.Equals(ParseCanonicalAttribute(line)?.Pattern, pattern, StringComparison.OrdinalIgnoreCase);

    private static bool IsCanonicalAttributeForPattern(string line, string pattern)
        => AttributePatternEquals(line, pattern);

    private static bool LooksLikeFormatPattern(string pattern)
        => pattern.StartsWith("*.", StringComparison.Ordinal) && !pattern.Contains('/') && !pattern.Contains('\\');

    /// <summary>
    /// 目录规则形态(V2.2.1 §3.3):以 / 结尾的相对路径,如 Library/、b-Unity/Temp/。
    /// 禁绝对路径、盘符、`..`、反斜杠、通配符与空白——守卫等级沿 M1.5 路径纪律。
    /// </summary>
    public static bool LooksLikeDirectoryPattern(string pattern)
    {
        if (!pattern.EndsWith('/') || pattern.Length < 2 || pattern.Contains('\\')
            || pattern.Contains("..", StringComparison.Ordinal)
            || pattern.Contains(':') || pattern.StartsWith('/')
            || pattern.Contains('*') || pattern.Contains('?')
            || pattern.Any(char.IsWhiteSpace))
        {
            return false;
        }

        // 每段非空(排除 a//b 之类)
        return pattern.TrimEnd('/').Split('/').All(segment => segment.Length > 0);
    }

    private static string ValidatePattern(string pattern)
    {
        pattern = pattern.Trim().Replace('\\', '/');
        if (pattern.Length > 128 || pattern.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("pattern 长度须 ≤128 且不允许 ..");

        // 目录规则(以 / 结尾)与格式规则(*.ext)两种形态,由形态自动判别;
        // 不为目录另开指令(D221-9:避免同一件事两套入口)
        if (LooksLikeDirectoryPattern(pattern))
            return pattern;

        if (!LooksLikeFormatPattern(pattern) || pattern.Length <= 2
            || pattern.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                "pattern 须为文件格式(如 *.xlsx)或目录(以 / 结尾,如 Library/);不允许绝对路径、空白或 ..");
        }

        return pattern;
    }

    private static bool MatchesPattern(string pattern, string path)
    {
        // 目录规则:匹配该目录下的一切
        if (pattern.EndsWith('/'))
        {
            var prefix = pattern.TrimEnd('/');
            return path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)
                   || path.Contains("/" + prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        var fileName = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static IReadOnlyList<string> ParseNullPaths(string output)
        => output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath).Where(path => path.Length > 0).ToList();

    private static HashSet<string> ParseLfsJson(string output)
    {
        using var document = JsonDocument.Parse(output);
        var files = document.RootElement.GetProperty("files");
        if (files.ValueKind == JsonValueKind.Null)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return files.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => NormalizePath(name!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/');

    private static string ToAbsolute(string root, string path)
        => Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

    private static string BuildDiff(string file, string oldText, string newText)
    {
        if (oldText == newText)
            return $"{file}: (无变化)";
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder($"--- {file} (当前)\n+++ {file} (建议)");
        var max = Math.Max(oldLines.Length, newLines.Length);
        for (var index = 0; index < max; index++)
        {
            var oldLine = index < oldLines.Length ? oldLines[index] : null;
            var newLine = index < newLines.Length ? newLines[index] : null;
            if (oldLine == newLine)
                continue;
            if (oldLine != null)
                builder.Append("\n- ").Append(oldLine);
            if (newLine != null)
                builder.Append("\n+ ").Append(newLine);
        }
        return builder.ToString();
    }

    private static string PreviewMessage(GitFileRulePreview preview, bool apply)
        => $"{(apply ? "规则与索引已更新" : "规则预览，尚未写入")}：{preview.Pattern}" +
           $"\n影响文件 {preview.AffectedFiles}，加入索引 {preview.AddToIndex}，" +
           $"移出索引 {preview.RemoveFromIndex}，重新写入 {preview.Renormalize}" +
           $"\n{preview.GitIgnoreDiff}\n{preview.GitAttributesDiff}" +
           "\n不会自动 commit、push 或重写历史";

    private sealed record RuleDefinition(
        string Pattern, bool Track, bool Lfs, bool Lf, bool Managed);

    private sealed record LineEntry(string Text, bool Managed);

    private sealed record DocumentPair(TextDocument Ignore, TextDocument Attributes);

    private sealed class RepositoryState
    {
        public bool Success { get; private init; }
        public string Message { get; private init; } = string.Empty;
        public HashSet<string> Tracked { get; private init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Ignored { get; private init; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> AllFiles { get; private init; } = [];
        public HashSet<string> LfsPointers { get; private init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Attributes { get; private init; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static RepositoryState Failed(string message) => new() { Message = message };

        public static RepositoryState Ok(
            HashSet<string> tracked,
            HashSet<string> ignored,
            IReadOnlyList<string> all,
            HashSet<string> pointers,
            Dictionary<string, Dictionary<string, string>> attributes) => new()
        {
            Success = true,
            Tracked = tracked,
            Ignored = ignored,
            AllFiles = all,
            LfsPointers = pointers,
            Attributes = attributes,
        };
    }

    private sealed class TextDocument
    {
        private TextDocument(
            string path, bool existed, byte[] originalBytes, bool bom, string newline,
            bool endsWithNewline, string text, List<string> lines)
        {
            Path = path;
            Existed = existed;
            OriginalBytes = originalBytes;
            HasBom = bom;
            Newline = newline;
            EndsWithNewline = endsWithNewline;
            Text = text;
            Lines = lines;
        }

        public string Path { get; }
        public bool Existed { get; }
        public byte[] OriginalBytes { get; }
        public bool HasBom { get; }
        public string Newline { get; }
        public bool EndsWithNewline { get; }
        public string Text { get; }
        public List<string> Lines { get; }

        public static async Task<TextDocument> ReadAsync(string path, CancellationToken cancellation)
        {
            var existed = File.Exists(path);
            var bytes = existed ? await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false) : [];
            var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var offset = bom ? Encoding.UTF8.Preamble.Length : 0;
            var text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var ends = text.EndsWith("\n", StringComparison.Ordinal);
            var lines = text.Length == 0
                ? []
                : text.Replace("\r\n", "\n").Split('\n').ToList();
            if (ends && lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
            return new TextDocument(path, existed, bytes, bom, newline, ends, text, lines);
        }

        public async Task WriteAsync(string text, CancellationToken cancellation)
        {
            if (text == Text)
                return;
            var temp = Path + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllTextAsync(temp, text, new UTF8Encoding(HasBom), cancellation)
                    .ConfigureAwait(false);
                File.Move(temp, Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }

        public async Task RestoreAsync(CancellationToken cancellation)
        {
            if (!Existed)
            {
                if (File.Exists(Path))
                    File.Delete(Path);
                return;
            }
            await File.WriteAllBytesAsync(Path, OriginalBytes, cancellation).ConfigureAwait(false);
        }
    }
}
