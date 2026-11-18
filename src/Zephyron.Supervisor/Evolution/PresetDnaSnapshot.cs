using System.Text.Json;
using System.Text.Json.Serialization;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Снимок генетического кода (DNA Snapshot) пресета.
/// Позволяет сохранять и переносить обученные стратегии обхода, делиться ими и возобновлять эволюцию.
/// </summary>
public sealed record PresetDnaSnapshot
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("presetName")]
    public string PresetName { get; init; } = "";

    [JsonPropertyName("basePresetName")]
    public string BasePresetName { get; init; } = "";

    [JsonPropertyName("generation")]
    public int Generation { get; init; }

    [JsonPropertyName("fitnessScore")]
    public double FitnessScore { get; init; }

    [JsonPropertyName("streamingSpeedKBps")]
    public double StreamingSpeedKBps { get; init; }

    [JsonPropertyName("ispName")]
    public string? IspName { get; init; }

    [JsonPropertyName("ispAsn")]
    public string? IspAsn { get; init; }

    [JsonPropertyName("dpiFilterType")]
    public string? DpiFilterType { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("genes")]
    public Dictionary<string, PresetGene> Genes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Экспортирует геном особи в JSON-файл ДНК.
    /// </summary>
    public static void SaveToFile(
        PresetIndividual individual,
        ZapretPreset basePreset,
        string filePath,
        IspProfile? isp = null,
        DpiProfile? dpi = null,
        double streamingSpeedKBps = 0.0)
    {
        var snapshot = new PresetDnaSnapshot
        {
            Version = 1,
            PresetName = $"{basePreset.Name} (Gen {individual.GenerationNumber})",
            BasePresetName = basePreset.Name,
            Generation = individual.GenerationNumber,
            FitnessScore = individual.FitnessScore,
            StreamingSpeedKBps = streamingSpeedKBps,
            IspName = isp?.IspName,
            IspAsn = isp?.Asn,
            DpiFilterType = dpi?.IspFilterType,
            CreatedAtUtc = DateTime.UtcNow,
            Genes = new Dictionary<string, PresetGene>(individual.Genes, StringComparer.OrdinalIgnoreCase)
        };

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Загружает геном особи из JSON-файла ДНК.
    /// </summary>
    public static PresetDnaSnapshot LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Файл снимка ДНК не найден: {filePath}");

        var json = File.ReadAllText(filePath);
        var snapshot = JsonSerializer.Deserialize<PresetDnaSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return snapshot ?? throw new InvalidOperationException("Не удалось разобрать файл снимка генома ДНК.");
    }

    /// <summary>
    /// Восстанавливает особь PresetIndividual для включения в генофонд или применения.
    /// </summary>
    public PresetIndividual ToIndividual()
    {
        return new PresetIndividual
        {
            GenerationNumber = Generation,
            FitnessScore = FitnessScore,
            Genes = new Dictionary<string, PresetGene>(Genes, StringComparer.OrdinalIgnoreCase),
            ProbesPassed = (int)Math.Round(FitnessScore * 6),
            ProbesTotal = 6,
            AvgLatencyMs = 60
        };
    }
}
