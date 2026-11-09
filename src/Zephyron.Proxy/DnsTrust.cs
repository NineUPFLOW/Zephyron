using System.Text.Json;

namespace Zephyron.Proxy;

/// <summary>
/// Что последний замер DNS (<see cref="DnsSurvey"/>) сказал про каждый резолвер:
/// перехвачен ли он у провайдера. Лежит в runtime/dns-trust.json, чтобы
/// «Диагностика» и окно читали одно и то же, не гоняя замер заново.
/// </summary>
public static class DnsTrust
{
    public sealed record Entry(string Name, bool Intercepted, bool Clean, DateTimeOffset At);

    /// <summary>Файл кэша; тесты подменяют.</summary>
    public static string CachePath { get; set; } = Path.Combine("runtime", "dns-trust.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Перехвачен ли резолвер (адрес или имя из списка) по последнему замеру;
    /// <c>null</c> — замера не было, судить не по чему.
    /// </summary>
    public static bool? IsIntercepted(string resolver)
    {
        if (string.IsNullOrWhiteSpace(resolver))
            return null;

        return Load().TryGetValue(resolver.Trim(), out var e) ? e.Intercepted : null;
    }

    /// <summary>
    /// Имя резолвера, который тот же замер нашёл чистым и годным в туннель
    /// (быстрейший по DoH); <c>null</c> — такого нет.
    /// </summary>
    public static string? CleanAlternative(IEnumerable<DnsSurveyRow>? rows = null)
    {
        if (rows is not null)
        {
            return rows.Where(r => r.Provider.Choosable && r.DohMs is not null && !IsBad(r))
                .OrderBy(r => r.DohMs).Select(r => r.Provider.Name).FirstOrDefault();
        }

        // Из кэша скорости нет — берём первого чистого из списка, он идёт по популярности.
        var clean = Load().Values.Where(e => e.Clean).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DnsSurvey.All.Where(p => p.Choosable && clean.Contains(p.Name)).Select(p => p.Name).FirstOrDefault();
    }

    /// <summary>Подмена или перехват: UDP ответил чужой, либо закрытые имена вернулись заглушкой.</summary>
    internal static bool IsBad(DnsSurveyRow r) => r.Intercepted || r.Spoofed > 0;

    /// <summary>Запоминает свежий замер; вызывается из <see cref="DnsSurvey.SurveyAllAsync"/>.</summary>
    public static void Record(IEnumerable<DnsSurveyRow> rows)
    {
        rows = rows.ToList();

        // Пометки «блокируется / подменяют / не тронут» — рядом с кэшем, один замер на оба.
        ResolverVerdicts.Remember(rows, DateTime.Now,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CachePath))!, "resolver-verdicts.json"));

        try
        {
            var map = Load();
            var now = DateTimeOffset.UtcNow;

            foreach (var r in rows)
            {
                // Без единого ответа по UDP и без DoH судить не о чем — прежнее знание не затираем.
                if (r.UdpAnswered == 0 && r.DohMs is null)
                    continue;

                var bad = IsBad(r);
                var e = new Entry(r.Provider.Name, bad, !bad && r.DohMs is not null, now);

                map[r.Provider.Name] = e;

                if (r.Provider.TlsAddress is { } tls)
                    map[tls] = e;

                foreach (var u in r.Provider.Udp)
                    map[u] = e;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CachePath))!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(map, Json));
        }
        catch (Exception)
        {
            // Кэш — подсказка; не записался — замер от этого не хуже.
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath), Json)
                is { } m ? new(m, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
