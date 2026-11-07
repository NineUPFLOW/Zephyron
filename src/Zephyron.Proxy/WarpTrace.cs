using System.Net.Http;

namespace Zephyron.Proxy;

/// <summary>Что Cloudflare увидел по ту сторону туннеля.</summary>
/// <param name="Country">Страна выхода, двумя буквами (<c>loc</c>); <c>null</c> — не назвал.</param>
/// <param name="WarpOn">Соединение пришло через WARP (<c>warp=on</c>).</param>
public sealed record WarpExit(string? Country, bool WarpOn);

/// <summary>Итог сверки выхода с ожидаемой страной.</summary>
public enum WarpExitVerdict
{
    /// <summary>Туннель не поднялся: ответа нет или трафик идёт мимо WARP.</summary>
    Down,

    /// <summary>Выход в ожидаемой стране либо ожидать было нечего.</summary>
    Match,

    /// <summary>WARP работает, но страна другая.</summary>
    Mismatch,
}

/// <summary>
/// Проверка выхода WARP: <c>cdn-cgi/trace</c> Cloudflare, снятый через туннель.
/// </summary>
/// <remarks>
/// Идёт через служебный вход движка (<see cref="EngineKeys"/>), как замер скорости:
/// в туннель попадает именно этот запрос, а не домашний канал. Страну называет
/// сам Cloudflare, поэтому это единственное свидетельство, которому можно верить:
/// настройка говорит, чего человек хотел, а не что получилось.
/// </remarks>
public static class WarpTrace
{
    public static async Task<WarpExit?> ReadAsync(CancellationToken cancellationToken)
    {
        if (EngineKeys.Current() is not { } keys)
            return null;

        try
        {
            using var handler = new SocketsHttpHandler { Proxy = keys.Proxy(SingBoxOptions.DefaultHealthPort), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

            return Parse(await http.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Не ответил — туннель ещё поднимается или лёг; решает вызывающий.
            return null;
        }
    }

    public static WarpExit Parse(string text)
    {
        var (_, country, _) = SpeedTest.ParseTrace(text);
        bool warp = text.Split('\n', StringSplitOptions.TrimEntries).Contains("warp=on", StringComparer.Ordinal);

        return new WarpExit(country?.ToUpperInvariant(), warp);
    }

    /// <param name="seen">Что вернула проверка; <c>null</c> — ответа не было.</param>
    /// <param name="expected">Страна выбранного сервера; пусто — страну не загадывали.</param>
    public static WarpExitVerdict Judge(WarpExit? seen, string? expected) =>
        seen is not { WarpOn: true } ? WarpExitVerdict.Down
        : string.IsNullOrEmpty(expected) || string.Equals(seen.Country, expected, StringComparison.OrdinalIgnoreCase)
            ? WarpExitVerdict.Match
            : WarpExitVerdict.Mismatch;
}
