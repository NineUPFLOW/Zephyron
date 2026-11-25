using System.Net.Http;
using System.Text.RegularExpressions;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Информация о текущем интернет-провайдере пользователя.
/// </summary>
public sealed record IspProfile(
    string IspName,
    string Asn,
    string PublicIp,
    string Country)
{
    public static readonly IspProfile Unknown = new("Локальная сеть / Неизвестно", "AS0", "", "RU");

    public string DisplaySummary => string.IsNullOrEmpty(Asn) || Asn == "AS0"
        ? IspName
        : $"{IspName} ({Asn})";
}

/// <summary>
/// Легковесный определитель оператора связи (ISP/ASN) без внешних громоздких зависимостей.
/// Результат кэшируется в оперативной памяти на время работы приложения.
/// </summary>
public static class IspIdentifier
{
    private static IspProfile? _cachedProfile;
    private static DateTime _lastResolve = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// Определяет текущего провайдера и номер автономной системы (ASN).
    /// </summary>
    public static async Task<IspProfile> ResolveCurrentIspAsync(CancellationToken ct = default)
    {
        if (_cachedProfile is not null && DateTime.UtcNow - _lastResolve < CacheDuration)
            return _cachedProfile;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            // Запрос через Cloudflare trace (очень быстрый и надежный)
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            string ip = "";
            string loc = "RU";

            try
            {
                var traceUrl = "https://1.1.1.1/cdn-cgi/trace";
                var resp = await client.GetStringAsync(traceUrl, cts.Token);
                var lines = resp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    if (parts[0] == "ip") ip = parts[1];
                    if (parts[0] == "loc") loc = parts[1];
                }
            }
            catch
            {
                // Fallback 1: api.ipify.org
                try
                {
                    ip = (await client.GetStringAsync("https://api.ipify.org", cts.Token)).Trim();
                }
                catch
                {
                    // Fallback 2: ifconfig.me
                    try
                    {
                        ip = (await client.GetStringAsync("https://ifconfig.me/ip", cts.Token)).Trim();
                    }
                    catch
                    {
                        // Все внешние эндпоинты недоступны
                    }
                }
            }

            // Пытаемся получить информацию об организации провайдера
            string ispName = "Интернет-провайдер";
            string asn = "AS";

            try
            {
                // Запрос к whois/rdap или ipwhois
                var infoResp = await client.GetStringAsync($"https://ipwhois.app/json/{ip}?objects=isp,asn,org,country", cts.Token);
                var ispMatch = Regex.Match(infoResp, "\"isp\":\"([^\"]+)\"");
                var orgMatch = Regex.Match(infoResp, "\"org\":\"([^\"]+)\"");
                var asnMatch = Regex.Match(infoResp, "\"asn\":\"([^\"]+)\"");

                if (ispMatch.Success)
                    ispName = ispMatch.Groups[1].Value;
                else if (orgMatch.Success)
                    ispName = orgMatch.Groups[1].Value;

                if (asnMatch.Success)
                    asn = asnMatch.Groups[1].Value;
            }
            catch
            {
                // Если вторичный эндпоинт недоступен, формируем на базе trace
                ispName = !string.IsNullOrEmpty(ip) ? $"Сеть ({ip})" : "Провайдер РФ";
            }

            var profile = new IspProfile(ispName, asn, ip, loc);
            _cachedProfile = profile;
            _lastResolve = DateTime.UtcNow;
            return profile;
        }
        catch
        {
            return _cachedProfile ?? IspProfile.Unknown;
        }
    }
}
