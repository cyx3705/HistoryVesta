using System.Text.Json.Serialization;

namespace AppShell.App.CoffeeMachine;

public sealed record BrewHistoryEntry(
    DateTime Time,
    string Type,
    string Message,
    string Mode,
    bool Gpio15,
    bool Gpio16,
    string? RecipeName = null)
{
    [JsonIgnore]
    public string TimeText => Time.ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public string TypeText => Type switch
    {
        "run" => "运行",
        "gpio" => "GPIO",
        "fault" => "故障",
        "recipe" => "配方",
        "device" => "设备",
        _ => Type,
    };

    [JsonIgnore]
    public string DisplayText => $"{TimeText} [{TypeText}] {Message}";
}
