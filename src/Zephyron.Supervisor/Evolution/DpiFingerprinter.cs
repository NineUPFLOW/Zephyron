namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Профиль сигнатуры оборудования ТСПУ (DPI) провайдера.
/// </summary>
public sealed record DpiProfile
{
    public string IspFilterType { get; init; } = "Стандартный L7 ТСПУ";
    public bool PrefersSeqOvl { get; init; } = true;
    public bool PrefersSniExt { get; init; } = true;
    public bool RequiresQuicDrop { get; init; } = true;
    public IReadOnlyList<string> RecommendedSeedSteps { get; init; } = [];
}

/// <summary>
/// Экспресс-диагност оборудования DPI для фокусировки генетических операторов.
/// </summary>
public static class DpiFingerprinter
{
    /// <summary>
    /// Вычисляет оптимальный начальный профиль десинхронизации под конкретное оборудование цензуры.
    /// </summary>
    public static DpiProfile DetectProfile(IReadOnlyDictionary<string, bool> initialProbes)
    {
        bool youtubeFails = initialProbes.TryGetValue("www.youtube.com", out bool yt) && !yt;
        bool discordFails = initialProbes.TryGetValue("discord.com", out bool dc) && !dc;

        var recommended = new List<string>();

        if (youtubeFails)
        {
            // Для жесткого ТСПУ на видеосервисах
            recommended.Add("multisplit:pos=sniext+1,midsld:seqovl=652");
            recommended.Add("multisplit:pos=1,sniext+2:seqovl=568");
            recommended.Add("hostfakesplit_multi:pos=sniext+1");
        }
        else
        {
            recommended.Add("multisplit:pos=1:seqovl=568");
            recommended.Add("multidisorder:pos=1");
        }

        if (discordFails)
        {
            recommended.Add("multisplit:pos=sniext+1:seqovl=652");
            recommended.Add("fake:blob=tls_discord");
        }

        return new DpiProfile
        {
            IspFilterType = youtubeFails && discordFails ? "Агрессивный ТСПУ (РДП/Гарда)" : "Умеренный DPI",
            PrefersSeqOvl = true,
            PrefersSniExt = youtubeFails,
            RequiresQuicDrop = true,
            RecommendedSeedSteps = recommended
        };
    }
}
