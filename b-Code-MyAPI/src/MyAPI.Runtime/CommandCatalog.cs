using System.Diagnostics;
using System.Text.RegularExpressions;
using MyAPI.Abstractions;

namespace MyAPI.Runtime;

public sealed record CommandCatalogOptions
{
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool ExposeExceptionDetails { get; init; }
}

public sealed class CommandRegistrationException : Exception
{
    public CommandRegistrationException(string message) : base(message) { }
}

public sealed class CommandCatalog : ICommandDispatcher
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly CommandCatalogOptions _options;
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public CommandCatalog(CommandCatalogOptions? options = null)
    {
        _options = options ?? new CommandCatalogOptions();
        if (_options.DefaultTimeout <= TimeSpan.Zero && _options.DefaultTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultTimeout must be positive or infinite.");
    }

    public IReadOnlyList<CommandDescriptor> Commands => _snapshot.Commands;
    public IReadOnlyList<ModuleDescriptor> Modules => _snapshot.Modules;

    public void RegisterOrReplace(IMyApiModule module)
    {
        var registration = BuildModule(module);
        lock (_gate)
        {
            var modules = _snapshot.Registrations
                .Where(x => !string.Equals(x.Descriptor.Id, registration.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
                .Append(registration)
                .ToArray();
            _snapshot = BuildSnapshot(modules);
        }
    }

    public void ReplaceAll(IEnumerable<IMyApiModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var registrations = modules.Select(BuildModule).ToArray();
        var next = BuildSnapshot(registrations);
        lock (_gate) _snapshot = next;
    }

    public bool RemoveModule(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) return false;
        lock (_gate)
        {
            var retained = _snapshot.Registrations
                .Where(x => !string.Equals(x.Descriptor.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (retained.Length == _snapshot.Registrations.Count) return false;
            _snapshot = BuildSnapshot(retained);
            return true;
        }
    }

    public async ValueTask<CommandResult> DispatchAsync(
        CommandRequest request,
        CommandContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invocation = context ?? CommandContext.Create();
        var started = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(request.CommandId))
            return Result(CommandStatus.Rejected, null, new CommandError("invalid_command", "Command id is required."), invocation, started);

        if (!_snapshot.CommandMap.TryGetValue(request.CommandId, out var registration))
            return Result(CommandStatus.NotFound, null, new CommandError("command_not_found", $"Command '{request.CommandId}' was not found."), invocation, started);

        if (cancellationToken.IsCancellationRequested)
            return Result(CommandStatus.Canceled, null, new CommandError("canceled", "The invocation was canceled."), invocation, started);

        using var timeout = _options.DefaultTimeout == Timeout.InfiniteTimeSpan
            ? null
            : new CancellationTokenSource(_options.DefaultTimeout);
        using var linked = timeout is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            var task = registration.Handler(invocation, request.Arguments, linked.Token).AsTask();
            var value = timeout is null
                ? await task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : await task.WaitAsync(linked.Token).ConfigureAwait(false);
            return Result(CommandStatus.Succeeded, value, null, invocation, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(CommandStatus.Canceled, null, new CommandError("canceled", "The invocation was canceled."), invocation, started);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            return Result(CommandStatus.TimedOut, null, new CommandError("timeout", "The command exceeded its execution timeout."), invocation, started);
        }
        catch (CommandRejectedException ex)
        {
            return Result(CommandStatus.Rejected, null, new CommandError(ex.Code, ex.Message), invocation, started);
        }
        catch (Exception ex)
        {
            var message = _options.ExposeExceptionDetails ? ex.Message : "The command failed.";
            return Result(CommandStatus.Failed, null, new CommandError("execution_failed", message), invocation, started);
        }
    }

    private static ModuleRegistration BuildModule(IMyApiModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var descriptor = module.Descriptor ?? throw new CommandRegistrationException("Module descriptor cannot be null.");
        ValidateModule(descriptor);
        var builder = new ModuleBuilder(descriptor.Id);
        module.Register(builder);
        return new ModuleRegistration(descriptor, builder.Items);
    }

    private static Snapshot BuildSnapshot(IEnumerable<ModuleRegistration> registrations)
    {
        var modules = registrations.ToArray();
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, RegisteredCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            if (!moduleIds.Add(module.Descriptor.Id))
                throw new CommandRegistrationException($"Module '{module.Descriptor.Id}' is registered more than once.");
            foreach (var command in module.Commands)
            {
                if (!commands.TryAdd(command.Descriptor.Id, command))
                    throw new CommandRegistrationException($"Command '{command.Descriptor.Id}' is registered more than once.");
            }
        }

        return new Snapshot(
            modules,
            commands,
            Array.AsReadOnly(modules.Select(x => x.Descriptor).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray()),
            Array.AsReadOnly(commands.Values.Select(x => x.Descriptor).OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    private static void ValidateModule(ModuleDescriptor descriptor)
    {
        ValidateId(descriptor.Id, "module");
        if (string.IsNullOrWhiteSpace(descriptor.Name)) throw new CommandRegistrationException("Module name is required.");
        if (string.IsNullOrWhiteSpace(descriptor.Version)) throw new CommandRegistrationException($"Module '{descriptor.Id}' version is required.");
    }

    private static void ValidateDescriptor(CommandDescriptor descriptor, string moduleId)
    {
        ValidateId(descriptor.Id, "command");
        if (descriptor.Id.Length > 128) throw new CommandRegistrationException($"Command '{descriptor.Id}' exceeds 128 characters.");
        if (!string.Equals(descriptor.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
            throw new CommandRegistrationException($"Command '{descriptor.Id}' belongs to module '{descriptor.ModuleId}', not '{moduleId}'.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters)
        {
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name) || !names.Add(parameter.Name))
                throw new CommandRegistrationException($"Command '{descriptor.Id}' has duplicate or invalid parameters.");
            if (string.IsNullOrWhiteSpace(parameter.Type))
                throw new CommandRegistrationException($"Command '{descriptor.Id}' parameter '{parameter.Name}' has no type.");
        }
    }

    private static void ValidateId(string id, string kind)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !IdPattern.IsMatch(id))
            throw new CommandRegistrationException($"Invalid {kind} id '{id}'. Use lowercase letters, digits, dots, underscores or hyphens.");
    }

    private static CommandResult Result(CommandStatus status, object? value, CommandError? error, CommandContext context, long started) =>
        new(status, value, error, context.InvocationId, Stopwatch.GetElapsedTime(started));

    private sealed class ModuleBuilder : ICommandRegistry
    {
        private readonly string _moduleId;
        private readonly Dictionary<string, RegisteredCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

        public ModuleBuilder(string moduleId) => _moduleId = moduleId;
        public IReadOnlyList<RegisteredCommand> Items => _commands.Values.ToArray();

        public void Register(CommandDescriptor descriptor, CommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(handler);
            ValidateDescriptor(descriptor, _moduleId);
            if (!_commands.TryAdd(descriptor.Id, new RegisteredCommand(descriptor, handler)))
                throw new CommandRegistrationException($"Command '{descriptor.Id}' is registered more than once in module '{_moduleId}'.");
        }
    }

    private sealed record RegisteredCommand(CommandDescriptor Descriptor, CommandHandler Handler);
    private sealed record ModuleRegistration(ModuleDescriptor Descriptor, IReadOnlyList<RegisteredCommand> Commands);

    private sealed class Snapshot
    {
        public static Snapshot Empty { get; } = new(Array.Empty<ModuleRegistration>(), new Dictionary<string, RegisteredCommand>(), Array.Empty<ModuleDescriptor>(), Array.Empty<CommandDescriptor>());

        public Snapshot(
            IReadOnlyList<ModuleRegistration> registrations,
            IReadOnlyDictionary<string, RegisteredCommand> commandMap,
            IReadOnlyList<ModuleDescriptor> modules,
            IReadOnlyList<CommandDescriptor> commands)
        {
            Registrations = registrations;
            CommandMap = commandMap;
            Modules = modules;
            Commands = commands;
        }

        public IReadOnlyList<ModuleRegistration> Registrations { get; }
        public IReadOnlyDictionary<string, RegisteredCommand> CommandMap { get; }
        public IReadOnlyList<ModuleDescriptor> Modules { get; }
        public IReadOnlyList<CommandDescriptor> Commands { get; }
    }
}
