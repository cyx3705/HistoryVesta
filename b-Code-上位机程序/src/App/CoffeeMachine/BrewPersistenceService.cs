using System.IO;
using System.Text.Json;
using AppShell.Core.Logging;
using AppShell.Services;

namespace AppShell.App.CoffeeMachine;

public sealed class BrewPersistenceService
{
    private const int HistoryLimit = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _file;
    private readonly IShellLog _log;

    public BrewPersistenceService(AppPaths paths, IShellLog log)
    {
        _log = log;
        _file = Path.Combine(paths.DataDir, "brew-m4.json");
    }

    public IReadOnlyList<BrewRecipePreset> ReadRecipes()
        => Load().Recipes
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public IReadOnlyList<BrewHistoryEntry> ReadHistory(int limit = 100)
        => Load().History
            .OrderByDescending(h => h.Time)
            .Take(Math.Clamp(limit, 1, HistoryLimit))
            .ToList();

    public BrewRecipePreset SaveRecipe(string name, BrewRecipe recipe)
    {
        name = NormalizeName(name);
        var store = Load();
        store.Recipes.RemoveAll(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        var preset = new BrewRecipePreset(name, recipe, DateTime.Now);
        store.Recipes.Add(preset);
        Save(store);
        return preset;
    }

    public bool TryGetRecipe(string name, out BrewRecipePreset? preset)
    {
        name = NormalizeName(name);
        preset = ReadRecipes().FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        return preset != null;
    }

    public bool DeleteRecipe(string name)
    {
        name = NormalizeName(name);
        var store = Load();
        var removed = store.Recipes.RemoveAll(p =>
            p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (removed > 0)
            Save(store);
        return removed > 0;
    }

    public void AppendHistory(BrewHistoryEntry entry)
    {
        var store = Load();
        store.History.Add(entry);
        store.History = store.History
            .OrderByDescending(h => h.Time)
            .Take(HistoryLimit)
            .OrderBy(h => h.Time)
            .ToList();
        Save(store);
    }

    private BrewStore Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new BrewStore();

            var text = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<BrewStore>(text, JsonOptions) ?? new BrewStore();
        }
        catch (Exception ex)
        {
            _log.Warn("brew", $"M4 数据读取失败，已使用空数据: {ex.Message}");
            return new BrewStore();
        }
    }

    private void Save(BrewStore store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(store, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error("brew", $"M4 数据保存失败: {ex.Message}");
        }
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("配方名称不能为空");
        if (name.Length > 40)
            throw new InvalidOperationException("配方名称不能超过 40 个字符");
        return name;
    }

    private sealed class BrewStore
    {
        public List<BrewRecipePreset> Recipes { get; set; } = [];

        public List<BrewHistoryEntry> History { get; set; } = [];
    }
}
