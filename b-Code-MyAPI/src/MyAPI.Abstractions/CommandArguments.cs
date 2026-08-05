using System.Text.Json;

namespace MyAPI.Abstractions;

public static class CommandArguments
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Required<T>(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        if (!TryGet(arguments, name, out var element))
            throw new CommandRejectedException("missing_argument", $"Argument '{name}' is required.");

        try
        {
            return element.Deserialize<T>(JsonOptions)
                ?? throw new CommandRejectedException("invalid_argument", $"Argument '{name}' cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new CommandRejectedException("invalid_argument", $"Argument '{name}' has an invalid value: {ex.Message}");
        }
    }

    public static T Optional<T>(IReadOnlyDictionary<string, JsonElement> arguments, string name, T defaultValue)
    {
        if (!TryGet(arguments, name, out var element)) return defaultValue;

        try
        {
            return element.Deserialize<T>(JsonOptions) ?? defaultValue;
        }
        catch (JsonException ex)
        {
            throw new CommandRejectedException("invalid_argument", $"Argument '{name}' has an invalid value: {ex.Message}");
        }
    }

    private static bool TryGet(IReadOnlyDictionary<string, JsonElement> arguments, string name, out JsonElement value)
    {
        if (arguments.TryGetValue(name, out value)) return true;
        foreach (var pair in arguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
