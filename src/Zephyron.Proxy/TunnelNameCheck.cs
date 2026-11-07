using System.Net;
using Zephyron.Core.Rules;

namespace Zephyron.Proxy;

/// <summary>Итог проверки имён «через VPN»; <c>Ok</c> — все получили подставной адрес.</summary>
public sealed record TunnelNameVerdict(bool Ok, string Text);

/// <summary>
/// Получают ли имена, которые правила ведут в VPN, подставной адрес движка,
/// когда их разрешает Windows, как любая программа.
/// </summary>
/// <remarks>
/// Настоящий адрес значит, что имя разрешил не движок (сторонний DNS-клиент,
/// DoH в системе), а в «Гибриде» TUN такой адрес не забирает.
/// </remarks>
public static class TunnelNameCheck
{
    public const int MaxNames = 3;

    /// <summary>
    /// До <see cref="MaxNames"/> имён, которые правила ведут в VPN: точные домены
    /// и домены из списков, без масок и без прибитых в hosts.
    /// </summary>
    public static IReadOnlyList<string> Names(RuleSet rules, IEnumerable<string> pinned)
    {
        var pins = pinned.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. rules.Rules
            .Where(r => r.Enabled && !r.Off && r.Mode == RoutingMode.Proxy)
            .SelectMany(r => r.Match switch
            {
                MatchKind.Domain => [r.Value],
                MatchKind.HostList => r.HostListDomains,
                _ => (IReadOnlyList<string>)[],
            })
            .Select(d => d.Trim().TrimStart('.'))
            .Where(d => d.Contains('.') && d.IndexOfAny(['*', '?', '/', ' ']) < 0 && !IPAddress.TryParse(d, out _) && !pins.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNames)];
    }

    /// <param name="answers">Имя — ответ Windows; пустой ответ значит «не разрешилось».</param>
    /// <param name="v4Range">Диапазон подставных IPv4 из конфига; <c>null</c> — по умолчанию.</param>
    /// <param name="resolvers">Резолверы Windows — для подсказки, кто мог ответить.</param>
    public static TunnelNameVerdict Judge(
        IReadOnlyList<(string Name, IReadOnlyList<IPAddress> Answers)> answers,
        string? v4Range,
        IReadOnlyList<string> resolvers)
    {
        var real = answers
            .Where(a => a.Answers.Count > 0 && a.Answers.Any(ip => !TunnelHealth.IsFakeIp(ip, v4Range)))
            .ToList();

        if (real.Count == 0)
            return new(true, $"Имена «через VPN» ({string.Join(", ", answers.Select(a => a.Name))}) получают подставной адрес — идут в туннель.");

        var list = string.Join("; ", real.Select(a => $"{a.Name} → {string.Join(", ", a.Answers)}"));
        var who = resolvers.Count > 0 ? $" Резолверы Windows: {string.Join(", ", resolvers.Select(Plain))}." : string.Empty;

        return new(false, $"Имя «через VPN» разрешилось в настоящий адрес, а не подставной ({list}): ответил не наш DNS, "
            + "и в «Гибриде» такой трафик идёт мимо туннеля." + who
            + " Отключите сторонний DNS-клиент (или DNS в браузере/системе) либо используйте наш DNS, затем перезапустите движки.");
    }

    private static string Plain(string prefix) => prefix.Split('/')[0];
}
