using System.Text.Json;

namespace Zephyron.Zapret.Evolution;

/// <summary>
/// Настройки генетической эволюции пресетов с сериализацией в JSON.
/// </summary>
public sealed class EvolutionSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Размер популяции (число особей в поколении).</summary>
    public int PopulationSize { get; set; } = 16;

    /// <summary>Максимальное число поколений.</summary>
    public int MaxGenerations { get; set; } = 20;

    /// <summary>Порог схождения (остановка при достижении фитнес-балла).</summary>
    public double ConvergeThreshold { get; set; } = 0.92;

    /// <summary>Базовая вероятность мутации гена секции (0.0..1.0).</summary>
    public double MutationRate { get; set; } = 0.15;

    /// <summary>Вероятность кроссовера родительской пары (0.0..1.0).</summary>
    public double CrossoverRate { get; set; } = 0.80;

    /// <summary>Размер выборки турнирной селекции.</summary>
    public int TournamentSize { get; set; } = 3;

    /// <summary>Число элитных особей, переходящих в следующее поколение без изменений.</summary>
    public int EliteCount { get; set; } = 2;

    /// <summary>Максимальное число параллельных процессов проб.</summary>
    public int MaxConcurrentProbes { get; set; } = 2;

    /// <summary>Таймаут пробы одного хоста в секундах.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 8;

    /// <summary>Число повторных попыток пробы при сбое.</summary>
    public int ProbeRetries { get; set; } = 3;

    /// <summary>Включен ли автоматический запуск эволюции при деградации сети.</summary>
    public bool AutoTriggerEnabled { get; set; } = false;

    /// <summary>Доля сбойных запросов для автотриггера.</summary>
    public double AutoTriggerFailRate { get; set; } = 0.30;

    /// <summary>Кулдаун между автозапусками в часах.</summary>
    public int AutoTriggerCooldownHours { get; set; } = 2;

    /// <summary>Включена ли островная модель субпопуляций.</summary>
    public bool IslandModelEnabled { get; set; } = false;

    /// <summary>Интервал миграции между островами (каждые N поколений).</summary>
    public int IslandMigrationInterval { get; set; } = 5;

    /// <summary>Включить локальную доводку (мететический поиск) победителя.</summary>
    public bool EnableMemeticFineTuning { get; set; } = true;

    /// <summary>Включить комплексную верификацию работы сервисов в связке.</summary>
    public bool EnableSynergyVerification { get; set; } = true;

    /// <summary>Целевые хосты для сетевых проб.</summary>
    public List<string> TargetHosts { get; set; } =
    [
        "discord.com",
        "cdn.discordapp.com",
        "www.youtube.com",
        "googlevideo.com",
        "api.twitch.tv",
        "vk.com",
        "steamcommunity.com",
        "api.telegram.org"
    ];

    public static string DefaultPath => Path.Combine("config", "evolution-settings.json");

    /// <summary>Загружает настройки из JSON-файла; при отсутствии создаёт дефолтные.</summary>
    public static EvolutionSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        if (!File.Exists(target))
            return new EvolutionSettings();

        try
        {
            var text = File.ReadAllText(target);
            return JsonSerializer.Deserialize<EvolutionSettings>(text, JsonOptions) ?? new EvolutionSettings();
        }
        catch
        {
            return new EvolutionSettings();
        }
    }

    /// <summary>Сохраняет текущие настройки в JSON-файл.</summary>
    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(target, json);
    }
}
