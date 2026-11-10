using Zephyron.Core;

namespace Zephyron.Proxy;

public sealed record PinRefreshResult(
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Dead,
    string? Backup,
    string? Error,
    int Checked = 0)
{
    public IReadOnlyList<string> Silent { get; init; } = [];

    public IReadOnlyList<string> Moved { get; init; } = [];

    public IReadOnlyList<string> Reordered { get; init; } = [];

    public int Suspects { get; init; }

    public int Assigned { get; init; }
}

/// <summary>Сколько пинов ведёт один посредник (или ни один).</summary>
public sealed record PinWatchGroup(string? Intermediary, int Count);

/// <summary>Что видно об автозамене без запуска проверки.</summary>
public sealed record PinWatch(
    DateTimeOffset? Last,
    IReadOnlyList<string> Suspects,
    IReadOnlyList<PinWatchGroup> Groups)
{
    public int Watched => Groups.Sum(g => g.Count);
}

/// <summary>Сетевые операции проверки, вынесенные наружу, чтобы тестировать ход без сети.</summary>
internal sealed class PinNet
{
    public required Func<string, string, CancellationToken, Task<PinVerdict>> Probe { get; init; }

    public required Func<Intermediary, IReadOnlyCollection<string>, CancellationToken, Task<ServerAnswers?>> Ask { get; init; }

    public required Func<CancellationToken, Task<bool>> Online { get; init; }

    internal static readonly string[] Controls = ["77.88.8.8", "87.250.250.242"];

    public static PinNet Real { get; } = new()
    {
        Probe = async (address, name, ct) =>
            (await PinPicker.ProbeAsync(new PinCandidate(address, PinSource.Pool, "пин"), name, ct)).Verdict,
        Ask = IntermediaryDns.AskServerAsync,
        Online = async ct =>
        {
            var tries = Controls.Select(async address =>
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));

                    await client.ConnectAsync(System.Net.IPAddress.Parse(address), 443, timeout.Token);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            });

            return (await Task.WhenAll(tries)).Any(ok => ok);
        },
    };
}

/// <summary>
/// Автозамена пинов к DNS-посредникам.
/// Пин живого адреса не трогается. Мёртвый первый адрес меняется на второй промах подряд
/// (или сразу при запуске кнопкой), только если есть связь и новый адрес сам ответил.
/// Если посредник, указанный в метке пина, отдаёт имени уже другие прокси, пин переезжает на них.
/// Срок между плановыми проверками — <see cref="Every"/>; отметка времени лежит в runtime.
/// </summary>
public static class PinRefresh
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    public static string StampPath => Path.Combine("runtime", "pin-refresh.txt");

    public static bool IsDue(DateTimeOffset now, string? stampPath = null)
    {
        try
        {
            var text = File.ReadAllText(stampPath ?? StampPath).Trim();

            return !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                   || now - last >= Every
                   || last > now;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static DateTimeOffset? LastChecked(string? stampPath = null)
    {
        try
        {
            var text = File.ReadAllText(stampPath ?? StampPath).Trim();

            return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                ? last
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PinWatch Watch(string? hostsPath = null, string? stampPath = null, string? suspectsPath = null)
    {
        var groups = new List<PinWatchGroup>();

        try
        {
            var own = HostsEditor.DesyncPins(hostsPath);
            var via = HostsEditor.PinsVia(hostsPath);

            var names = HostsEditor.PinsAll(hostsPath)
                .Where(p => !own.Contains(p.Key) && p.Value.Count > 0 && !p.Value.Contains(VideoNodeGuard.Refused))
                .Select(p => p.Key)
                .ToList();

            foreach (var server in IntermediaryDns.Servers)
            {
                int count = names.Count(n => via.TryGetValue(n, out var key)
                    && string.Equals(IntermediaryDns.ByKey(key)?.Key, server.Key, StringComparison.OrdinalIgnoreCase));

                if (count > 0)
                    groups.Add(new(server.Name, count));
            }

            int bare = names.Count(n => !via.TryGetValue(n, out var key) || IntermediaryDns.ByKey(key) is null);

            if (bare > 0)
                groups.Add(new(null, bare));
        }
        catch (Exception)
        {
        }

        return new PinWatch(
            LastChecked(stampPath),
            LoadSuspects(suspectsPath ?? SuspectsPath).Order(StringComparer.Ordinal).ToList(),
            groups);
    }

    public static void Mark(DateTimeOffset now, string? stampPath = null)
    {
        var target = stampPath ?? StampPath;

        if (Path.GetDirectoryName(target) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        Zephyron.Core.AtomicFile.WriteAllText(target, now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string SuspectsPath => Path.Combine("runtime", "pin-suspects.json");

    public static async Task<PinRefreshResult> RunAsync(CancellationToken cancellationToken, string? path = null, bool byHand = false)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            return await CheckAsync(PinNet.Real, path, path is null ? SuspectsPath : path + ".zy-suspects.json", byHand, cancellationToken);
        }
        finally
        {
            if (path is null)
                TryMark();

            Gate.Release();
        }
    }

    public static async Task<PinRefreshResult?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (!IsDue(DateTimeOffset.Now))
                return null;

            try
            {
                return await CheckAsync(PinNet.Real, null, SuspectsPath, byHand: false, cancellationToken);
            }
            finally
            {
                TryMark();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryMark()
    {
        try
        {
            Mark(DateTimeOffset.Now);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Heard(ServerAnswers Answers, IReadOnlySet<string> Relays, IReadOnlySet<string> Strong);

    private static async Task<ServerAnswers?> AskAsync(
        PinNet net, Intermediary server, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        try
        {
            return await net.Ask(server, names, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static async Task<PinRefreshResult> CheckAsync(
        PinNet net,
        string? path,
        string suspectsPath,
        bool byHand,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> pins;
        IReadOnlyDictionary<string, string> profiles;

        try
        {
            var own = HostsEditor.DesyncPins(path);

            pins = HostsEditor.PinsAll(path)
                .Where(p => !own.Contains(p.Key) && p.Value.Count > 0 && !p.Value.Contains(VideoNodeGuard.Refused))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

            profiles = HostsEditor.PinsVia(path)
                .Where(p => pins.ContainsKey(p.Key) && IntermediaryDns.ByKey(p.Value) is not null)
                .ToDictionary(p => p.Key, p => IntermediaryDns.ByKey(p.Value)!.Key, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return new([], [], null, "файл hosts не прочитан: " + ex.GetBaseException().Message);
        }

        if (pins.Count == 0)
            return new([], [], null, null);

        var names = pins.Keys.Order(StringComparer.Ordinal).ToList();

        var asked = new List<(Intermediary Server, List<string> Own, List<string> Names)>();

        foreach (var server in IntermediaryDns.Servers)
        {
            var mine = names.Where(n => profiles.TryGetValue(n, out var key) && key == server.Key).ToList();
            var all = mine.Concat(names.Where(n => !profiles.ContainsKey(n))).ToList();

            if (all.Count > 0)
                asked.Add((server, mine, all));
        }

        var replies = await Task.WhenAll(asked.Select(a => AskAsync(net, a.Server, a.Names, cancellationToken)));
        var silent = asked.Zip(replies).Where(p => p.Second is null).Select(p => p.First.Server.Name).ToList();

        var heard = asked.Zip(replies)
            .Where(p => p.Second is not null)
            .ToDictionary(
                p => p.First.Server.Key,
                p => new Heard(p.Second!, IntermediaryDns.Relays(p.Second!, p.First.Own), IntermediaryDns.StrongRelays(p.Second!)));

        IReadOnlyList<string> Given(string key, string name, bool strong = false) =>
            heard.TryGetValue(key, out var h) ? IntermediaryDns.RelaysFor(h.Answers, strong ? h.Strong : h.Relays, name) : [];

        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names.Where(n => !profiles.ContainsKey(n)))
        {
            if (IntermediaryDns.Servers.FirstOrDefault(s => Given(s.Key, name).Intersect(pins[name], StringComparer.Ordinal).Any())
                is { } owner)
            {
                assigned[name] = owner.Key;
            }
        }

        string? Profile(string name) =>
            profiles.TryGetValue(name, out var key) ? key : assigned.GetValueOrDefault(name);

        var verdicts = new Dictionary<string, (PinVerdict Verdict, string Name)>(StringComparer.Ordinal);

        async Task<PinVerdict> ProbeAsync(string address, string name)
        {
            if (verdicts.TryGetValue(address, out var first)
                && (first.Verdict != PinVerdict.Refused || string.Equals(first.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return first.Verdict;
            }

            var key = verdicts.ContainsKey(address) ? address + " " + name.ToLowerInvariant() : address;

            if (verdicts.TryGetValue(key, out var again))
                return again.Verdict;

            PinVerdict verdict;

            try
            {
                verdict = await net.Probe(address, name, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                verdict = PinVerdict.Dead;
            }

            verdicts[key] = (verdict, name);

            return verdict;
        }

        var heads = names.ToDictionary(n => n, n => pins[n].FirstOrDefault(Checkable), StringComparer.OrdinalIgnoreCase);
        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (heads[name] is { } head && !alive.ContainsKey(head))
                alive[head] = await ProbeAsync(head, name) != PinVerdict.Dead;
        }

        var before = LoadSuspects(suspectsPath);

        int deadCount = alive.Values.Count(ok => !ok);

        if (alive.Count >= 4 && deadCount * 2 > alive.Count)
        {
            SaveSuspects(suspectsPath, before.Where(a => alive.TryGetValue(a, out var ok) && !ok).ToList());

            return new([], [], null,
                $"молчат {deadCount} адресов из {alive.Count}: скорее всего, нет связи, пины оставлены как есть", alive.Count)
            { Silent = silent };
        }

        var moves = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var confirm = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (heads[name] is not { } head || Profile(name) is not { } profile || !heard.TryGetValue(profile, out var h))
                continue;

            bool live = alive[head];
            var given = Given(profile, name, strong: live);

            if (given.Count == 0 || given.Contains(head, StringComparer.Ordinal))
                continue;

            if (live && h.Relays.Contains(head))
                continue;

            moves[name] = given;

            if (live)
            {
                if (!confirm.TryGetValue(profile, out var list))
                    confirm[profile] = list = [];

                list.Add(name);
            }
        }

        var again = await Task.WhenAll(confirm.Select(async c =>
            (Names: c.Value, Reply: await AskAsync(net, IntermediaryDns.ByKey(c.Key)!, c.Value, cancellationToken))));

        foreach (var (list, reply) in again)
        {
            foreach (var name in list)
            {
                var head = heads[name]!;

                if (reply is null
                    || reply.Names.Values.Any(a => a.Any(x => x.ToString() == head))
                    || IntermediaryDns.RelaysFor(reply, IntermediaryDns.StrongRelays(reply), name).Count == 0)
                {
                    moves.Remove(name);
                }
            }
        }

        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var via = new Dictionary<string, string>(assigned, StringComparer.OrdinalIgnoreCase);
        var replaced = new List<(string Name, string Line)>();
        var moved = new List<(string Name, string Line)>();
        var reordered = new List<(string Name, string Line)>();
        var pending = new List<string>();
        var dead = new List<string>();
        bool? online = null;

        async Task<List<string>> LiveAsync(IEnumerable<string> addresses, string name)
        {
            var live = new List<string>();

            foreach (var address in addresses)
            {
                if (alive.TryGetValue(address, out var known) && !known)
                    continue;

                if (await ProbeAsync(address, name) is PinVerdict.Works or PinVerdict.Challenge)
                    live.Add(address);

                if (live.Count == 2)
                    break;
            }

            return live;
        }

        foreach (var name in names)
        {
            var current = pins[name];
            var head = heads[name];
            var profile = Profile(name);

            if (moves.TryGetValue(name, out var given))
            {
                var live = await LiveAsync(given, name);

                if (live.Count > 0)
                {
                    entries[name] = live;
                    via[name] = profile!;
                    moved.Add((name, $"{IntermediaryDns.ByKey(profile)!.Name}: {Join(current)} → {Join(live)}"));
                    continue;
                }
            }

            if (head is null || alive[head])
                continue;

            if (!byHand && !before.Contains(head))
                continue;

            online ??= await OnlineAsync(net, cancellationToken);

            if (online == false)
                continue;

            if (profile is not null)
            {
                var live = await LiveAsync(Given(profile, name).Where(a => a != head), name);

                if (live.Count > 0)
                {
                    entries[name] = live;
                    via[name] = profile;
                    replaced.Add((name, $"{Join(current)} → {Join(live)}"));
                    continue;
                }
            }

            string? spare = null;

            foreach (bool strict in new[] { true, false })
            {
                foreach (var address in current.Where(a => a != head && Checkable(a)))
                {
                    var verdict = await ProbeAsync(address, name);

                    if (strict ? verdict is PinVerdict.Works or PinVerdict.Challenge : verdict != PinVerdict.Dead)
                    {
                        spare = address;
                        break;
                    }
                }

                if (spare is not null)
                    break;
            }

            if (spare is not null)
            {
                entries[name] = [spare, .. current.Where(a => a != spare)];

                if (profile is not null)
                    via[name] = profile;

                reordered.Add((name, $"{head} → первым {spare}"));
                continue;
            }

            pending.Add(name);
        }

        var foreign = new Dictionary<string, Heard>(StringComparer.Ordinal);

        if (pending.Any(profiles.ContainsKey))
        {
            var ask = IntermediaryDns.Servers
                .Select(s => (Server: s, Names: pending.Where(n => profiles.TryGetValue(n, out var k) && k != s.Key).ToList()))
                .Where(a => a.Names.Count > 0)
                .ToList();

            var answers = await Task.WhenAll(ask.Select(a => AskAsync(net, a.Server, a.Names, cancellationToken)));

            foreach (var (a, reply) in ask.Zip(answers))
            {
                if (reply is not null)
                    foreach (var name in a.Names)
                        foreign[a.Server.Key + " " + name.ToLowerInvariant()] = new Heard(reply, IntermediaryDns.Relays(reply, []), IntermediaryDns.StrongRelays(reply));
            }
        }

        foreach (var name in pending)
        {
            var head = heads[name];
            var profile = Profile(name);
            bool found = false;

            foreach (var server in IntermediaryDns.Servers.Where(s => s.Key != profile))
            {
                var given = profiles.ContainsKey(name)
                    ? foreign.TryGetValue(server.Key + " " + name.ToLowerInvariant(), out var f)
                        ? IntermediaryDns.RelaysFor(f.Answers, f.Relays, name)
                        : []
                    : Given(server.Key, name);

                var live = await LiveAsync(given.Where(a => a != head), name);

                if (live.Count == 0)
                    continue;

                entries[name] = live;
                via[name] = server.Key;
                replaced.Add((name, $"{Join(pins[name])} → {Join(live)}"));
                found = true;
                break;
            }

            if (!found)
                dead.Add(name);
        }

        var suspects = alive.Where(p => !p.Value).Select(p => p.Key).ToList();
        SaveSuspects(suspectsPath, suspects);

        string? offline = online == false
            ? "нет связи с контрольными адресами: замены мёртвых пинов отложены"
            : null;

        if (entries.Count == 0)
        {
            return new([], dead, null, offline, alive.Count)
            {
                Silent = silent,
                Suspects = suspects.Count,
            };
        }

        try
        {
            var result = HostsEditor.Repin(entries, path, via: via,
                assignedAt: assigned.Keys.ToDictionary(n => n, n => pins[n], StringComparer.OrdinalIgnoreCase));

            if (path is null)
                HostsEditor.FlushDns();

            if (result.RevertedBy is { } by)
            {
                return new([], dead, result.Backup, $"запись в hosts откатил {by}", alive.Count)
                {
                    Silent = silent,
                    Suspects = suspects.Count,
                };
            }

            return new(Grouped(replaced), dead, result.Backup, offline, alive.Count)
            {
                Silent = silent,
                Moved = Grouped(moved),
                Reordered = Grouped(reordered),
                Suspects = suspects.Count,
                Assigned = assigned.Count,
            };
        }
        catch (Exception ex)
        {
            return new([], dead, null, "запись в hosts не удалась: " + ex.GetBaseException().Message, alive.Count)
            {
                Silent = silent,
                Suspects = suspects.Count,
            };
        }
    }

    private static async Task<bool> OnlineAsync(PinNet net, CancellationToken cancellationToken)
    {
        try
        {
            return await net.Online(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool Checkable(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && !BlockCheck.IsStub(ip);

    private static string Join(IEnumerable<string> addresses) => string.Join(", ", addresses);

    private static IReadOnlyList<string> Grouped(IEnumerable<(string Name, string Line)> lines) =>
        lines
            .GroupBy(x => x.Line, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()} {Names(g.Count())})")
            .ToList();

    internal static IReadOnlySet<string> LoadSuspects(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new HashSet<string>(StringComparer.Ordinal);

            return (System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveSuspects(string path, IReadOnlyCollection<string> addresses)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            Zephyron.Core.AtomicFile.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(addresses.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
    private static string Names(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => "имя",
        (2 or 3 or 4, not (12 or 13 or 14)) => "имени",
        _ => "имён",
    };
}
