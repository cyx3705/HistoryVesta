using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

internal static class ModuleCommandTaxonomy
{
    public static CommandDescriptor Apply(CommandDescriptor source, string moduleName)
        => new()
        {
            Name = source.Name,
            Domain = moduleName,
            CommandClass = string.IsNullOrWhiteSpace(source.CommandClass) ? "core" : source.CommandClass,
            Summary = source.Summary,
            Example = source.Example,
            Parameters = source.Parameters,
            ConfirmPrompt = source.ConfirmPrompt,
            SupportsUndo = source.SupportsUndo,
            Dangerous = source.Dangerous,
            Readonly = source.Readonly,
            RequiresUiThread = source.RequiresUiThread,
            ExecutionSite = source.ExecutionSite,
            AllowMcpExecution = source.AllowMcpExecution,
            AllowUnspecifiedParameters = source.AllowUnspecifiedParameters,
            Handler = source.Handler,
        };
}
