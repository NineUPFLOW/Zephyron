using System.Net;
using System.Net.Http;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;

namespace Zephyron.Gui;

/// <summary>
/// Сторож сайтов на Cloudflare: часть адресов сайта закрыта — имя прибивается к открытому
/// (под десинком); не открывается ни один — следующий рецепт и перезапуск движков.
/// </summary>
/// <remarks>
/// <para>
/// 18.11 провайдер владельца менял блокировки Cloudflare поминутно: рецепт, открывший
/// nexusmods.com, через несколько минут переставал. Разовый подбор такого не переживает —
/// нужен тот же сторож, что у YouTube (<see cref="YouTubeWatch"/>), только для своих
/// маршрутов «десинк» на адресах Cloudflare.
/// </para>
/// <para>
/// Проверяется приветствием как у Chrome до полного ответа сервера (<see cref="BrowserHello.FlightAsync"/>):
/// короткое системное проходило там, где браузер рвался, а одно ServerHello приходило и там,
/// где сертификат за ним уже не пускали. Оператор закрывает адреса Cloudflare выборочно —
/// поэтому сначала закрепление за открытым адресом, и только потом смена рецепта. Сначала — есть ли сеть вообще (ya.ru). Смена — после
/// двух отказов подряд, не чаще раза в три минуты и не больше круга рецептов за полчаса:
/// каждый перезапуск рвёт соединения.
/// </para>
/// </remarks>
internal static class CloudflareWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>Рецепты каталога по очереди — от найденного против обрыва длинного приветствия.</summary>
    internal static readonly IReadOnlyList<string> Rotation =
    [
        "white-ya-multisplit",
        "fake-fakedsplit-badseq",
        "seqovl-4pda",
        "fake-multidisorder",
        "multidisorder",
        "tls-multisplit-sni",
        "hostfakesplit-multi",
    ];

    private static bool _started;
    private static DateTime _lastSwitch = DateTime.MinValue;
    private static readonly Queue<DateTime> Switches = new();
    private static readonly Dictionary<string, int> Streaks = new(StringComparer.OrdinalIgnoreCase);

    public static void Start()
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);

        while (true)
        {
            try
            {
                await RoundAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Journal.Write("Cloudflare", "сторож сайтов на Cloudflare не смог проверить: " + ex.GetBaseException().Message);
            }

            await Task.Delay(Every).ConfigureAwait(false);
        }
    }

    private static async Task RoundAsync()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (!EngineControl.IsRunning || !settings.NeedsDesync)
            return;

        var file = UserRulesFile.Load();
        var watched = file.Entries
            .Select((entry, index) => (entry, index))
            .Where(x => x.entry is { Enabled: true, Off: false, Mode: RoutingMode.Desync, Match: MatchKind.Domain })
            .ToList();

        if (watched.Count == 0)
            return;

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        if ((await BlockCheck.CheckAsync("ya.ru", null, cancel.Token).ConfigureAwait(false)).Kind != BlockKind.None)
            return;

        var changed = new List<string>();
        var pinned = new List<string>();
        using var doh = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        var pins = HostsEditor.Pins();
        var desyncPins = HostsEditor.DesyncPins();

        foreach (var (entry, index) in watched)
        {
            var host = entry.Value.TrimStart('*', '.');
            bool opens = true, cloudflare = false;

            foreach (var name in Names(entry.Value))
            {
                // DoH, а не система: прибитое имя система отдаёт одним нашим адресом.
                var all = (await DohResolver.ResolveAllAsync(name, doh, cancel.Token).ConfigureAwait(false))
                    .Where(a => CloudflareNet.Contains(a))
                    .Take(4)
                    .ToList();

                // Не на Cloudflare — не наше дело: у других сетей свои беды и свой подбор.
                if (all.Count == 0)
                    continue;

                cloudflare = true;
                var flights = await Task.WhenAll(all.Select(a => FullAsync(a, name, cancel.Token))).ConfigureAwait(false);
                var working = all.Where((_, i) => flights[i]).ToList();

                IPAddress? pin = desyncPins.Contains(name) && pins.TryGetValue(name, out var text) && IPAddress.TryParse(text, out var ip) ? ip : null;
                bool pinWorks = pin is not null && (all.Contains(pin) ? working.Contains(pin) : await FullAsync(pin, name, cancel.Token).ConfigureAwait(false));

                switch (Decide(all.Count, working, pin, pinWorks))
                {
                    case { Unpin: true }:
                        HostsEditor.Unpin([name]);
                        pinned.Add($"{name} — все адреса снова открываются, закрепление снято");
                        break;
                    case { PinTo: { } to }:
                        HostsEditor.PinWithDesync(new Dictionary<string, IReadOnlyList<string>> { [name] = [to.ToString()] }, note: "Zephyron — Cloudflare");
                        pinned.Add($"{name} → {to} (открываются {working.Count} из {all.Count} адресов)");
                        break;
                    case { Opens: false }:
                        opens = false;
                        break;
                }
            }

            if (!cloudflare || opens)
            {
                Streaks.Remove(host);
                continue;
            }

            Streaks[host] = Streaks.GetValueOrDefault(host) + 1;

            if (!Due(Streaks[host], DateTime.UtcNow, _lastSwitch, Switches))
                continue;

            var next = Next(entry.Recipe);
            file.ReplaceAt(index, entry with { Recipe = next });
            Streaks.Remove(host);
            changed.Add($"{host} → «{next}»");
        }

        if (pinned.Count > 0)
        {
            HostsEditor.FlushDns();
            Journal.Write("Cloudflare", "часть адресов сайта закрыта — закрепляю за открытым: " + string.Join("; ", pinned) + ".");
        }

        if (changed.Count == 0)
            return;

        file.Save();
        _lastSwitch = DateTime.UtcNow;
        Switches.Enqueue(_lastSwitch);

        Journal.Write("Cloudflare", "сайт не открывается ни на одном адресе — пробую другой способ обхода: " + string.Join("; ", changed) + ".");

        await EngineControl.RestartAsync("Cloudflare: смена способа обхода", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Имена правила: «*.site» — и сам сайт, и www.</summary>
    internal static IReadOnlyList<string> Names(string value)
    {
        var host = value.TrimStart('*', '.').ToLowerInvariant();
        return value.StartsWith('*') && !host.StartsWith("www.", StringComparison.Ordinal) ? [host, "www." + host] : [host];
    }

    /// <summary>Пришёл ли полный ответ сервера, а не одно ServerHello.</summary>
    private static async Task<bool> FullAsync(IPAddress address, string name, CancellationToken cancellationToken) =>
        await BrowserHello.FlightAsync(address, name, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false) >= 2048;

    /// <summary>Что делать с именем: снять закрепление, прибить к открытому адресу или признать, что не открывается.</summary>
    internal static (bool Opens, bool Unpin, IPAddress? PinTo) Decide(int total, IReadOnlyList<IPAddress> working, IPAddress? pin, bool pinWorks)
    {
        if (working.Count == total)
            return (true, pin is not null, null);

        if (pinWorks)
            return (true, false, null);

        return working.Count > 0 ? (true, false, working[0]) : (false, false, null);
    }

    /// <summary>Следующий рецепт круга; чужой или пустой — первый.</summary>
    internal static string Next(string? current)
    {
        int at = Rotation.ToList().FindIndex(r => string.Equals(r, current, StringComparison.OrdinalIgnoreCase));
        return Rotation[(at + 1) % Rotation.Count];
    }

    /// <summary>Пора ли менять: два отказа подряд, пауза после прошлой смены и не больше круга за полчаса.</summary>
    internal static bool Due(int streak, DateTime now, DateTime lastSwitch, Queue<DateTime> switches)
    {
        while (switches.Count > 0 && now - switches.Peek() > Window)
            switches.Dequeue();

        return streak >= 2 && now - lastSwitch >= Gap && switches.Count < Rotation.Count;
    }
}
