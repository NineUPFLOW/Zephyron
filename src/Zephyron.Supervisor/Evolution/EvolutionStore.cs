using System.Text.Json;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Сохраненное состояние эволюционной популяции на диске.
/// </summary>
public sealed class EvolutionSaveState
{
    public int Generation { get; set; }
    public string BasePreset { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public List<PresetIndividual> Population { get; set; } = [];
}

/// <summary>
/// Обеспечивает персистентность популяции в JSON-файл для возобновления эволюции.
/// </summary>
public sealed class EvolutionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string DefaultPath => Path.Combine("config", "evolution", "population.json");

    /// <summary>
    /// Атомарно сохраняет текущую популяцию на диск.
    /// </summary>
    public async Task SaveAsync(
        IReadOnlyList<PresetIndividual> population,
        int generation,
        string basePresetName,
        string? path = null,
        CancellationToken ct = default)
    {
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var state = new EvolutionSaveState
        {
            Generation = generation,
            BasePreset = basePresetName,
            SavedAt = DateTimeOffset.Now,
            Population = population.ToList()
        };

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var temp = target + ".tmp";

        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Загружает сохраненную популяцию; возвращает null, если файл отсутствует или поврежден.
    /// </summary>
    public async Task<EvolutionSaveState?> LoadAsync(string? path = null, CancellationToken ct = default)
    {
        var target = path ?? DefaultPath;
        if (!File.Exists(target))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(target, ct);
            return JsonSerializer.Deserialize<EvolutionSaveState>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
