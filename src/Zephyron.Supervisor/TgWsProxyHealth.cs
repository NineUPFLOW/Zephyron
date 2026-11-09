using System.Globalization;
using System.Text.RegularExpressions;

namespace Zephyron.Supervisor;

/// <summary>
/// Перегрузка общих доменов Cloudflare у прокси Telegram — по его журналу:
/// «WARN … CF WS DC2 failed on kws2.x.co.uk: HTTP 503 from server».
/// </summary>
public static partial class TgWsProxyHealth
{
    [GeneratedRegex(@"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?Z)\s+WARN\s+\S+\s+CF WS .*HTTP 503")]
    private static partial Regex Cf503Line();

    /// <summary>Ответов 503 по CF WS за последнее <paramref name="window"/>; журнала нет — 0.</summary>
    public static int RecentCf503(TimeSpan window, string? logPath = null, DateTimeOffset? now = null)
    {
        var path = logPath ?? Path.Combine("runtime", "tg-ws-proxy.log");

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // Хвост хватает: окно — минуты, а журнал пишется строка на сбой.
            const int Tail = 512 * 1024;

            if (stream.Length > Tail)
                stream.Seek(-Tail, SeekOrigin.End);

            using var reader = new StreamReader(stream);

            return Count(reader.ReadToEnd().Split('\n'), window, now ?? DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal static int Count(IEnumerable<string> lines, TimeSpan window, DateTimeOffset now)
    {
        int n = 0;

        foreach (var line in lines)
        {
            var m = Cf503Line().Match(line);

            if (m.Success && DateTimeOffset.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                && now - at <= window && at <= now.AddMinutes(1))
            {
                n++;
            }
        }

        return n;
    }
}
