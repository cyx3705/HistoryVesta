using AppShell.Core.Data;
using AppShell.Core.Mcp;
using AppShell.Core.Logging;

namespace AppShell.Services.Mcp;

public sealed record PromptRevision(
    string Id,
    string Command,
    string? Description,
    string? ParentRevision,
    string Source,
    string Reason,
    string Created,
    string CreatedBy,
    bool Applied,
    string? RevertedFrom);

public sealed record PromptProposal(
    string Id,
    string Command,
    string? BaseRevision,
    string OldText,
    string ProposedText,
    string Reason,
    string Evidence,
    string SourceClient,
    string Created,
    string Status,
    string? Reviewer,
    string? Reviewed,
    string? ReviewNote,
    string? AppliedRevision);

public sealed record PromptCorrection(
    string Id,
    string Command,
    string Claim,
    string Correction,
    string Evidence,
    string SourceClient,
    string Created,
    string Status,
    string? LinkedProposal);

public sealed record PromptIncident(
    string Id,
    string Command,
    string Symptom,
    string Expected,
    string Actual,
    string Evidence,
    string SourceClient,
    string Created,
    string? Resolution,
    string? LinkedCorrection);

/// <summary>
/// MCP 工具描述治理存储(V2.1.2):修订是生效真值，提案/勘误/事故为追加式审计记录。
/// V2.1.1 mcp_descriptions 仅作为迁移来源和降级兼容镜像。
/// </summary>
public sealed class PromptGovernanceStore
{
    public const string TableDescriptions = "mcp_descriptions";
    public const string TableProposals = "mcp_prompt_proposals";

    private readonly IDataService _data;
    private readonly IShellLog _log;
    private readonly object _writeGate = new();

    public PromptGovernanceStore(IDataService data, IShellLog log)
    {
        _data = data;
        _log = log;
        Initialize();
    }

    public IReadOnlyDictionary<string, string> AllEffectiveDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var result = Read(
                "SELECT command, description FROM mcp_prompt_revisions " +
                "WHERE applied=1 AND description IS NOT NULL");
            if (result != null)
            {
                foreach (var row in result.Rows)
                {
                    var command = Text(row[0]);
                    var description = Text(row[1]);
                    if (!string.IsNullOrWhiteSpace(command) && description != null)
                        map[command] = description;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("prompt", $"读取生效提示词失败: {ex.Message}");
        }

        return map;
    }

    public PromptRevision? GetCurrentRevision(string command)
        => ReadRevisions(
                "SELECT id,command,description,parent_revision,source,reason,created,created_by,applied,reverted_from " +
                $"FROM mcp_prompt_revisions WHERE command={Sql(command)} AND applied=1 LIMIT 1")
            .FirstOrDefault();

    public PromptRevision? GetRevision(string id)
        => ReadRevisions(
                "SELECT id,command,description,parent_revision,source,reason,created,created_by,applied,reverted_from " +
                $"FROM mcp_prompt_revisions WHERE id={Sql(id)} LIMIT 1")
            .FirstOrDefault();

    public IReadOnlyList<PromptRevision> GetRevisions(string command, int limit = 50)
        => ReadRevisions(
            "SELECT id,command,description,parent_revision,source,reason,created,created_by,applied,reverted_from " +
            $"FROM mcp_prompt_revisions WHERE command={Sql(command)} ORDER BY created DESC LIMIT {ClampLimit(limit)}");

    public PromptRevision ApplyDirect(
        string command, string? description, string source, string reason,
        string? revertedFrom = null, string? createdBy = null)
    {
        if (description != null)
            description = PromptTextIntegrity.ValidateDescription(description);

        lock (_writeGate)
        {
            var current = GetCurrentRevision(command);
            var revision = new PromptRevision(
                NewId("rev"), command, description, current?.Id, source, reason,
                Now(), createdBy ?? source, true, revertedFrom);

            var mirror = description == null
                ? $"DELETE FROM {TableDescriptions} WHERE command={Sql(command)};"
                : $"INSERT OR REPLACE INTO {TableDescriptions} (command,description,updated) VALUES " +
                  $"({Sql(command)},{Sql(description)},{Sql(revision.Created)});";

            Execute(
                "BEGIN IMMEDIATE;" +
                $"UPDATE mcp_prompt_revisions SET applied=0 WHERE command={Sql(command)} AND applied=1;" +
                InsertRevisionSql(revision) +
                mirror +
                "COMMIT;");
            return revision;
        }
    }

    public PromptProposal CreateProposal(
        string command, string oldText, string proposedText, string reason, string evidence, string sourceClient)
    {
        proposedText = PromptTextIntegrity.ValidateDescription(proposedText);
        var proposal = new PromptProposal(
            NewId("proposal"), command, GetCurrentRevision(command)?.Id,
            oldText, proposedText, reason, evidence, sourceClient, Now(),
            "pending", null, null, null, null);

        Execute(
            $"INSERT INTO {TableProposals} " +
            "(id,command,base_revision,old_text,proposed_text,reason,evidence,source_client,created,status) VALUES " +
            $"({Sql(proposal.Id)},{Sql(command)},{SqlNullable(proposal.BaseRevision)},{Sql(oldText)}," +
            $"{Sql(proposedText)},{Sql(reason)},{Sql(evidence)},{Sql(sourceClient)},{Sql(proposal.Created)},'pending')");
        return proposal;
    }

    public PromptProposal? GetProposal(string id)
        => ReadProposals(ProposalSelect + $" WHERE id={Sql(id)} LIMIT 1").FirstOrDefault();

    public IReadOnlyList<PromptProposal> ListProposals(
        string? command = null, bool openOnly = false, int limit = 100)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(command))
            clauses.Add($"command={Sql(command)}");
        if (openOnly)
            clauses.Add("status IN ('pending','approved')");
        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
        return ReadProposals(ProposalSelect + where + $" ORDER BY created DESC LIMIT {ClampLimit(limit)}");
    }

    public PromptProposal ApproveProposal(string id, string reviewer)
    {
        var proposal = RequireProposal(id);
        if (!proposal.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"提案状态为 {proposal.Status}，只有 pending 可批准");
        PromptTextIntegrity.ValidateDescription(proposal.ProposedText);

        Execute(
            $"UPDATE {TableProposals} SET status='approved'," +
            $"reviewer={Sql(reviewer)},reviewed={Sql(Now())} WHERE id={Sql(id)} AND status='pending'");
        return RequireProposal(id);
    }

    public PromptProposal RejectProposal(string id, string reviewer, string reason)
    {
        var proposal = RequireProposal(id);
        if (proposal.Status is not ("pending" or "approved"))
            throw new InvalidOperationException($"提案状态为 {proposal.Status}，不能拒绝");

        Execute(
            $"UPDATE {TableProposals} SET status='rejected'," +
            $"reviewer={Sql(reviewer)},reviewed={Sql(Now())},review_note={Sql(reason)} " +
            $"WHERE id={Sql(id)} AND status IN ('pending','approved')");
        return RequireProposal(id);
    }

    public PromptRevision ApplyProposal(string id, string reviewer)
    {
        lock (_writeGate)
        {
            var proposal = RequireProposal(id);
            if (!proposal.Status.Equals("approved", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"提案状态为 {proposal.Status}，必须先批准再应用");
            PromptTextIntegrity.ValidateDescription(proposal.ProposedText);

            var current = GetCurrentRevision(proposal.Command);
            if (!string.Equals(current?.Id, proposal.BaseRevision, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"提案基线冲突: 创建时={proposal.BaseRevision ?? "(默认)"}，当前={current?.Id ?? "(默认)"}");

            var revision = new PromptRevision(
                NewId("rev"), proposal.Command, proposal.ProposedText, current?.Id,
                "proposal", proposal.Reason, Now(), reviewer, true, null);
            Execute(
                "BEGIN IMMEDIATE;" +
                $"UPDATE mcp_prompt_revisions SET applied=0 WHERE command={Sql(proposal.Command)} AND applied=1;" +
                InsertRevisionSql(revision) +
                $"INSERT OR REPLACE INTO {TableDescriptions} (command,description,updated) VALUES " +
                $"({Sql(proposal.Command)},{Sql(proposal.ProposedText)},{Sql(revision.Created)});" +
                $"UPDATE {TableProposals} SET status='applied'," +
                $"reviewer={Sql(reviewer)},reviewed={Sql(Now())},applied_revision={Sql(revision.Id)} " +
                $"WHERE id={Sql(id)} AND status='approved';" +
                "COMMIT;");
            return revision;
        }
    }

    public PromptRevision RevertToRevision(string revisionId, string reviewer, string reason)
    {
        var target = GetRevision(revisionId)
                     ?? throw new InvalidOperationException($"修订不存在: {revisionId}");
        return ApplyDirect(target.Command, target.Description, "revert", reason, target.Id, reviewer);
    }

    public PromptCorrection CreateCorrection(
        string command, string claim, string correction, string evidence, string sourceClient,
        string? linkedProposal = null)
    {
        var item = new PromptCorrection(
            NewId("correction"), command, claim, correction, evidence,
            sourceClient, Now(), "pending", linkedProposal);
        Execute(
            "INSERT INTO mcp_corrections " +
            "(id,command,claim,correction,evidence,source_client,created,status,linked_proposal) VALUES " +
            $"({Sql(item.Id)},{Sql(command)},{Sql(claim)},{Sql(correction)},{Sql(evidence)}," +
            $"{Sql(sourceClient)},{Sql(item.Created)},'pending',{SqlNullable(linkedProposal)})");
        return item;
    }

    public PromptCorrection? GetCorrection(string id)
        => ReadCorrections(
                "SELECT id,command,claim,correction,evidence,source_client,created,status,linked_proposal " +
                $"FROM mcp_corrections WHERE id={Sql(id)} LIMIT 1")
            .FirstOrDefault();

    public IReadOnlyList<PromptCorrection> ListCorrections(string? command = null, int limit = 100)
    {
        var where = string.IsNullOrWhiteSpace(command) ? "" : $" WHERE command={Sql(command)}";
        return ReadCorrections(
            "SELECT id,command,claim,correction,evidence,source_client,created,status,linked_proposal " +
            $"FROM mcp_corrections{where} ORDER BY created DESC LIMIT {ClampLimit(limit)}");
    }

    public PromptIncident CreateIncident(
        string command, string symptom, string expected, string actual, string evidence, string sourceClient,
        string? linkedCorrection = null)
    {
        var item = new PromptIncident(
            NewId("incident"), command, symptom, expected, actual, evidence,
            sourceClient, Now(), null, linkedCorrection);
        Execute(
            "INSERT INTO mcp_incidents " +
            "(id,command,symptom,expected,actual,evidence,source_client,created,linked_correction) VALUES " +
            $"({Sql(item.Id)},{Sql(command)},{Sql(symptom)},{Sql(expected)},{Sql(actual)}," +
            $"{Sql(evidence)},{Sql(sourceClient)},{Sql(item.Created)},{SqlNullable(linkedCorrection)})");
        return item;
    }

    public IReadOnlyList<PromptIncident> ListIncidents(string? command = null, int limit = 100)
    {
        var where = string.IsNullOrWhiteSpace(command) ? "" : $" WHERE command={Sql(command)}";
        var result = Read(
            "SELECT id,command,symptom,expected,actual,evidence,source_client,created,resolution,linked_correction " +
            $"FROM mcp_incidents{where} ORDER BY created DESC LIMIT {ClampLimit(limit)}");
        return result?.Rows.Select(r => new PromptIncident(
            Text(r[0])!, Text(r[1])!, Text(r[2])!, Text(r[3])!, Text(r[4])!, Text(r[5]) ?? "",
            Text(r[6])!, Text(r[7])!, Text(r[8]), Text(r[9]))).ToList() ?? [];
    }

    private void Initialize()
    {
        try
        {
            Execute(
                $"""
                CREATE TABLE IF NOT EXISTS {TableDescriptions} (
                    command TEXT PRIMARY KEY,
                    description TEXT NOT NULL,
                    updated TEXT NOT NULL
                )
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS mcp_prompt_revisions (
                    id TEXT PRIMARY KEY,
                    command TEXT NOT NULL,
                    description TEXT,
                    parent_revision TEXT,
                    source TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    created TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    applied INTEGER NOT NULL DEFAULT 0,
                    reverted_from TEXT
                )
                """);
            Execute(
                """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_mcp_prompt_active
                ON mcp_prompt_revisions(command) WHERE applied=1
                """);
            Execute(
                $"""
                CREATE TABLE IF NOT EXISTS {TableProposals} (
                    id TEXT PRIMARY KEY,
                    command TEXT NOT NULL,
                    base_revision TEXT,
                    old_text TEXT NOT NULL,
                    proposed_text TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    evidence TEXT NOT NULL DEFAULT '',
                    source_client TEXT NOT NULL,
                    created TEXT NOT NULL,
                    status TEXT NOT NULL,
                    reviewer TEXT,
                    reviewed TEXT,
                    review_note TEXT,
                    applied_revision TEXT
                )
                """);
            EnsureColumn(TableProposals, "review_note", "TEXT");
            Execute($"CREATE INDEX IF NOT EXISTS ix_mcp_prompt_proposals_command ON {TableProposals}(command,created)");
            Execute(
                """
                CREATE TABLE IF NOT EXISTS mcp_corrections (
                    id TEXT PRIMARY KEY,
                    command TEXT NOT NULL,
                    claim TEXT NOT NULL,
                    correction TEXT NOT NULL,
                    evidence TEXT NOT NULL DEFAULT '',
                    source_client TEXT NOT NULL,
                    created TEXT NOT NULL,
                    status TEXT NOT NULL,
                    linked_proposal TEXT
                )
                """);
            Execute(
                """
                CREATE TABLE IF NOT EXISTS mcp_incidents (
                    id TEXT PRIMARY KEY,
                    command TEXT NOT NULL,
                    symptom TEXT NOT NULL,
                    expected TEXT NOT NULL,
                    actual TEXT NOT NULL,
                    evidence TEXT NOT NULL DEFAULT '',
                    source_client TEXT NOT NULL,
                    created TEXT NOT NULL,
                    resolution TEXT,
                    linked_correction TEXT
                )
                """);

            Execute(
                $"""
                INSERT INTO mcp_prompt_revisions
                    (id,command,description,parent_revision,source,reason,created,created_by,applied)
                SELECT 'rev_' || lower(hex(randomblob(16))), d.command, d.description, NULL,
                       'migration', 'V2.1.1 覆盖迁移', d.updated, 'migration', 1
                FROM {TableDescriptions} d
                WHERE NOT EXISTS (
                    SELECT 1 FROM mcp_prompt_revisions r WHERE r.command=d.command
                )
                """);
        }
        catch (Exception ex)
        {
            _log.Error("prompt", $"提示词治理表初始化失败: {ex.Message}");
            throw;
        }
    }

    private PromptProposal RequireProposal(string id)
        => GetProposal(id) ?? throw new InvalidOperationException($"提案不存在: {id}");

    private IReadOnlyList<PromptRevision> ReadRevisions(string sql)
    {
        var result = Read(sql);
        return result?.Rows.Select(r => new PromptRevision(
            Text(r[0])!, Text(r[1])!, Text(r[2]), Text(r[3]), Text(r[4])!, Text(r[5])!,
            Text(r[6])!, Text(r[7])!, Flag(r[8]), Text(r[9]))).ToList() ?? [];
    }

    private IReadOnlyList<PromptProposal> ReadProposals(string sql)
    {
        var result = Read(sql);
        return result?.Rows.Select(r => new PromptProposal(
            Text(r[0])!, Text(r[1])!, Text(r[2]), Text(r[3])!, Text(r[4])!, Text(r[5])!,
            Text(r[6]) ?? "", Text(r[7])!, Text(r[8])!, Text(r[9])!, Text(r[10]), Text(r[11]),
            Text(r[12]), Text(r[13]))).ToList() ?? [];
    }

    private IReadOnlyList<PromptCorrection> ReadCorrections(string sql)
    {
        var result = Read(sql);
        return result?.Rows.Select(r => new PromptCorrection(
            Text(r[0])!, Text(r[1])!, Text(r[2])!, Text(r[3])!, Text(r[4]) ?? "",
            Text(r[5])!, Text(r[6])!, Text(r[7])!, Text(r[8]))).ToList() ?? [];
    }

    private QueryResult? Read(string sql) => _data.ExecuteSql(sql).Result;

    private void Execute(string sql) => _data.ExecuteSql(sql);

    private void EnsureColumn(string table, string column, string type)
    {
        if (_data.GetSchema(table).Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
            return;
        Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    private static string InsertRevisionSql(PromptRevision revision)
        => "INSERT INTO mcp_prompt_revisions " +
           "(id,command,description,parent_revision,source,reason,created,created_by,applied,reverted_from) VALUES " +
           $"({Sql(revision.Id)},{Sql(revision.Command)},{SqlNullable(revision.Description)}," +
           $"{SqlNullable(revision.ParentRevision)},{Sql(revision.Source)},{Sql(revision.Reason)}," +
           $"{Sql(revision.Created)},{Sql(revision.CreatedBy)},1,{SqlNullable(revision.RevertedFrom)});";

    private const string ProposalSelect =
        "SELECT id,command,base_revision,old_text,proposed_text,reason,evidence,source_client," +
        "created,status,reviewer,reviewed,review_note,applied_revision FROM " + TableProposals;

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string Now() => DateTimeOffset.Now.ToString("O");

    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, 500);

    private static string Sql(string value) => SqlText.Quote(value); // R1:转义唯一实现

    private static string SqlNullable(string? value) => value == null ? "NULL" : Sql(value);

    private static string? Text(object? value) => value?.ToString();

    private static bool Flag(object? value)
        => value != null && long.TryParse(value.ToString(), out var number) && number != 0;
}
