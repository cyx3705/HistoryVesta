using MyAPI.Abstractions;
using MyAPI.TestCapability;

namespace MyAPI.TestModule;

public sealed class TestModule : IMyApiModule
{
    private readonly Calculator _calculator;

    public TestModule() : this(new Calculator()) { }

    public TestModule(Calculator calculator) => _calculator = calculator;

    public ModuleDescriptor Descriptor { get; } = new(
        "test",
        "External test capability module",
        "4.0.0-exploration",
        "An out-of-tree explicit-registration module used by contract and smoke tests.");

    public void Register(ICommandRegistry registry)
    {
        registry.Register(
            new CommandDescriptor(
                "test.calculator.add",
                Descriptor.Id,
                "Adds two 32-bit integers.",
                new CommandParameter("a", "integer"),
                new CommandParameter("b", "integer")),
            (context, arguments, _) =>
                ValueTask.FromResult<object?>(_calculator.Add(
                    CommandArguments.Required<int>(arguments, "a"),
                    CommandArguments.Required<int>(arguments, "b"))));

        registry.Register(
            new CommandDescriptor(
                "test.calculator.hello",
                Descriptor.Id,
                "Greets a named caller.",
                new CommandParameter("name", "string", required: false, "Name to greet.")),
            async (_, arguments, cancellationToken) =>
                await _calculator.SayHelloAsync(
                    CommandArguments.Optional(arguments, "name", "World"),
                    cancellationToken).ConfigureAwait(false));

        registry.Register(
            new CommandDescriptor(
                "test.calculator.reverse",
                Descriptor.Id,
                "Reverses a string.",
                new CommandParameter("text", "string")),
            (context, arguments, _) =>
                ValueTask.FromResult<object?>(_calculator.Reverse(
                    CommandArguments.Required<string>(arguments, "text"))));

        registry.Register(
            new CommandDescriptor(
                "test.calculator.clock",
                Descriptor.Id,
                "Returns UTC time and machine name."),
            (_, _, _) => ValueTask.FromResult<object?>(_calculator.ReadClock()));
    }
}
