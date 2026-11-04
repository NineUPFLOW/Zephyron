namespace Zephyron.Proxy;

/// <summary>Разделы списка резолверов и пометки по замеру.</summary>
/// <remarks>
/// Пометки — по обзору у этого человека, а не списком из кода: один и тот же резолвер у одного
/// провайдера закрыт, у другого честен. Туннелю важен DoH, системе — UDP, поэтому помечается
/// отдельно то и другое.
/// </remarks>
public static class DnsMarks
{
    public const string Blocked = "блокируется";

    public const string UdpSpoofed = "UDP подменяют";

    public static string Title(DnsGroup group) => group switch
    {
        DnsGroup.Intermediary => "Посредники",
        DnsGroup.Popular => "Популярные",
        DnsGroup.Open => "Без фильтрации",
        DnsGroup.Filtering => "С фильтрацией",
        _ => "Свои",
    };

    /// <summary>Пояснение раздела под заголовком.</summary>
    public static string Hint(DnsGroup group) => group switch
    {
        DnsGroup.Intermediary => "Закрытые по стране сервисы открывают через свои прокси. Действуют на имена мимо VPN.",
        DnsGroup.Popular => "Быстрые и надёжные, без своих правил.",
        DnsGroup.Open => "Отвечают как есть, ничего не отсекают.",
        DnsGroup.Filtering => "Отсекают рекламу, вредоносные или взрослые сайты.",
        _ => "Добавлены вами.",
    };

    /// <summary>Пометки одного резолвера по результату обзора; пусто — замечаний нет или не мерили.</summary>
    public static IReadOnlyList<string> Of(DnsSurveyRow row)
    {
        var marks = new List<string>();

        // Блокируется — когда не прошли ни DoH, ни DoT: причина названа (TIMEOUT, SYN DROP), а не «не мерили».
        if (row.DohMs is null && row.DohFailure.Length > 0 && row.DotMs is null && row.DotFailure.Length > 0)
            marks.Add(Blocked);

        if (row.Intercepted || row.Spoofed > 0)
            marks.Add(UdpSpoofed);

        return marks;
    }

    /// <summary>Резолверы по разделам в порядке <see cref="DnsGroup"/>; пустые разделы опускаются.</summary>
    public static IReadOnlyList<(DnsGroup Group, IReadOnlyList<DnsProvider> Items)> Sections(IEnumerable<DnsProvider> providers) =>
        providers
            .GroupBy(p => p.Section)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<DnsProvider>)g.ToList()))
            .ToList();
}
