namespace Zephyron.Core.Diagnostics;

/// <summary>
/// Видеосервер YouTube, с которого человеку на деле идёт видео: <c>rr1---sn-….googlevideo.com</c>.
/// </summary>
/// <remarks>
/// <para>
/// redirector.googlevideo.com/report_mapping отвечает строкой «адрес => узел (подсеть)», например
/// «… => muc12s10 (…)». Имя узла посимвольной подстановкой превращается в имя видеосервера:
/// muc12s10 → sn-h0jelne7. Так делают GoodCheck и zapret4rocket; таблица — из Zapret GUI
/// (blockcheck/googlevideo_discovery.py, MIT, censorliber).
/// </para>
/// <para>
/// Зачем: redirector отвечает и тогда, когда провайдер режет сам видеосервер, — страница
/// открывается, видео стоит. Проверять надо тот сервер, с которого видео и идёт.
/// </para>
/// </remarks>
public static class GoogleVideoNode
{
    private const string From = "uzpkfa50vqlgb61wrmhc72xsnid83ytoje94-";
    private const string To = "0123456789abcdefghijklmnopqrstuvwxyz-";

    public const string MappingUrl = "https://redirector.googlevideo.com/report_mapping?di=no";

    /// <summary>Имя видеосервера по имени узла; <c>null</c> — имя не похоже на настоящее.</summary>
    public static string? FromCluster(string? cluster)
    {
        var name = cluster?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(name) || name.Any(c => From.IndexOf(c) < 0))
            return null;

        return "rr1---sn-" + new string(name.Select(c => To[From.IndexOf(c)]).ToArray()) + ".googlevideo.com";
    }

    /// <summary>Имя узла из ответа report_mapping (после «=&gt;»), без адреса человека.</summary>
    public static string? Cluster(string? answer)
    {
        var match = System.Text.RegularExpressions.Regex.Match(answer ?? string.Empty, @"=>\s*([a-z0-9-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>Видеосервер этого человека или <c>null</c>, если redirector не ответил.</summary>
    public static async Task<string?> FindAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var answer = await http.GetStringAsync(MappingUrl, cancellationToken).ConfigureAwait(false);
            return FromCluster(Cluster(answer));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
