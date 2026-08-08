#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projects = @(
    'HistoryVulcan.Core'
    'HistoryVulcan.Services'
    'HistoryVulcan.ServiceHost'
    'HistoryVulcan.Shell'
)

$componentRoot = Split-Path -Parent $PSScriptRoot
$violations = @()
$versionProps = Get-Content -LiteralPath (Join-Path $componentRoot 'VulcanVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($versionProps, '<VulcanVersion>(?<version>[^<]+)</VulcanVersion>')
if (-not $versionMatch.Success) {
    throw 'VulcanVersion.props does not contain VulcanVersion'
}
$version = $versionMatch.Groups['version'].Value
$approved = @{
    'HistoryVulcan.Core' = @()
    'HistoryVulcan.Services' = @()
    'HistoryVulcan.ServiceHost' = @()
    'HistoryVulcan.Shell' = @()
}

if ($version -eq '3.2.1') {
    $approved['HistoryVulcan.Core'] = @(
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void'
    )
    $approved['HistoryVulcan.Shell'] = @(
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void'
    )
}

if ($version -eq '3.2.2') {
    $approved['HistoryVulcan.Core'] = @(
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.CommandDescriptor.Domain.init -> void'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetCommandClass(string! name) -> string!'
        'HistoryVulcan.Core.Commands.CommandRegistry.GetDomain(string! name) -> string!'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.CommandClass.init -> void'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.get -> string?'
        'HistoryVulcan.Core.Commands.FrontendCommandCapability.Domain.init -> void'
        'HistoryVulcan.Core.Modules.IActivatableToolContent'
        'HistoryVulcan.Core.Modules.IActivatableToolContent.ActivateContent() -> void'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.get -> string?'
        'HistoryVulcan.Core.Modules.ModuleCommandAttribute.CommandClass.init -> void'
    )
    $approved['HistoryVulcan.Services'] = @(
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestFileName = "module.manifest.json" -> string!'
        'const HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ManifestType = "HistoryVulcan.Module" -> string!'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!'
        'HistoryVulcan.Services.Modules.IModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Code.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Message.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.ModuleDiscoveryDiagnostic(string! Path, string! Code, string! Message) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic.Path.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ArtifactPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DependencyPaths.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.DocsPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ManifestPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.McpExposure.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.ModuleDiscoveryEntry(string! Name, string! Version, string! PackagePath, string! ManifestPath, string! ArtifactPath, bool Ui, string? DocsPath, System.Collections.Generic.IReadOnlyList<string!>! DependencyPaths, string? McpExposure) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Name.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.PackagePath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.get -> bool'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Ui.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.get -> string!'
        'HistoryVulcan.Services.Modules.ModuleDiscoveryEntry.Version.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Diagnostics.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.ModuleDiscoverySnapshot(System.Collections.Generic.IReadOnlyList<string!>! Roots, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>! Modules, System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>! Diagnostics) -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryEntry!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Modules.init -> void'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot.Roots.init -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.ChangeDiscoveryRoots(System.Collections.Generic.IEnumerable<string!>! roots) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryDiagnostics.get -> System.Collections.Generic.IReadOnlyList<HistoryVulcan.Services.Modules.ModuleDiscoveryDiagnostic!>!'
        'HistoryVulcan.Services.Modules.ModuleHost.DiscoveryRoots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ModuleHost.ModuleHost(HistoryVulcan.Services.Modules.IModuleDiscoverySource! discoverySource, HistoryVulcan.Core.Logging.IShellLog! log) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.ReloadConfirmedSources(System.Collections.Generic.IEnumerable<string!>! manifestPaths) -> void'
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.get -> bool'
        'HistoryVulcan.Services.Modules.ModuleHost.RequireConfirmedSources.set -> void'
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleMeta.ManifestPath.init -> void'
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.get -> string?'
        'HistoryVulcan.Services.Modules.ModuleMeta.SourcePath.init -> void'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Discover() -> HistoryVulcan.Services.Modules.ModuleDiscoverySnapshot!'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.Roots.get -> System.Collections.Generic.IReadOnlyList<string!>!'
        'HistoryVulcan.Services.Modules.ZModuleDiscoverySource.ZModuleDiscoverySource(System.Collections.Generic.IEnumerable<string!>! roots) -> void'
        'static HistoryVulcan.Services.Modules.ZModuleDiscoverySource.FindAutomaticRoot(string! startPath) -> string?'
    )
    $approved['HistoryVulcan.Shell'] = @(
        'const HistoryVulcan.Shell.Modules.ModuleCommands.KeyModuleRoots = "module.roots" -> string!'
        'HistoryVulcan.Shell.Console.ConsoleView.ActivateContent() -> void'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.get -> string!'
        'HistoryVulcan.Shell.Mcp.CommandCatalogRow.CommandClass.init -> void'
        'HistoryVulcan.Shell.ShellConfig.ModuleDiscoveryRoots.get -> System.Collections.Generic.List<string!>!'
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.get -> bool'
        'HistoryVulcan.Shell.ShellConfig.RequireConfirmedModuleSources.set -> void'
    )
}

foreach ($project in $projects) {
    $path = Join-Path $componentRoot "src\$project\PublicAPI.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $violations += "$project : PublicAPI.Unshipped.txt missing"
        continue
    }

    $entries = @(
        Get-Content -LiteralPath $path -Encoding UTF8 |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )

    $difference = @(Compare-Object -ReferenceObject @($approved[$project]) -DifferenceObject $entries)
    if ($difference.Count -ne 0) {
        $violations += "$project : unshipped API differs from approved $version baseline -> $($difference -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Public API freeze gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Public API changes must exactly match the version-approved Unshipped baseline.'
    exit 1
}

Write-Host "Public API baseline gate passed: $($projects.Count) package Unshipped files match the approved $version baseline."
