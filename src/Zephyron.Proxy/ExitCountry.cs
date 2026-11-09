using System.Text.Json;

namespace Zephyron.Proxy;

/// <summary>
/// Страна, из которой выход туннеля реально выходит в сеть, — по замеру,
/// а не по флагу в имени. «🇷🇺 Основной» бывает балансировщиком с выходом
/// за границей, а флаг в имени — просто подпись. Кэш — runtime/exit-country.json.
/// </summary>
public static class ExitCountry
{
    public sealed record Entry(string Country, DateTimeOffset At);

    /// <summary>Файл кэша; тесты подменяют.</summary>
    public static string CachePath { get; set; } = Path.Combine("runtime", "exit-country.json");

    /// <summary>Как долго замер считается свежим.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(6);

    // Неудачные попытки не пишем в кэш, но и не повторяем каждым опросом.
    private static readonly Dictionary<string, DateTimeOffset> Tried = new(StringComparer.Ordinal);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    private static readonly object Sync = new();

    /// <summary>Код страны (ISO, верхним регистром) измеренного выхода; <c>null</c> — не мерили.</summary>
    public static string? Of(string tag) => Load().TryGetValue(tag, out var e) ? e.Country : null;

    /// <summary>
    /// «Отечественный» ли выход для фильтра «Только зарубежные»: измеренная страна
    /// важнее флага в имени, нет замера — прежний признак по имени.
    /// </summary>
    public static bool IsDomestic(string tag) =>
        Of(tag) is { } country
            ? string.Equals(country, "RU", StringComparison.OrdinalIgnoreCase)
            : SingBoxConfigCompiler.LooksDomestic(tag);

    /// <summary><c>loc=XX</c> из ответа cdn-cgi/trace; <c>null</c> — нет или не код страны.</summary>
    public static string? Parse(string? trace)
    {
        foreach (var line in (trace ?? "").Split('\n'))
        {
            var l = line.Trim();

            if (l.StartsWith("loc=", StringComparison.Ordinal) && l.Length == 6
                && char.IsAsciiLetterUpper(l[4]) && char.IsAsciiLetterUpper(l[5]))
            {
                return l[4..];
            }
        }

        return null;
    }

    /// <summary>Пора ли мерить выход: замера нет или он старше <see cref="Fresh"/>, и недавно не пробовали.</summary>
    public static bool Due(string tag, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;

        if (Load().TryGetValue(tag, out var e) && at - e.At < Fresh)
            return false;

        lock (Sync)
            return !Tried.TryGetValue(tag, out var t) || at - t >= RetryAfter;
    }

    public static void Store(string tag, string country, DateTimeOffset? now = null)
    {
        try
        {
            // Под замком и атомарно: замеры разных выходов идут в фоне и могли затереть друг друга.
            lock (Sync)
            {
                var map = Load();

                map[tag] = new Entry(country, now ?? DateTimeOffset.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CachePath))!);
                Zephyron.Core.AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(map));
            }
        }
        catch (Exception)
        {
            // Подсказка, не данные: не записалась — выход определится по имени.
        }
    }

    /// <summary>Мерит выход, если пора: trace запрашивается ЧЕРЕЗ туннель (вход проверки).</summary>
    public static async Task MeasureAsync(string tag, int proxyPort, EngineKeys? keys, CancellationToken cancellationToken)
    {
        if (!Due(tag))
            return;

        lock (Sync)
            Tried[tag] = DateTimeOffset.UtcNow;

        try
        {
            using var handler = new HttpClientHandler { Proxy = EngineKeys.Loopback(proxyPort, keys), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            if (Parse(await http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken)) is { } loc)
                Store(tag, loc);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? [], StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
