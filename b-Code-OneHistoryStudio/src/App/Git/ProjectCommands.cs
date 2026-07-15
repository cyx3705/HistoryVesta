using System.IO;
using System.Text;
using AppShell.Core.Commands;

namespace OneHistoryStudio.Git;

/// <summary>
/// proj.* 指令域注册(PJ-01~PJ-13)。
/// 危险操作(delete / commitall / pushall / repair)经总线 ConfirmPrompt 单闸口(N-04);
/// 受保护分支在确认之前即由 ConfirmPrompt 返回 null + Handler 拒绝,不弹无意义确认框。
/// </summary>
public static class ProjectCommands
{
    public static void RegisterAll(CommandRegistry registry, ProjectService projects, HistoryRecorder history)
    {
        registry.Register(BuildList(projects));
        registry.Register(BuildCreate(projects, history));
        registry.Register(BuildDelete(projects, history));
        registry.Register(BuildTree(projects));
        registry.Register(BuildCommit(projects, history));
        registry.Register(BuildPush(projects, history));
        registry.Register(BuildCommitAll(projects, history));
        registry.Register(BuildPushAll(projects, history));
        registry.Register(BuildOpen(projects));
        registry.Register(BuildScan(projects));
        registry.Register(BuildRepair(projects, history));
        registry.Register(BuildConfig(projects));
        registry.Register(BuildNote(projects, history));
        registry.Register(BuildMetaList(projects));
        registry.Register(BuildMetaOpen(projects));
    }

    // ---------------------------------------------------------------- proj.list(PJ-01)

    private static CommandDescriptor BuildList(ProjectService projects) => new()
    {
        Name = "proj.list",
        Summary = "列出全部项目工作树(编号/分支/路径/状态)",
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

    // ---------------------------------------------------------------- proj.create(PJ-02)

    private static CommandDescriptor BuildCreate(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.create",
        Summary = "创建新项目:新建分支 + 同名工作树(分支名 = 文件夹名)",
        Example = "proj.create name=2026-020-新项目",
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

    // ---------------------------------------------------------------- proj.delete(PJ-03)

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

    // ---------------------------------------------------------------- proj.tree(PJ-04)

    private static CommandDescriptor BuildTree(ProjectService projects) => new()
    {
        Name = "proj.tree",
        Summary = "输出分支继承树(默认读文件缓存秒开;refresh=true 重新扫描并更新缓存)",
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
            return success ? CommandResult.Ok(message, root) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.commit(PJ-05)

    private static CommandDescriptor BuildCommit(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.commit",
        Summary = "提交单个项目到本地裸仓库(大小检查→LFS 处理→add→commit)",
        Example = "proj.commit name=2026-018-MyAPI msg=\"更新说明\"",
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
        ],
        Handler = async ctx =>
        {
            var name = ctx.RequireString("name");
            var msg = ctx.RequireString("msg");
            var report = await projects.CommitAsync(name, msg, ctx.Progress);
            history.Record(name, "commit", msg,
                report.Outcome switch
                {
                    CommitOutcome.Success => "成功",
                    CommitOutcome.Skipped => "跳过",
                    CommitOutcome.Rejected => "拒绝",
                    _ => "失败",
                },
                (report.HasSizeWarning ? 1 : 0) + (report.RejectedFiles?.Count ?? 0));
            return report.Outcome switch
            {
                CommitOutcome.Success => CommandResult.Ok(
                    report.HasSizeWarning ? report.Message + "(含大文件警告,见上方明细)" : report.Message),
                CommitOutcome.Skipped => CommandResult.Ok(report.Message),
                CommitOutcome.Rejected => CommandResult.Fail(report.Message),
                _ => CommandResult.Fail(report.Message),
            };
        },
    };

    // ---------------------------------------------------------------- proj.push(PJ-06)

    private static CommandDescriptor BuildPush(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.push",
        Summary = "推送单个分支到 GitHub(git push origin 分支名)",
        Example = "proj.push name=2026-018-MyAPI",
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
            var name = ctx.RequireString("name");
            var (success, message) = await projects.PushAsync(name);
            history.Record(name, "push", "", success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.commitall(PJ-07)

    private static CommandDescriptor BuildCommitAll(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.commitall",
        Summary = "一键提交全部工作树到本地裸仓库(逐项大小检查,汇总四类结果)",
        Example = "proj.commitall msg=\"每日推送\"",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "msg",
                Description = "统一提交描述",
                Required = true,
                Position = 0,
            },
        ],
        ConfirmPrompt = _ =>
            "确定要对全部工作树执行 git add . & git commit 吗?\n\n" +
            "每个项目提交前将检查文件大小:\n" +
            "• ≥ 警告阈值的文件会警告后继续\n" +
            "• ≥ LFS 阈值的文件检查 LFS 状态,未启用则逐项目询问",
        Handler = async ctx =>
        {
            var msg = ctx.RequireString("msg");
            var (success, message) = await projects.CommitAllAsync(msg, ctx.Progress,
                (branch, report) => history.Record(branch, "commit", msg,
                    report.Outcome switch
                    {
                        CommitOutcome.Success => "成功",
                        CommitOutcome.Skipped => "跳过",
                        CommitOutcome.Rejected => "拒绝",
                        _ => "失败",
                    },
                    (report.HasSizeWarning ? 1 : 0) + (report.RejectedFiles?.Count ?? 0)));
            history.Record("(全部)", "commitall", msg, success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.pushall(PJ-08)

    private static CommandDescriptor BuildPushAll(ProjectService projects, HistoryRecorder history) => new()
    {
        Name = "proj.pushall",
        Summary = "推送全部分支到 GitHub(git push --all origin)",
        Example = "proj.pushall",
        ConfirmPrompt = _ =>
            "确定要执行 git push --all origin 吗?\n\n此操作会推送裸仓库中的所有本地分支到 GitHub。",
        Handler = async _ =>
        {
            var (success, message) = await projects.PushAllAsync();
            history.Record("(全部)", "pushall", "", success ? "成功" : "失败");
            return success ? CommandResult.Ok(message) : CommandResult.Fail(message);
        },
    };

    // ---------------------------------------------------------------- proj.open(PJ-09)

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

    // ---------------------------------------------------------------- proj.scan(PJ-10)

    private static CommandDescriptor BuildScan(ProjectService projects) => new()
    {
        Name = "proj.scan",
        Summary = "扫描项目大文件并输出分级报告(不提交)",
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

    // ---------------------------------------------------------------- proj.repair(PJ-11)

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

    // ---------------------------------------------------------------- proj.note(DT-02)

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

    // ---------------------------------------------------------------- proj.config(PJ-12)

    private static CommandDescriptor BuildConfig(ProjectService projects) => new()
    {
        Name = "proj.config",
        Summary = "显示 proj.* 当前生效配置(经 app.set 修改)",
        Example = "proj.config",
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(projects.DescribeConfig())),
    };

    // ---------------------------------------------------------------- proj.metalist(V2.0.1 MF-10)

    private static CommandDescriptor BuildMetaList(ProjectService projects) => new()
    {
        Name = "proj.metalist",
        Summary = "列出全部项目根下以 z/Z 开头的一级元文件夹",
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

    // ---------------------------------------------------------------- proj.metaopen(V2.0.1 MF-12)

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
