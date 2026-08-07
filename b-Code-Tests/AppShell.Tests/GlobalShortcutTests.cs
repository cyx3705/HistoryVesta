using System.Diagnostics;
using AppShell.Core.Commands;
using AppShell.Core.Input;
using AppShell.Core.Logging;
using AppShell.Services.Input;
using Xunit;

namespace AppShell.Tests;

public sealed class GlobalShortcutTests
{
    [Fact]
    public void MatcherRecognizesSequenceAndRespectsTimeout()
    {
        var matcher = new GlobalShortcutMatcher();
        var shortcut = Registration("slash", [new(0xBF), new(0xBF)], 350);
        var start = Stopwatch.GetTimestamp();

        Assert.Empty(matcher.Process([shortcut], new(0xBF), start));
        Assert.Single(matcher.Process(
            [shortcut],
            new(0xBF),
            start + (long)(Stopwatch.Frequency * 0.2)));

        Assert.Empty(matcher.Process([shortcut], new(0xBF), start));
        Assert.Empty(matcher.Process(
            [shortcut],
            new(0xBF),
            start + (long)(Stopwatch.Frequency * 0.5)));
    }

    [Fact]
    public void MatcherRestartsWhenCurrentStrokeIsAlsoTheFirstStroke()
    {
        var matcher = new GlobalShortcutMatcher();
        var shortcut = Registration("sequence", [new(0x41), new(0x42)], 350);
        var timestamp = Stopwatch.GetTimestamp();

        Assert.Empty(matcher.Process([shortcut], new(0x41), timestamp));
        Assert.Empty(matcher.Process([shortcut], new(0x41), timestamp + 1));
        Assert.Single(matcher.Process([shortcut], new(0x42), timestamp + 2));
    }

    [Fact]
    public void RegistrationRejectsExactAndEitherDirectionPrefixConflicts()
    {
        using var service = CreateService();
        using var first = service.Register(
            new("long", [new(0x41), new(0x42)], "help"), "one");

        Assert.Throws<InvalidOperationException>(() => service.Register(
            new("short", [new(0x41)], "help"), "two"));
        Assert.Throws<InvalidOperationException>(() => service.Register(
            new("same", [new(0x41), new(0x42)], "help"), "three"));
    }

    [Fact]
    public void OwnerRegistrarReleasesEveryRegistration()
    {
        using var service = CreateService();
        using (var owner = service.CreateOwnerRegistrar("module:test"))
        {
            owner.Register(new("one", [new(0x41)], "help"));
            owner.Register(new("two", [new(0x42)], "help"));
            Assert.Equal(2, service.Registrations.Count);
        }

        Assert.Empty(service.Registrations);
    }

    [Fact]
    public void DescriptorValidationEnforcesSequenceAndIntervalBounds()
    {
        using var service = CreateService();

        Assert.Throws<ArgumentException>(() => service.Register(
            new("empty", [], "help"), "test"));
        Assert.Throws<ArgumentException>(() => service.Register(
            new("long", [new(1), new(2), new(3), new(4), new(5)], "help"), "test"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Register(
            new("fast", [new(1)], "help", 99), "test"));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Register(
            new("slow", [new(1)], "help", 2001), "test"));
    }

    private static GlobalShortcutRegistrationInfo Registration(
        string id,
        IReadOnlyList<GlobalShortcutStroke> strokes,
        int interval)
        => new(id, "test", strokes, "help", interval);

    private static GlobalShortcutService CreateService()
        => new(new CommandBus(new CommandRegistry(), new NullLog()), new NullLog());

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
