using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace MyAPI.Abstractions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandStatus
{
    Succeeded,
    NotFound,
    Rejected,
    Canceled,
    TimedOut,
    Failed
}

public sealed record CommandParameter
{
    public CommandParameter(string name, string type, bool required = true, string description = "")
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type ?? throw new ArgumentNullException(nameof(type));
        Description = description ?? string.Empty;
        Required = required;
    }

    public string Name { get; }
    public string Type { get; }
    public bool Required { get; }
    public string Description { get; }
}

public sealed record CommandDescriptor
{
    public CommandDescriptor(string id, string moduleId, string description, params CommandParameter[] parameters)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ModuleId = moduleId ?? throw new ArgumentNullException(nameof(moduleId));
        Description = description ?? string.Empty;
        Parameters = Array.AsReadOnly((parameters ?? Array.Empty<CommandParameter>()).ToArray());
    }

    public string Id { get; }
    public string ModuleId { get; }
    public string Description { get; }
    public IReadOnlyList<CommandParameter> Parameters { get; }
}

public sealed record ModuleDescriptor
{
    public ModuleDescriptor(string id, string name, string version, string description = "")
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Description = description ?? string.Empty;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
}

public sealed record CommandRequest
{
    public CommandRequest(string commandId, IReadOnlyDictionary<string, JsonElement>? arguments = null)
    {
        CommandId = commandId ?? throw new ArgumentNullException(nameof(commandId));
        Arguments = arguments is null
            ? EmptyArguments
            : new ReadOnlyDictionary<string, JsonElement>(arguments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase));
    }

    public string CommandId { get; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; }

    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyArguments =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));
}

public sealed class CommandContext
{
    public CommandContext(string invocationId, string? caller = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        InvocationId = string.IsNullOrWhiteSpace(invocationId)
            ? throw new ArgumentException("Invocation id is required.", nameof(invocationId))
            : invocationId;
        Caller = caller;
        Properties = properties is null
            ? EmptyProperties
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(properties, StringComparer.Ordinal));
    }

    public string InvocationId { get; }
    public string? Caller { get; }
    public IReadOnlyDictionary<string, string> Properties { get; }

    public static CommandContext Create(string? caller = null) =>
        new(Guid.NewGuid().ToString("N"), caller);

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed record CommandError(string Code, string Message);

public sealed record CommandResult(
    CommandStatus Status,
    object? Value,
    CommandError? Error,
    string InvocationId,
    TimeSpan Duration)
{
    public bool IsSuccess => Status == CommandStatus.Succeeded;
}

public delegate ValueTask<object?> CommandHandler(
    CommandContext context,
    IReadOnlyDictionary<string, JsonElement> arguments,
    CancellationToken cancellationToken);

public interface ICommandRegistry
{
    void Register(CommandDescriptor descriptor, CommandHandler handler);
}

public interface ICommandDispatcher
{
    IReadOnlyList<CommandDescriptor> Commands { get; }
    IReadOnlyList<ModuleDescriptor> Modules { get; }
    ValueTask<CommandResult> DispatchAsync(
        CommandRequest request,
        CommandContext? context = null,
        CancellationToken cancellationToken = default);
}

public interface IMyApiModule
{
    ModuleDescriptor Descriptor { get; }
    void Register(ICommandRegistry registry);
}

public sealed class CommandRejectedException : Exception
{
    public CommandRejectedException(string code, string message)
        : base(message)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "rejected" : code;
    }

    public string Code { get; }
}
