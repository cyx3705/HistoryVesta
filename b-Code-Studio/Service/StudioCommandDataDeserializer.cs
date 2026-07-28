using System.Text.Json;
using AppShell.Services.Mcp;
using AppShell.Services.Modules;
using AppShell.Shell.Mcp;
using AppShell.Core.Data;
using AppShell.Core.Files;
using OneHistoryStudio.Git;
using OneHistoryStudio.Connection;

namespace OneHistoryStudio.Service;

/// <summary>把命令 API 的 JSON Data 还原为现有 WPF 视图消费的记录类型。</summary>
public static class StudioCommandDataDeserializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static object? Deserialize(string commandName, JsonElement data)
        => commandName.ToLowerInvariant() switch
        {
            "proj.list" => Read<List<WorktreeInfo>>(data),
            "proj.tree" => Read<BranchTreeNode>(data),
            "proj.metalist" => Read<List<MetaFolderInfo>>(data),
            "proj.history" => Read<BranchHistoryReport>(data),
            "proj.history.show" => Read<CommitDetail>(data),
            "proj.history.diff" => Read<BranchDiffReport>(data),
            "git.rule.list" => Read<List<GitFileRuleInfo>>(data),
            "git.rule.scan" => Read<InventoryReport>(data),
            "git.rule.set" or "git.rule.remove" => Read<GitFileRulePreview>(data),
            "github.status" => Read<GitHubAccountOverview>(data),
            "github.accounts" => Read<List<GitCredentialAccount>>(data),
            "github.test" => Read<GitHubConnectionStatus>(data),
            "lan.paircode" => Read<LanPairCode>(data),
            "lan.status" => Read<LanServerStatus>(data),
            "lan.device.list" => Read<List<LanDeviceInfo>>(data),
            "module.list" => Read<List<ModuleMeta>>(data),
            "command.list" => Read<List<CommandCatalogRow>>(data),
            "command.show" => Read<CommandCatalogDetail>(data),
            "prompt.get" => Read<PromptStatus>(data),
            "prompt.history" => Read<List<PromptRevision>>(data),
            "mcp.pending" => Read<List<PromptProposal>>(data),
            "correction.list" => Read<List<PromptCorrection>>(data),
            "incident.list" => Read<List<PromptIncident>>(data),
            "db.list" or "db.tables" => Read<List<string>>(data),
            "db.schema" => Read<List<ColumnInfo>>(data),
            "db.query" => Read<QueryResult>(data),
            "db.insert" or "db.update" or "db.delete" or "db.export" => Read<long>(data),
            "db.sql" => data.ValueKind == JsonValueKind.Object
                ? Read<QueryResult>(data)
                : Read<int>(data),
            "res.list" => Read<WorkspaceListing>(data),
            _ => data.Clone(),
        };

    private static T? Read<T>(JsonElement data)
        => JsonSerializer.Deserialize<T>(data.GetRawText(), Options);
}
