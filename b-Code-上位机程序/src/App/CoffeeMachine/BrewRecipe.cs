namespace AppShell.App.CoffeeMachine;

public sealed record BrewRecipe(
    int UpSeconds,
    int UpDwellSeconds,
    int DownSeconds,
    int DownDwellSeconds,
    int Cycles)
{
    public int StepCount => Cycles * 4;

    public int TotalSeconds => Cycles * (UpSeconds + UpDwellSeconds + DownSeconds + DownDwellSeconds);

    public static bool TryCreate(
        int upSeconds,
        int upDwellSeconds,
        int downSeconds,
        int downDwellSeconds,
        int cycles,
        out BrewRecipe? recipe,
        out string error)
    {
        recipe = null;
        error = "";

        if (upSeconds is < 1 or > 3600)
            error = "上液时间需在 1-3600 秒之间";
        else if (upDwellSeconds is < 0 or > 3600)
            error = "上液停留需在 0-3600 秒之间";
        else if (downSeconds is < 1 or > 3600)
            error = "降液时间需在 1-3600 秒之间";
        else if (downDwellSeconds is < 0 or > 3600)
            error = "降液停留需在 0-3600 秒之间";
        else if (cycles is < 1 or > 999)
            error = "循环次数需在 1-999 次之间";

        if (error.Length > 0)
            return false;

        recipe = new BrewRecipe(upSeconds, upDwellSeconds, downSeconds, downDwellSeconds, cycles);
        return true;
    }
}
