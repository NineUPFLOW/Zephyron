using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Zapret;

namespace Zephyron.Gui;

/// <summary>
/// Сторож YouTube: перестал открываться — следующий способ обхода и перезапуск движков.
/// </summary>
/// <remarks>
/// <para>
/// Только в нашем пресете (<see cref="YouTubeRecipes.Applies"/>) и только когда YouTube
/// идёт десинком: через VPN рецепт ни при чём. Сначала проверяется, есть ли сеть вообще
/// (ya.ru): без неё молчит всё, и менять способ не за что.
/// </para>
/// <para>
/// Отказ — когда страница, миниатюры или видеосервер не открылись два захода подряд. Между сменами
/// не меньше трёх минут, и за полчаса — не больше полного круга способов: если не помог
/// ни один, крутить дальше бессмысленно, а каждый перезапуск рвёт соединения.
/// </para>
/// </remarks>
internal static class YouTubeWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    private static readonly string[] Probes = ["www.youtube.com", "i.ytimg.com", "redirector.googlevideo.com"];

    private static bool _started;
    private static int _streak;
    private static bool _switched;
    private static DateTime _lastSwitch = DateTime.MinValue;
    private static readonly Queue<DateTime> Switches = new();

    public static void Start()
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        // Движкам — время подняться после запуска окна.
        await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);

        while (true)
        {
            try
            {
                await RoundAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Journal.Write("YouTube", "сторож YouTube не смог проверить: " + ex.GetBaseException().Message);
            }

            await Task.Delay(Every).ConfigureAwait(false);
        }
    }

    private static async Task RoundAsync()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (!EngineControl.IsRunning || !YouTubeRecipes.Applies(settings.PresetName) || !settings.NeedsDesync
            || settings.Mode is OperatingMode.ProxyAll or OperatingMode.ProxyStrict || ThroughVpn())
        {
            _streak = 0;
            return;
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var control = await BlockCheck.CheckAsync("ya.ru", null, cancel.Token).ConfigureAwait(false);

        if (control.Kind != BlockKind.None)
        {
            _streak = 0;
            return;
        }

        var results = await Task.WhenAll(Probes.Select(h => BlockCheck.CheckAsync(h, "YouTube", cancel.Token))).ConfigureAwait(false);

        // Страница открылась, а видео нет — для человека YouTube тоже не работает.
        if (results.All(r => r.Kind == BlockKind.None))
        {
            if (_switched)
                Journal.Write("YouTube", $"YouTube снова открывается — способ обхода {YouTubeRecipes.Read() + 1}.");

            _streak = 0;
            _switched = false;
            return;
        }

        _streak++;

        if (!Due(_streak, DateTime.UtcNow, _lastSwitch, Switches))
            return;

        int now = YouTubeRecipes.Read();
        int next = YouTubeRecipes.Next(now);
        YouTubeRecipes.Write(next);

        _lastSwitch = DateTime.UtcNow;
        Switches.Enqueue(_lastSwitch);
        _streak = 0;
        _switched = true;

        Journal.Write("YouTube", $"YouTube перестал открываться ({string.Join("; ", results.Select(r => r.Describe()))}) — "
            + $"пробую другой способ обхода: {next + 1} из {YouTubeRecipes.Recipes.Count}.");

        await EngineControl.RestartAsync("YouTube: смена способа обхода", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Пора ли менять способ: два отказа подряд, пауза после прошлой смены и не больше круга за полчаса.</summary>
    internal static bool Due(int streak, DateTime now, DateTime lastSwitch, Queue<DateTime> switches)
    {
        while (switches.Count > 0 && now - switches.Peek() > Window)
            switches.Dequeue();

        return streak >= 2 && now - lastSwitch >= Gap && switches.Count < YouTubeRecipes.Recipes.Count;
    }

    /// <summary>Свой маршрут YouTube — через VPN: тогда способ десинка ни при чём.</summary>
    private static bool ThroughVpn()
    {
        try
        {
            return UserRulesFile.Load().Entries.Any(e => e.Enabled && !e.Off && e.Mode == RoutingMode.Proxy
                && e.Value.Replace('\\', '/').EndsWith("lists/youtube.txt", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
