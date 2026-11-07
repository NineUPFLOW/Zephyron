using System.Net;
using Zephyron.Core;

namespace Zephyron.Proxy;

/// <summary>DNS-посредник: имя, ключ для метки пина, адрес DoH и запасные адреса сервера.</summary>
public sealed record Intermediary(
    string Name,
    string Key,
    string DohName,
    IReadOnlyList<string> Fallback,
    string Path = "/dns-query",
    string? RelayName = null);

/// <summary>Ответ одного сервера на опрос: адреса по именам и ответ на служебное имя.</summary>
public sealed record ServerAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> Names,
    IReadOnlyList<IPAddress> RelayCheck)
{
    public IReadOnlySet<string> Unanswered { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record IntermediaryAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>> Answers,
    IReadOnlyList<string> Silent);

public enum RelayVerdict
{
    Silent,

    Relays,

    NotProxied,

    Refuses,

    Unanswered,

    Unconfirmed,
}

public sealed record RelayNow(
    Intermediary Server,
    string Name,
    RelayVerdict Verdict,
    IReadOnlyList<string> Addresses,
    bool ByRelayCheck = false);

/// <summary>Опрос посредников по DoH: какие прокси они отдают именам сейчас.</summary>
public static class IntermediaryDns
{
    public static IReadOnlyList<Intermediary> Servers { get; } =
    [
        new("Comss DNS", "comss", "dns.comss.one", ["212.109.221.20", "195.133.25.16", "212.109.195.93"]),

        new("Zapret DNS", "zapret", "dns.zapret.moe", ["188.114.96.1", "188.114.97.1"], RelayName: "relay.check.dns.zapret.moe"),
        new("GeoHide EU", "geohide-eu", "eu.geohide.ru", ["5.175.161.3", "5.175.161.40", "143.20.64.192"]),
        new("GeoHide US", "geohide-us", "us.geohide.ru", ["192.255.159.240", "192.255.159.241"]),
        // Замер 07.11 по DoH: оба отдают на ChatGPT свой прокси (wlrus — 191.217.212.16,
        // dagami — 45.86.65.240), через него ChatGPT и Gemini отвечают 200 с настоящим
        // сертификатом. Запасные адреса — честный DoH на то же имя.
        new("WLRUS DNS", "wlrus", "dns.wlrus.cfd", ["95.85.240.254", "138.124.254.134"]),
        new("Dagami DNS", "dagami", "dns.dagami.ru", ["45.86.65.240"]),
        new("DNS-AI", "dns-ai", "dns.dns-ai.ru", ["194.87.196.13", "45.151.102.219"]),
        new("AstraCat", "astracat", "dns.astracat.network", ["135.106.217.200", "135.106.197.22"]),
    ];

    public static Intermediary? ByKey(string? key) =>
        key is null ? null : Servers.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    internal const int RelayMinNames = 3;

    public static async Task<ServerAnswers?> AskServerAsync(
        Intermediary server,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();

        if (await RouteAsync(server, nic, cancellationToken) is not { } address)
            return null;

        var asked = names.Concat(Controls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var unanswered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var slots = new SemaphoreSlim(6);

        using var http = DnsSurvey.DohClient(address, nic);

        await Task.WhenAll(asked.Select(async host =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var answer = await DnsSurvey.DohQueryAsync(
                    http, server.DohName, server.Path, host, DnsWire.TypeA, cancellationToken);

                lock (result)
                {
                    result[host] = answer is { Code: 0 } ? answer.Addresses : [];

                    if (answer is null || answer.Code is not (0 or NxDomain))
                        unanswered.Add(host);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                lock (result)
                {
                    result[host] = [];
                    unanswered.Add(host);
                }
            }
            finally
            {
                slots.Release();
            }
        }));

        IReadOnlyList<IPAddress> relay = [];

        try
        {
            if (server.RelayName is { } relayName
                && await DnsSurvey.DohQueryAsync(http, server.DohName, server.Path, relayName, DnsWire.TypeA, cancellationToken)
                    is { Code: 0 } answered)
            {
                relay = answered.Addresses;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return new(result, relay) { Unanswered = unanswered };
    }

    internal static IReadOnlySet<string> Relays(ServerAnswers answers, IReadOnlyCollection<string> own)
    {
        var result = new HashSet<string>(StrongRelays(answers), StringComparer.Ordinal);
        var mine = own.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in answers.Names
                     .Where(p => mine.Contains(p.Key))
                     .SelectMany(p => p.Value.Select(a => (Address: a.ToString(), Name: p.Key)))
                     .GroupBy(x => x.Address, StringComparer.Ordinal))
        {
            if (group.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= RelayMinNames)
                result.Add(group.Key);
        }

        result.RemoveWhere(a => !Usable(a));

        return result;
    }

    internal static IReadOnlySet<string> StrongRelays(ServerAnswers answers)
    {
        var result = new HashSet<string>(Proxies(answers.Names), StringComparer.Ordinal);

        foreach (var address in answers.RelayCheck)
            result.Add(address.ToString());

        result.RemoveWhere(a => !Usable(a));

        return result;
    }

    private static bool Usable(string address) =>
        IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !BlockCheck.IsStub(ip);

    internal static IReadOnlyList<string> RelaysFor(ServerAnswers answers, IReadOnlySet<string> relays, string name) =>
        (answers.Names.TryGetValue(name, out var given) ? given : [])
            .Select(a => a.ToString())
            .Where(relays.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    public static async Task<RelayNow> NowAsync(Intermediary server, string name, CancellationToken cancellationToken)
    {
        var honest = HonestAsync(name, cancellationToken);
        ServerAnswers? answers;

        try
        {
            answers = await AskServerAsync(server, [name], cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            answers = null;
        }

        return Judge(server, answers, name, await honest);
    }

    private static async Task<IReadOnlyList<IPAddress>?> HonestAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return (await DnsSurvey.HonestAsync(name, DnsWire.TypeA, DnsSurvey.PhysicalInterface(), cancellationToken))
                is { Code: 0 } answer ? answer.Addresses : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static RelayNow Judge(Intermediary server, ServerAnswers? answers, string name, IReadOnlyList<IPAddress>? honest = null)
    {
        if (answers is null)
            return new(server, name, RelayVerdict.Silent, []);

        var strong = StrongRelays(answers);
        var relays = RelaysFor(answers, strong, name);

        if (relays.Count > 0)
        {
            var checkedBy = answers.RelayCheck.Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
            return new(server, name, RelayVerdict.Relays, relays, relays.Any(checkedBy.Contains));
        }

        var given = (answers.Names.TryGetValue(name, out var list) ? list : [])
            .Where(a => Usable(a.ToString()))
            .ToList();

        if (given.Count == 0)
        {
            return answers.Unanswered.Contains(name)
                ? new(server, name, RelayVerdict.Unanswered, [])
                : new(server, name, RelayVerdict.Refuses, []);
        }

        var shown = given
            .Select(a => a.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return honest is not null && given.Any(a => honest.Any(h => SameNet16(a, h)))
            ? new(server, name, RelayVerdict.NotProxied, shown)
            : new(server, name, RelayVerdict.Unconfirmed, shown);
    }

    private static bool SameNet16(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != b.AddressFamily || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();

        return x[0] == y[0] && x[1] == y[1];
    }

    private const int NxDomain = 3;

    private const string Probe = "example.com";

    public static async Task<IReadOnlyList<PinCandidate>> AskAsync(string host, CancellationToken cancellationToken) =>
        (await AskManyAsync([host], cancellationToken)).TryGetValue(host, out var found) ? found : [];

    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>>> AskManyAsync(
        IEnumerable<string> hosts,
        CancellationToken cancellationToken) =>
        (await ReachAsync(hosts, cancellationToken)).Answers;

    public static async Task<IntermediaryAnswers> ReachAsync(IEnumerable<string> hosts, CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();
        var routes = await Task.WhenAll(Servers.Select(s => RouteAsync(s, nic, cancellationToken)));

        var live = Servers.Zip(routes)
            .Where(p => p.Second is not null)
            .Select(p => (Server: p.First, Address: p.Second!))
            .ToList();

        var silent = Servers.Zip(routes).Where(p => p.Second is null).Select(p => p.First.Name).ToList();
        var wanted = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var asked = wanted.Concat(Controls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var raw = live.ToDictionary(l => l.Server.Name, _ => new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase));
        using var slots = new SemaphoreSlim(6);

        await Task.WhenAll(asked.Select(async host =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                await Task.WhenAll(live.Select(async l =>
                {
                    var answer = await DnsSurvey.DohQueryAsync(
                        l.Address, l.Server.DohName, l.Server.Path, host, DnsWire.TypeA, nic, cancellationToken);

                    lock (raw)
                        raw[l.Server.Name][host] = answer is { Code: 0 } ? answer.Addresses : [];
                }));
            }
            finally
            {
                slots.Release();
            }
        }));

        var proxies = raw.ToDictionary(p => p.Key, p => Proxies(p.Value));
        var result = new Dictionary<string, IReadOnlyList<PinCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in wanted)
        {
            result[host] = Candidates(live.Select(l => (
                l.Server.Name,
                (IReadOnlyList<IPAddress>)(raw[l.Server.Name].TryGetValue(host, out var a) ? a : [])
                    .Where(x => proxies[l.Server.Name].Contains(x.ToString()))
                    .OrderBy(x => x.ToString(), StringComparer.Ordinal)
                    .ToList())));
        }

        return new(result, silent);
    }

    private static readonly string[] Controls = ["claude.ai", "chatgpt.com", "grok.com", "gemini.google.com"];

    internal static IReadOnlySet<string> Proxies(IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> answers) =>
        Controls
            .SelectMany(c => answers.TryGetValue(c, out var a) ? a.Select(x => (Address: x.ToString(), Control: c)) : [])
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Control).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlyList<PinCandidate> Candidates(IEnumerable<(string Name, IReadOnlyList<IPAddress> Addresses)> answers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PinCandidate>();

        foreach (var (name, addresses) in answers)
        {
            foreach (var address in addresses)
            {
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || BlockCheck.IsStub(address)
                    || !seen.Add(address.ToString()))
                {
                    continue;
                }

                result.Add(new PinCandidate(address.ToString(), PinSource.Catalog, $"{name}, сейчас",
                    Servers.FirstOrDefault(s => s.Name == name)?.Key));
            }
        }

        return result;
    }

    private static async Task<string?> RouteAsync(Intermediary server, int? nic, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> named = [];

        try
        {
            named = (await DnsSurvey.HonestAsync(server.DohName, DnsWire.TypeA, nic, cancellationToken))?.Addresses ?? [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        var addresses = named
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Concat(server.Fallback)
            .Distinct(StringComparer.Ordinal)
            .Take(6);

        foreach (var address in addresses)
        {
            if (await DnsSurvey.DohQueryAsync(address, server.DohName, server.Path, Probe, DnsWire.TypeA, nic, cancellationToken) is not null)
                return address;
        }

        return null;
    }
}
