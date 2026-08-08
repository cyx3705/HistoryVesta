using System.IO;
using System.Text;
using AppShell.Core.Commands;

namespace HistoryJanus.Git;

/// <summary>
/// proj.* 指令域注册。
/// 危险操作（delete / commitall / pushall / repair）统一经过总线 ConfirmPrompt；
/// 受保护分支在确认之前即由 ConfirmPrompt 返回 null + Handler 拒绝,不弹无意义确认框。
/// </summary>
public static class ProjectCommands
{
    private static CommandResult Fail(string message, object data)
        => new() { Success = false, Message = message, Data = data };

    private static RepositoryTarget ResolveTarget(CommandContext context)
        => ResolveRepositoryTarget(context.GetString("target"), context.GetBool("submodules"));

    public static RepositoryTarget ResolveRepositoryTarget(
        string? target, bool includeSubmodules)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            return target.Trim().ToLowerInvariant() switch
            {
                "submodules" => RepositoryTarget.Submodules,
                "both" => RepositoryTarget.Both,
                _ => RepositoryTarget.Parent,
            };
        }
        return includeSubmodules ? RepositoryTarget.Both : RepositoryTarget.Parent;
    }

    private static ParameterSpec TargetParameter() => new()
    {
        Name = "target",
        Description = "实际仓库目标；存在时优先于兼容参数 submodules",
        AllowedValues = ["parent", "submodules", "both"],
    };

    /// <summary>proj.commit / proj.commitall 共用的单项目提交留痕(R4:结果映射唯一实现)。</summary>
    private static void RecordCommit(HistoryRecorder history, string branch, string msg, CommitReport report)
    {
        var submodules = report.Submodules ?? [];
        var detail = submodules.Count == 0
            ? msg
            : $"{msg}; 子模块 成功={submodules.Count(item => item.Outcome == SubmoduleOperationOutcome.Success)} " +
              $"跳过={submodules.Count(item => item.Outcome == SubmoduleOperationOutcome.Skipped)} " +
              $"失败={submodules.Count(item => item.Outcome is SubmoduleOperationOutcome.Failed or SubmoduleOperationOutcome.Rejected)}";
        history.Record(branch, "commit", detail,
            report.Outcome switch
            {
                CommitOutcome.Success => "成功",
                CommitOutcome.Skipped => "跳过",
                CommitOutcome.Rejected => "拒绝",
                _ => "失败",
            },
            (report.HasSizeWarning ? 1 : 0) + (report.RejectedFiles?.Count ?? 0));
        RecordSubmodules(history, branch, "submodule.commit", submodules);
    }

    private static void RecordSubmodules(
        HistoryRecorder history,
        string parent,
        string action,
        IEnumerable<SubmoduleOperationEntry> entries)
    {
        foreach (var entry in entries)
        {
            history.Record($"{parent}/{entry.RelativePath}", action,
                $"branch={entry.Branch}; before={entry.BeforeSha}; after={entry.AfterSha}",
                entry.Outcome switch
                {
                    SubmoduleOperationOutcome.Success => "成功",
                    SubmoduleOperationOutcome.Skipped => "跳过",
                    SubmoduleOperationOutcome.Rejected => "拒绝",
                    _ => "失败",
                });
        }
    }

    public static void RegisterAll(
        CommandRegistry registry, ProjectService projects, HistoryRecorder history, string source = "app")
    {
        registry.Register(BuildList(projects), source);
        registry.Register(BuildCreate(projects, history), source);
        registry.Register(BuildDelete(projects, history), source);
        registry.Register(BuildTree(projects), source);
        registry.Register(BuildCommit(projects, history), source);
        registry.Register(BuildPush(projects, history), source);
        registry.Register(BuildCommitAll(projects, history), source);
        registry.Register(BuildPushAll(projects, history), source);
        registry.Register(BuildOpen(projects), source);
        registry.Register(BuildScan(projects), source);
        registry.Register(BuildRepair(projects, history), source);
        registry.Register(BuildConfig(projects), source);
        registry.Register(BuildNote(projects, history), source);
        registry.Register(BuildMetaList(projects), source);
        registry.Register(BuildMetaOpen(projects), source);
    }

    // ---------------------------------------------------------------- proj.list

    private static CommandDescriptor BuildList(ProjectService projects) => new()
    {
        Name = "proj.list",
        Summary = "列出全部项目工作树(编号/分支/路径/状态)",
        Readonly = true,
        Example = "proj.list filter=2026",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "filter",
                Description = "分支名关键字过滤(包含匹配,忽略大小写)",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var (git, worktrees) = await projects.ListWorktreesAsync();
            if (!git.Success)
                return CommandResult.Fail($"获取工作树列表失败:\n{git.Output}");

            var filter = ctx.GetString("filter");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                worktrees = worktrees
                    .Where(w => w.BranchName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (worktrees.Count == 0)
                return CommandResult.Ok("没有匹配的工作树", worktrees);

            var sb = new StringBuilder();
            sb.Append($"共 {worktrees.Count} 个工作树:");
            for (var i = 0; i < worktrees.Count; i++)
            {
                var w = worktrees[i];
                var time = w.LastCommitTime.Length > 0 ? $"  [{w.LastCommitTime}]" : "";
                var state = Directory.Exists(w.WorktreePath) ? "" : "  ⚠目录缺失(疑似断链,可 proj.repair)";
                sb.Append($"\n  {i + 1,3}. {w.BranchName}{time}  →  {w.WorktreePath}{state}");
            }

            return CommandResult.Ok(sb.ToString(), worktrees);
        },
    };

    // ---------------------------------------------------------------- proj.create

    private static CommandDescriptor BuildCreate(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.create",
        Summary = "创建新项目:新建分支 + 同名工作树(分支名 = 文件夹名)",
        Example = "proj.create name=2026-025-新项目",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "项目名称(合法文件夹字符)",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "base",
                Description = "基础分支(缺省取 proj.basebranch 配置)",
            },
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var (success, message) = await projects.CreateAsync(name, ctx.GetString("base"), ctx.Progress);
            history.Record(name, "create", ctx.GetString("base") ?? "", success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.delete

    private static CommandDescriptor BuildDelete(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.delete",
        Summary = "删除项目:移除工作树 + 强制删除分支(不可撤销;受保护分支拒绝)",
        Example = "proj.delete name=9999-901-测试",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "要删除的分支名(= 工作树文件夹名)",
                Required = true,
                Position = 0,
            },
        ],
        // 受保护分支返回 null 跳过确认,由 Handler 直接拒绝(不弹无意义的确认框)
        ConfirmPrompt = ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            if (projects.IsProtected(name))
                return null;
            return $"你即将执行以下【不可撤销】的操作:\n\n" +
                   $"• 删除 Git 分支: {name}\n" +
                   $"• 删除工作树目录: {Path.Combine(projects.WorktreeRoot, name)}\n\n" +
                   $"工作树内所有未提交的修改、未跟踪文件都将被永久删除!\n确定要继续吗?";
        },
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var (success, message) = await projects.DeleteAsync(name, ctx.Progress);
            history.Record(name, "delete", "", success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.tree

    private static CommandDescriptor BuildTree(ProjectService projects) => new()
    {
        Name = "proj.tree",
        Summary = "输出分支继承树(默认读文件缓存秒开;refresh=true 重新扫描并更新缓存)",
        Readonly = true,
        Example = "proj.tree refresh=true",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "refresh",
                Description = "true 时忽略缓存重新扫描裸仓库",
                Type = ParamType.Bool,
                Default = "false",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "cached",
                Description = "true 时仅读缓存,无缓存不触发扫描(视图自动加载用)",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Handler = async ctx =>
        {
            var (success, message, root) = await projects.BuildTreeAsync(
                ctx.Progress, ctx.GetBool("refresh"), ctx.GetBool("cached"));
            return success
                ? CommandResult.Ok(message, root == null ? null : BranchTreeNode.FromDomain(root))
                : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.commit

    private static CommandDescriptor BuildCommit(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.commit",
        Summary = "提交单个项目到本地仓库；可按子模块先、父项目后联动提交",
        Example = "proj.commit name=2026-018-MyAPI msg=\"更新说明\" target=both",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "分支名(= 工作树文件夹名)",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "msg",
                Description = "提交描述(Commit Message)",
                Required = true,
                Position = 1,
            },
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
            new ParameterSpec
            {
                Name = "submsg",
                Description = "子模块统一提交描述；空时沿用 msg",
            },
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var msg = ctx.RequireString("msg");
            var target = ResolveTarget(ctx);
            var report = await projects.CommitAsync(name, msg, ctx.Progress,
                target, ctx.GetString("submsg"), ctx.Cancellation);
            RecordCommit(history, name, msg, report);
            return report.Outcome switch
            {
                CommitOutcome.Success => CommandResult.Ok(
                    report.HasSizeWarning ? report.Message + "(含大文件警告,见上方明细)" : report.Message,
                    report),
                CommitOutcome.Skipped => CommandResult.Ok(report.Message, report),
                CommitOutcome.Rejected => Fail(report.Message, report),
                _ => Fail(report.Message, report),
            };
        },
    };

    // ---------------------------------------------------------------- proj.push

    private static CommandDescriptor BuildPush(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.push",
        Summary = "推送单个分支；可先推直属子模块，全部成功后再推父项目",
        Example = "proj.push name=2026-018-MyAPI target=both",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "分支名(= 工作树文件夹名)",
                Required = true,
                Position = 0,
            },
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var report = await projects.PushAsync(name, ResolveTarget(ctx), ctx.Cancellation);
            history.Record(name, "push",
                $"target={report.Target}; 子模块={report.Submodules?.Count ?? 0}; " +
                $"parentPushed={report.ParentPushed}; pointerPending={report.ParentPointerPending}",
                report.Success ? "成功" : "失败");
            RecordSubmodules(history, name, "submodule.push", report.Submodules ?? []);
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };

    // ---------------------------------------------------------------- proj.commitall

    private static CommandDescriptor BuildCommitAll(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.commitall",
        Summary = "一键提交全部工作树；可联动各项目直属子模块",
        Example = "proj.commitall msg=\"每日推送\" target=both",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "msg",
                Description = "统一提交描述",
                Required = true,
                Position = 0,
            },
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
            new ParameterSpec
            {
                Name = "submsg",
                Description = "全部子模块统一提交描述；空时沿用 msg",
            },
        ],
        ConfirmPrompt = ctx =>
            "确定要对全部工作树执行 git add . & git commit 吗?\n\n" +
            "每个项目提交前将检查文件大小:\n" +
            "• ≥ 警告阈值的文件会警告后继续\n" +
            "• ≥ LFS 阈值的文件检查 LFS 状态,未启用则逐项目询问" +
            (ResolveTarget(ctx) switch
            {
                RepositoryTarget.Submodules => "\n• 本次只提交子模块，父分支将保留待收口 gitlink",
                RepositoryTarget.Both => "\n• 子模块先提交，父项目后提交；多仓库无法原子回退",
                _ => string.Empty,
            }),
        Handler = async ctx =>
        {
            var msg = ctx.RequireString("msg");
            var target = ResolveTarget(ctx);
            var report = await projects.CommitAllAsync(msg, ctx.Progress,
                (branch, item) => RecordCommit(history, branch, msg, item),
                target, ctx.GetString("submsg"), ctx.Cancellation);
            history.Record("(全部)", "commitall", msg, report.Success ? "成功" : "失败");
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };

    // ---------------------------------------------------------------- proj.pushall

    private static CommandDescriptor BuildPushAll(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.pushall",
        Summary = "推送全部分支；可先去重推送所有直属子模块",
        Example = "proj.pushall target=both",
        Parameters =
        [
            TargetParameter(),
            new ParameterSpec
            {
                Name = "submodules",
                Description = "兼容参数：true=both，false=parent；target 存在时忽略",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
        ConfirmPrompt = ctx =>
            "确定要执行 git push --all origin 吗?\n\n此操作会推送裸仓库中的所有本地分支到 GitHub。" +
            (ResolveTarget(ctx) switch
            {
                RepositoryTarget.Submodules => "\n本次只推送全部直属子模块，父分支不会推送。",
                RepositoryTarget.Both => "\n直属子模块将先推送，任一失败都会阻止父仓库推送。",
                _ => string.Empty,
            }),
        Handler = async ctx =>
        {
            var report = await projects.PushAllAsync(ResolveTarget(ctx), ctx.Cancellation);
            history.Record("(全部)", "pushall", $"子模块={report.Submodules.Count}",
                report.Success ? "成功" : "失败");
            RecordSubmodules(history, "(全部)", "submodule.push", report.Submodules);
            return report.Success
                ? CommandResult.Ok(report.Message, report)
                : Fail(report.Message, report);
        },
    };

    // ---------------------------------------------------------------- proj.open

    private static CommandDescriptor BuildOpen(ProjectService projects) => new()
    {
        Name = "proj.open",
        Summary = "在系统资源管理器中打开项目工作树(不带 name 打开工作树根目录)",
        Example = "proj.open name=2026-018-MyAPI",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "分支名(= 工作树文件夹名);省略打开根目录",
                Position = 0,
            },
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var (success, message) = projects.OpenFolder(ctx.GetString("name"));
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        }),
    };

    // ---------------------------------------------------------------- proj.scan

    private static CommandDescriptor BuildScan(ProjectService projects) => new()
    {
        Name = "proj.scan",
        Summary = "扫描项目大文件并输出分级报告(不提交)",
        Readonly = true,
        Example = "proj.scan name=2026-018-MyAPI",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "分支名(= 工作树文件夹名)",
                Required = true,
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var (success, message) = await projects.ScanAsync(ctx.RequireString("name"));
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.repair

    private static CommandDescriptor BuildRepair(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.repair",
        Summary = "worktree 断链批量修复:删除全部工作树目录→prune→按分支清单重建",
        Example = "proj.repair",
        ConfirmPrompt = _ =>
            "【高危】worktree 断链批量修复将:\n\n" +
            "1. 删除工作树根目录下全部现有工作树文件夹\n" +
            "  (所有未提交修改、未跟踪文件将永久丢失!)\n" +
            "2. git worktree prune 清理残留记录\n" +
            "3. 按裸仓库分支清单重建全部 worktree(目录名 = 分支名)\n\n" +
            "已提交到裸仓库的数据不受影响。确定要继续吗?",
        Handler = async ctx =>
        {
            var (success, message) = await projects.RepairAsync(ctx.Progress);
            history.Record("(全部)", "repair", "", success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.note

    private static CommandDescriptor BuildNote(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.note",
        Summary = "写入/更新分支的项目描述(继承树与 proj.tree 优先显示此描述)",
        Example = "proj.note name=2026-018-MyAPI text=\"基础设施整合项目\"",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "name",
                Description = "分支名",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "text",
                Description = "项目描述文本",
                Required = true,
                Position = 1,
            },
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            var text = ctx.RequireString("text").Trim();
            history.SetNote(name, text);
            return CommandResult.Ok($"已记录分支描述: {name} → {text}");
        }),
    };

    // ---------------------------------------------------------------- proj.config

    private static CommandDescriptor BuildConfig(ProjectService projects) => new()
    {
        Name = "proj.config",
        Summary = "显示 proj.* 当前生效配置(经 app.set 修改)",
        Readonly = true,
        Example = "proj.config",
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(projects.DescribeConfig())),
    };

    // ---------------------------------------------------------------- proj.metalist

    private static CommandDescriptor BuildMetaList(ProjectService projects) => new()
    {
        Name = "proj.metalist",
        Summary = "列出全部项目根下以 z/Z 开头的一级元文件夹",
        Readonly = true,
        Example = "proj.metalist filter=AD",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "filter",
                Description = "项目名/元文件夹名/路径关键字过滤(包含匹配,忽略大小写)",
                Position = 0,
            },
        ],
        Handler = async ctx =>
        {
            var (git, metas, warnings) = await projects.ListMetaFoldersAsync();
            if (!git.Success)
                return CommandResult.Fail($"获取工作树列表失败:\n{git.Output}");

            var filter = ctx.GetString("filter");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                metas = metas
                    .Where(m =>
                        m.ProjectName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        m.MetaName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        m.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var sb = new StringBuilder();
            if (metas.Count == 0)
            {
                sb.Append("没有匹配的元文件夹");
            }
            else
            {
                sb.Append($"共 {metas.Count} 个元文件夹:");
                for (var i = 0; i < metas.Count; i++)
                {
                    var m = metas[i];
                    var time = m.LastWriteTime.Length > 0 ? $"  [{m.LastWriteTime}]" : "";
                    sb.Append($"\n  {i + 1,3}. {m.ProjectName} / {m.MetaName}{time}  →  {m.FullPath}");
                }
            }

            if (warnings.Count > 0)
            {
                sb.Append($"\n⚠ {warnings.Count} 个项目扫描失败(已跳过):");
                foreach (var w in warnings)
                    sb.Append($"\n  · {w}");
            }

            return CommandResult.Ok(sb.ToString(), metas);
        },
    };

    // ---------------------------------------------------------------- proj.metaopen

    private static CommandDescriptor BuildMetaOpen(ProjectService projects) => new()
    {
        Name = "proj.metaopen",
        Summary = "在系统资源管理器中打开指定元文件夹(path= 或 name=+meta=)",
        Example = "proj.metaopen name=2026-016-AD学习 meta=z-AD库文件汇总",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "path",
                Description = "元文件夹完整路径",
            },
            new ParameterSpec
            {
                Name = "name",
                Description = "所属项目分支名(= 工作树文件夹名)",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "meta",
                Description = "元文件夹名(须以 z/Z 开头)",
                Position = 1,
            },
        ],
        Handler = async ctx =>
        {
            var (success, message) = await projects.OpenMetaFolderAsync(
                ctx.GetString("path"),
                ctx.GetString("name"),
                ctx.GetString("meta"));
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };
}
