using System.Text.Json.Serialization;

namespace AppShell.App.CoffeeMachine;

public sealed record BrewRecipePreset(
    string Name,
    BrewRecipe Recipe,
    DateTime UpdatedAt)
{
    [JsonIgnore]
    public string Summary =>
        $"{Name} | 上液 {Recipe.UpSeconds}s, 停留 {Recipe.UpDwellSeconds}s, 降液 {Recipe.DownSeconds}s, 停留 {Recipe.DownDwellSeconds}s, {Recipe.Cycles} 次";
}
