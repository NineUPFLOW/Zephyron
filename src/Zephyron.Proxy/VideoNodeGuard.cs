using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Zephyron.Core.Diagnostics;

namespace Zephyron.Proxy;

/// <summary>
/// Узлы видео YouTube, которые не отдают видео (фильтр по адресу, сломанный кэш провайдера), — мгновенный отказ вместо зависания.
/// </summary>
/// <remarks>
/// <para>
/// С конца октября 2026 часть групп серверов Google (по 32 адреса) не пропускает TLS
/// с любым именем: фильтр узнаёт ответ сервера, и десинк тут бессилен. Плеер, попавший
/// на такой узел, ждёт десятки секунд и застывает. Проверено 10.11 вживую: если узел
/// отказывает сразу, плеер в ту же секунду берёт запасной (<c>fallback_count</c>)
/// и видео идёт — без VPN. Так же лечится кэш Google внутри сети провайдера, который
/// принимает соединение и молчит: плеер уходит на узел Google.
/// </para>
/// <para>
/// Отказ делается записью <c>0.0.0.0</c> в нашем блоке hosts с пометкой
/// <see cref="Via"/>: соединение рвётся мгновенно, движки не перезапускаются.
/// Запись живёт, пока адрес под фильтром: каждые <see cref="Recheck"/> адрес
/// проверяется заново, и не дольше <see cref="MaxAge"/>.
/// </para>
/// </remarks>
public static class VideoNodeGuard
{
    /// <summary>Пометка наших отказов в блоке hosts (<see cref="HostsEditor.ViaMark"/>).</summary>
    public const string Via = "video-guard";

    public const string Refused = "0.0.0.0";

    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    /// <summary>Сеть под окном помнится дольше: запись в списке winws2 безвредна, а новый узел той же группы идёт сразу.</summary>
    public static readonly TimeSpan WindowMaxAge = TimeSpan.FromDays(7);

    /// <summary>Больше отказов в hosts не бывает: остальное решает окно или плеер сам.</summary>
    public const int MaxRefused = 32;

    /// <summary>
    /// Сеть /24 узла: группы под фильтром по 32 адреса, и соседний узел той же группы
    /// получает окно сразу, без первого зависания. Список winws2 так и остаётся коротким.
    /// </summary>
    public static string Network(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return $"{b[0]}.{b[1]}.{b[2]}.0/24";
    }

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(3);

    public static string StatePath => Path.Combine("runtime", "video-guard.json");

    /// <summary>
    /// Имя узла, его настоящий адрес и когда меры приняты или проверены; <paramref name="Window"/> —
    /// узел ожил под малым окном (<see cref="Zapret.VideoWindow"/>), а не отключён в hosts.
    /// </summary>
    public sealed record Entry(string Address, DateTimeOffset At, DateTimeOffset Checked, bool Window = false);

    /// <summary>Узлы видео из кэша DNS Windows с их адресами IPv4.</summary>
    public static IReadOnlyList<(string Host, IPAddress Address)> Candidates()
    {
        var found = new List<(string, IPAddress)>();

        foreach (var host in RecentNodes.FromDnsCache().Where(IsVideoNode).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var address in Dns.GetHostAddresses(host).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    if (!IPAddress.Any.Equals(address))
                        found.Add((host, address));
                }
            }
            catch (SocketException)
            {
            }
        }

        return found;
    }

    /// <summary>Узел видео: <c>rrN---sn-….googlevideo.com</c> или <c>rrN---sn-….c.youtube.com</c>.</summary>
    public static bool IsVideoNode(string host) =>
        host.StartsWith("rr", StringComparison.OrdinalIgnoreCase)
        && host.Contains("---sn-", StringComparison.OrdinalIgnoreCase)
        && (host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase)
            // Те же узлы под вторым именем: плеер проверяет по нему связь (generate_204) до видео,
            // и зависший узел держал старт ролика (замер 10.11: 30 с).
            || host.EndsWith(".c.youtube.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Отдаёт ли узел ответ так же, как браузеру: его адрес, его имя, путь через десинк,
    /// короткий запрос <c>/generate_204</c>. Ловит и фильтр по адресу, и кэш провайдера,
    /// который рвёт или душит соединение после рукопожатия. Дважды, чтобы разовый
    /// сбой сети не отключил живой узел.
    /// </summary>
    /// <returns><c>true</c> — узел не отвечает; <c>false</c> — отвечает; <c>null</c> — не ответил и TCP: фильтр на SYN или нет сети, решает <see cref="RunAsync"/>.</returns>
    public static async Task<bool?> IsFilteredAsync(string host, IPAddress address, CancellationToken cancellationToken, int attempts = 2)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            switch (await AskAsync(host, address, cancellationToken))
            {
                case Outcome.Answered:
                    return false;
                case Outcome.NoTcp:
                    return null;
            }
        }

        return true;
    }

    private enum Outcome { Answered, NoTcp, Silent }

    private static async Task<Outcome> AskAsync(string host, IPAddress address, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(Step);
            await tcp.ConnectAsync(address, 443, connect.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome.NoTcp;
        }

        try
        {
            // Один срок на рукопожатие и первый байт ответа: здоровый узел укладывается в полсекунды.
            using var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            answer.CancelAfter(Step);
            using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, answer.Token);
            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET /generate_204 HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"), answer.Token);
            var head = new byte[12];
            int read = await tls.ReadAtLeastAsync(head, head.Length, false, answer.Token);
            return read >= 9 && System.Text.Encoding.ASCII.GetString(head, 0, read).StartsWith("HTTP/1.", StringComparison.Ordinal) ? Outcome.Answered : Outcome.Silent;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome.Silent;
        }
    }

    public static Dictionary<string, Entry> Load(string? path = null)
    {
        try
        {
            return new(JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path ?? StatePath)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(IReadOnlyDictionary<string, Entry> state, string? path = null)
    {
        var target = path ?? StatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        Zephyron.Core.AtomicFile.WriteAllText(target, JsonSerializer.Serialize(state));
    }

    /// <summary>Чьи отказы пора перепроверить, а чьи снять по возрасту.</summary>
    public static (IReadOnlyList<string> Recheck, IReadOnlyList<string> Expired) Due(IReadOnlyDictionary<string, Entry> state, DateTimeOffset now) =>
        (state.Where(p => now - p.Value.At < Age(p.Value) && now - p.Value.Checked >= Recheck).Select(p => p.Key).ToList(),
         state.Where(p => now - p.Value.At >= Age(p.Value)).Select(p => p.Key).ToList());

    private static TimeSpan Age(Entry entry) => entry.Window ? WindowMaxAge : MaxAge;

    /// <summary>Что сделал круг сторожа: узлы под малым окном, отключённые и возвращённые.</summary>
    public sealed record Round(IReadOnlyList<string> Refused, IReadOnlyList<string> Restored, IReadOnlyList<string> Windowed);

    /// <summary>
    /// Один круг: новые узлы из кэша DNS проверяются; узел под фильтром сначала получает малое
    /// окно (адрес — в список winws2), и только если не ожил и с ним — отказ в hosts. Старые
    /// записи перепроверяются: отключённый узел, ожив под окном, возвращается в строй.
    /// </summary>
    /// <param name="known">Адреса, проверенные в этом сеансе: живой узел не стоит мерить на каждом круге.</param>
    /// <param name="windowList">Список адресов для малого окна; <c>null</c> — окна нет (движки запущены без него).</param>
    public static async Task<Round> RunAsync(
        ISet<string> known,
        CancellationToken cancellationToken,
        string? hostsPath = null,
        string? statePath = null,
        string? windowList = null)
    {
        var now = DateTimeOffset.Now;
        var state = Load(statePath);
        var refused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var restored = new List<string>();
        var windowed = new List<string>();
        var unpin = new List<string>();
        bool windowChanged = false;

        // Узел, молчащий и на TCP, — тоже под фильтром (часть адресов не отвечает на SYN),
        // но только если сеть жива: иначе обрыв связи отключил бы все узлы разом.
        bool? up = null;

        // Адрес — в список окна, и тот же узел спрашивается ещё раз: winws2 перечитывает
        // список при следующем поиске профиля, проба идёт через него же.
        async Task<bool> RevivesAsync(string host, IPAddress address)
        {
            if (windowList is null)
                return false;

            known.Add("window:" + address);

            var listed = Zapret.VideoWindow.Read(windowList).ToHashSet(StringComparer.Ordinal);

            if (listed.Add(Network(address)))
            {
                Zapret.VideoWindow.Write(listed, windowList);
                windowChanged = true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return await IsFilteredAsync(host, address, cancellationToken) == false;
        }

        foreach (var (host, address) in Candidates())
        {
            if (state.ContainsKey(host) || !known.Add(address.ToString()))
                continue;

            // Одна быстрая попытка: плеер ждёт, а лишнее окно безвредно. Отказ в hosts —
            // только после повторной проверки без окна ниже, в RevivesAsync она не нужна.
            if (await IsFilteredAsync(host, address, cancellationToken, attempts: 1) is not false
                && (up ??= await ReachesAsync("www.youtube.com", cancellationToken)))
            {
                if (await RevivesAsync(host, address))
                {
                    windowed.Add(host);
                    state[host] = new Entry(address.ToString(), now, now, Window: true);
                }
                else if (await IsFilteredAsync(host, address, cancellationToken) is not false)
                {
                    refused[host] = address.ToString();
                    state[host] = new Entry(address.ToString(), now, now);
                }
            }
        }

        var (due, expired) = Due(state, now);
        restored.AddRange(expired);

        // Отключённый узел без попытки окна в этом сеансе (запись прежней версии или окна
        // не было) пробуется сразу, не дожидаясь получаса.
        var recheck = due.Concat(windowList is null ? [] : state
                .Where(p => !p.Value.Window && !expired.Contains(p.Key) && known.Add("window:" + p.Value.Address))
                .Select(p => p.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var host in recheck)
        {
            var entry = state[host];

            if (!IPAddress.TryParse(entry.Address, out var address))
            {
                restored.Add(host);
                continue;
            }

            if (entry.Window)
            {
                // Под окном узел ответил — оставляем; замолчал и с ним — отказ в hosts.
                if (await IsFilteredAsync(host, address, cancellationToken) == false)
                {
                    state[host] = entry with { Checked = now };
                }
                else
                {
                    refused[host] = entry.Address;
                    state[host] = entry with { Checked = now, Window = false };
                }
            }
            else if (await IsFilteredAsync(host, address, cancellationToken) == false)
            {
                restored.Add(host);
            }
            else if (await RevivesAsync(host, address))
            {
                unpin.Add(host);
                windowed.Add(host);
                state[host] = entry with { Checked = now, Window = true };
            }
            else
            {
                state[host] = entry with { Checked = now };
            }
        }

        // hosts — крайняя мера и не свалка: сверх MaxRefused новые отказы не пишутся.
        int room = MaxRefused - state.Values.Count(e => !e.Window) + refused.Count;

        foreach (var host in refused.Keys.Skip(Math.Max(room, 0)).ToList())
        {
            refused.Remove(host);
            state.Remove(host);
        }

        if (refused.Count > 0)
        {
            HostsEditor.Pin(
                refused.ToDictionary(p => p.Key, _ => Refused, StringComparer.OrdinalIgnoreCase),
                hostsPath,
                "узлы видео YouTube под фильтром — отказ, чтобы плеер сразу взял другой",
                refused.ToDictionary(p => p.Key, _ => Via, StringComparer.OrdinalIgnoreCase));
        }

        foreach (var host in restored)
            state.Remove(host);

        if (restored.Count + unpin.Count > 0)
            HostsEditor.Unpin([.. restored, .. unpin], hostsPath);

        // Список окна — ровно адреса записей под окном: снятые и устаревшие уходят из него.
        if (windowList is not null)
        {
            var wanted = state.Values.Where(e => e.Window)
                .Select(e => IPAddress.TryParse(e.Address, out var a) ? Network(a) : null)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            if (!wanted.SetEquals(Zapret.VideoWindow.Read(windowList)))
            {
                Zapret.VideoWindow.Write(wanted, windowList);
                windowChanged = true;
            }
        }

        if (refused.Count > 0 || restored.Count > 0 || windowed.Count > 0 || windowChanged)
            Save(state, statePath);

        if (refused.Count > 0 || restored.Count > 0 || unpin.Count > 0)
            HostsEditor.FlushDns();

        return new Round(refused.Keys.ToList(), restored, windowed);
    }

    private static async Task<bool> ReachesAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(Step);
            await tcp.ConnectAsync(host, 443, connect.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Снимает все наши отказы — выключателем или при выходе из режима.</summary>
    public static int Clear(string? hostsPath = null, string? statePath = null, string? windowList = null)
    {
        var ours = HostsEditor.PinsVia(hostsPath).Where(p => p.Value == Via).Select(p => p.Key).ToList();

        if (windowList is not null && File.Exists(windowList))
            Zapret.VideoWindow.Write([], windowList);

        if (ours.Count > 0)
        {
            HostsEditor.Unpin(ours, hostsPath);
            HostsEditor.FlushDns();
        }

        Save(new Dictionary<string, Entry>(), statePath);
        return ours.Count;
    }
}
