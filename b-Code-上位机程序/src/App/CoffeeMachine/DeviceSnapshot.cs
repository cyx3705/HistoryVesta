namespace AppShell.App.CoffeeMachine;

public sealed record DeviceSnapshot(
    BrewMode Mode,
    bool Gpio15,
    bool Gpio16,
    string RawMode,
    string? Error,
    long UptimeMs,
    int Sequence);
