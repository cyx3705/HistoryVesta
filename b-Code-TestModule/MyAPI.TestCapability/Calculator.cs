namespace MyAPI.TestCapability;

public sealed class Calculator
{
    public int Add(int a, int b) => checked(a + b);

    public async Task<string> SayHelloAsync(string name, CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        return $"Hello {name}!";
    }

    public string Reverse(string text) => new(text.Reverse().ToArray());

    public ServerClock ReadClock() => new(DateTimeOffset.UtcNow, Environment.MachineName);
}

public sealed record ServerClock(DateTimeOffset UtcNow, string MachineName);
