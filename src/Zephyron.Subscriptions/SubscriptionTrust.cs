using System.Net;
using System.Text.RegularExpressions;

namespace Zephyron.Subscriptions;

/// <summary>Насколько подписке можно верить.</summary>
public enum TrustLevel
{
    Ok,

    /// <summary>Есть причины для осторожности: срок, квота, подозрительные адреса.</summary>
    Suspicious,

    /// <summary>Заглушка или обман: рабочих серверов в ней нет.</summary>
    Fake,
}

/// <param name="Level">Вердикт.</param>
/// <param name="Reasons">Причины словами для окна; пусто при <see cref="TrustLevel.Ok"/>.</param>
/// <param name="PseudoServers">Служебные записи панели — их нельзя брать выходом.</param>
public sealed record TrustVerdict(TrustLevel Level, IReadOnlyList<string> Reasons, IReadOnlyList<ProxyServer> PseudoServers);

/// <summary>
/// Разбор подписки на «пустышку»: серверы на 127.0.0.1, информационные строки вместо серверов,
/// одинаковый набор при вышедшем сроке или трафике.
/// </summary>
/// <remarks>
/// Только по самой подписке, без сети: ходить проверять каждый адрес — дорого, а эти признаки видны сразу.
/// Нулевая квота (<c>total=0</c>) признаком не служит — так панели пишут «без ограничения».
/// </remarks>
public static class SubscriptionTrust
{
    private static readonly Regex InfoName = new(
        @"истек|истёк|продл|закончил|обновит\w*\s+подпис|пополн|оплат|осталось\s+дн|expired|renew|subscription\s+(end|expir)|^\s*t\.me/|telegram\s*@|@\w+bot\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Почему запись — не сервер; <c>null</c> — сервер как сервер.</summary>
    public static string? PseudoWhy(ProxyServer server)
    {
        if (InfoName.IsMatch(server.Tag))
            return "название — сообщение панели";

        var host = server.Host.Trim().TrimEnd('.');

        // Локальный SOCKS/HTTP — обычная цепочка к программе на этом же компьютере, не обман.
        bool local = server.Protocol is ProxyProtocol.Socks or ProxyProtocol.Http;

        // Петля — заглушка панели, только если и порт пустышка (0/1): своя локальная
        // цепочка к xray или trojan на этом компьютере — законный сервер, его не трогаем.
        bool stubPort = server.Port is 0 or 1;

        if (IPAddress.TryParse(host, out var address))
            return !local && stubPort && IPAddress.IsLoopback(address) ? "адрес — этот же компьютер" : null;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !local && stubPort)
            return "адрес — этот же компьютер";

        if (host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase))
            return "адрес-пустышка (.invalid)";

        return null;
    }

    // Не «служебная запись»: example.com встречается в чужих проверочных наборах, но подписке в целом он вердикт портит.
    private static bool ExampleHost(ProxyServer s) =>
        s.Host.TrimEnd('.').ToLowerInvariant() is "example.com" or "example.org" or "example.net"
        || s.Host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase);

    private static bool PrivateOnly(ProxyServer s) =>
        IPAddress.TryParse(s.Host, out var a)
        && (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? a.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..] or [169, 254, ..]
            : a.IsIPv6LinkLocal || a.IsIPv6SiteLocal);

    public static TrustVerdict Assess(SubscriptionInfo info, DateTimeOffset now)
    {
        var reasons = new List<string>();
        var pseudo = info.Servers.Where(s => s.IsPseudo).ToList();
        var real = info.Servers.Except(pseudo).ToList();

        bool expired = info.ExpiresAt is { } until && until <= now;
        bool exhausted = info.TotalBytes > 0 && info.UsedBytes >= info.TotalBytes;
        bool fake = false;

        if (info.Servers.Count > 0 && real.Count == 0)
        {
            reasons.Add("все «серверы» подписки — служебные записи или адреса-пустышки");
            fake = true;
        }
        else if (pseudo.Count > 0)
        {
            reasons.Add($"служебных записей вместо серверов: {pseudo.Count} (они не используются)");
        }

        if (expired)
            reasons.Add("срок подписки истёк");

        if (exhausted)
            reasons.Add("трафик подписки исчерпан");

        if (real.Count > 0 && real.All(ExampleHost))
        {
            reasons.Add("все серверы — на адресах-примерах (example)");
            fake = true;
        }

        if (real.Count > 0 && real.All(PrivateOnly))
            reasons.Add("все серверы — на внутренних адресах локальной сети");

        if (real.Count > 1
            && real.Select(s => (s.Host.ToLowerInvariant(), s.Port, s.Credential)).Distinct().Count() == 1
            && (expired || exhausted))
        {
            reasons.Add("один и тот же сервер повторён под разными названиями, а срок или трафик кончились");
            fake = true;
        }
        else if (SubscriptionClient.LooksLikeStub(info))
        {
            reasons.Add("похоже на заглушку: все серверы на одном адресе и без шифрования");
        }

        var keyed = real.Where(s => s.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Tuic).ToList();

        if (keyed.Count > 0 && keyed.All(s => !Guid.TryParse(s.Credential, out _)))
            reasons.Add("у серверов ключи не в виде UUID");

        if (keyed.Count > 0 && keyed.All(s => Guid.TryParse(s.Credential, out var id) && id == Guid.Empty))
        {
            reasons.Add("у всех серверов нулевой ключ");
            fake = true;
        }

        return new(fake ? TrustLevel.Fake : reasons.Count > 0 ? TrustLevel.Suspicious : TrustLevel.Ok, reasons, pseudo);
    }
}
