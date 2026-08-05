using System.Text.Json;

namespace MyAPI.Host;

internal static class HttpCommandBinding
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<Dictionary<string, JsonElement>> ReadArgumentsAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0) return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(request.Body, Options).ConfigureAwait(false);
            return values is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Request body must be a JSON object: {ex.Message}", ex);
        }
    }
}
