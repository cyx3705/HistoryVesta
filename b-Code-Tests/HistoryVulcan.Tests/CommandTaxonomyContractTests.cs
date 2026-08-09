using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Mcp;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// DEC-023 / REQ-CMD-010 / REQ-CMD-011:三段式九类分类法与模块域去品牌前缀。
/// </summary>
public sealed class CommandTaxonomyContractTests
{
    /// <summary>3.3.2 认可的九个内置类，见技术合同 REQ-CMD-010。</summary>
    private static readonly HashSet<string> BuiltinClasses = new(StringComparer.Ordinal)
    {
        "app", "command", "ui", "log", "mcp", "module", "prompt", "svc", "web",
    };

    [Theory]
    [InlineData("HistoryJanus", "janus")]
    [InlineData("HistoryMercury", "mercury")]
    [InlineData("HistoryMinerva", "minerva")]
    [InlineData("HistoryVulcan", "vulcan")]
    [InlineData("historyjanus", "janus")]
    [InlineData("HISTORYJANUS", "janus")]
    [InlineData("  HistoryJanus  ", "janus")]
    [InlineData("WBall", "wball")]
    [InlineData("History", "history")]
    [InlineData("", "")]
    public void ModuleDomainStripsTheHistoryBrandPrefix(string moduleName, string expected)
        => Assert.Equal(expected, ModuleDomainNaming.ToDomain(moduleName));

    [Fact]
    public void ModuleOwnedCommandsUseTheNormalizedDomainNotTheManifestName()
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "janus.project.commit",
                Domain = "spoofed",
                CommandClass = "project",
                Summary = "commit",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            },
            "module:HistoryJanus");

        // owner 强制且归一化：描述符自填的 Domain 被覆盖，History 前缀被剥离。
        Assert.Equal("janus", registry.GetDomain("janus.project.commit"));
        Assert.Equal("project", registry.GetCommandClass("janus.project.commit"));
    }

    [Fact]
    public void EveryBuiltinCommandNameIsThreeSegmentLowercaseWithoutHyphen()
    {
        foreach (var name in BuiltinCommandDefinitions.Names)
        {
            var parts = name.Split('.');
            Assert.True(parts.Length == 3, $"{name} 不是三段式");
            Assert.Equal(name.ToLowerInvariant(), name);
            Assert.DoesNotContain('-', name);
            Assert.Equal("vulcan", parts[0]);
            Assert.Contains(parts[1], BuiltinClasses);
        }
    }

    [Fact]
    public void SharedBuiltinDefinitionsCarryAnApprovedClass()
    {
        foreach (var name in BuiltinCommandDefinitions.Names)
        {
            var descriptor = BuiltinCommandDefinitions.Bind(
                name,
                _ => Task.FromResult(CommandResult.Ok()));

            Assert.Equal("vulcan", descriptor.Domain);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.CommandClass), $"{name} 未声明类");
            Assert.Contains(descriptor.CommandClass!, BuiltinClasses);
        }
    }

    [Fact]
    public void RetiredClassesAndTheShadowDebugDomainAreGone()
    {
        // core / frontend / win / layout / panel 五个类与 debug 影子域在 3.3.2 退役。
        foreach (var retired in new[] { "core", "frontend", "win", "layout", "panel" })
            Assert.DoesNotContain(retired, BuiltinClasses);

        foreach (var name in BuiltinCommandDefinitions.Names)
            Assert.False(
                name.StartsWith("debug.", StringComparison.OrdinalIgnoreCase),
                $"{name} 仍在影子域 debug 下");
    }

    [Fact]
    public void DiagnosticFloodCommandStaysOutOfReachOfRemoteClients()
    {
        // 3.3.2 把 debug.logflood 收编为 vulcan.log.flood。原先的 MCP 硬排除按 "debug." 前缀
        // 生效，改名一度让它失效——承压注水因此可被 MCP/Web 远程触发
        // （rate=100000 × seconds=600 即 6000 万条日志）。这里锁住修复后的行为。
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("vulcan.log.flood"));
        Assert.Contains("vulcan.log.flood", McpExposurePolicy.DiagnosticCommandNames);

        // 前缀规则保留给尚未迁移的模块调试指令。
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("debug.anything"));

        // 同类的日志指令不受影响，仍可正常暴露。
        Assert.Null(McpExposurePolicy.HardExclusionReason("vulcan.log.level"));
    }

    [Fact]
    public void FloodIsNotPartOfTheShippedBuiltinCatalog()
    {
        // 诊断指令不属于正式命令集：只有把 diagnostics.commands 显式置真的宿主才注册它。
        Assert.DoesNotContain(
            "vulcan.log.flood",
            BuiltinCommandDefinitions.Names,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoSegmentNamesFallBackToTheFirstSegmentAsClass()
    {
        // 「无类」概念已废止：两段名回退到首段，而不是空串。
        Assert.Equal("fixture", CommandRegistry.LegacyClass("fixture.run"));
        Assert.Equal("core", CommandRegistry.LegacyClass("ping"));
        Assert.Equal("ui", CommandRegistry.LegacyClass("vulcan.ui.dock"));
        Assert.Equal("dock", CommandRegistry.GetMethod("vulcan.ui.dock"));
    }
}
