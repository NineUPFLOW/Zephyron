namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Результат сетевой пробы одного хоста.
/// </summary>
public sealed record ProbeOutcome
{
    public bool Success { get; init; }
    public long LatencyMs { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Результат комплексной оценки приспособленности (фитнеса) одной особи.
/// </summary>
public sealed record FitnessResult
{
    /// <summary>Сводный балл фитнеса (0.0..1.0).</summary>
    public double Score { get; init; }

    /// <summary>Количество открывшихся хостов.</summary>
    public int Passed { get; init; }

    /// <summary>Всего проверенных хостов.</summary>
    public int Total { get; init; }

    /// <summary>Средняя задержка ответа в миллисекундах.</summary>
    public long AvgLatencyMs { get; init; }

    /// <summary>Замеренная скорость потокового видео (L7 Throughput) в КБ/с.</summary>
    public double StreamingSpeedKBps { get; init; }

    /// <summary>Успешно ли пройдена проба голосового UDP трафика Discord Voice.</summary>
    public bool DiscordVoicePassed { get; init; }

    /// <summary>Детальные исходы по каждому хосту.</summary>
    public IReadOnlyDictionary<string, ProbeOutcome> HostResults { get; init; } = new Dictionary<string, ProbeOutcome>();

    public string Summary => $"{Score:P0} ({Passed}/{Total}, {AvgLatencyMs} мс, {StreamingSpeedKBps:F0} КБ/с)";
}

/// <summary>
/// Результат верификации работы пресета в комплексной связке сервисов.
/// </summary>
public sealed record CrossServiceSynergyResult
{
    public bool IsAllPassed { get; init; }
    public int PassedCount { get; init; }
    public int TotalCount { get; init; }
    public double SynergyScore { get; init; }
    public IReadOnlyDictionary<string, string> HostStatuses { get; init; } = new Dictionary<string, string>();
    public string Verdict { get; init; } = "";
}
