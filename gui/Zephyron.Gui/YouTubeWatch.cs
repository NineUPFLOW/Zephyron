using Zephyron.Core;
using Zephyron.Core.Diagnostics;
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
    private static string? _videoNode;
    private static bool _nodeRefreshed;
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

        // Свой видеосервер человека — один раз за сторожа: узел меняется редко, а redirector
        // отвечает и тогда, когда режут само видео.
        _videoNode ??= await GoogleVideoNode.FindAsync(cancel.Token).ConfigureAwait(false);

        // Узел, которому отказал наш же сторож узлов (0.0.0.0 в hosts), закрыт нами, а не провайдером:
        // плеер берёт другие. 22.11 у владельца на Adaptive такой узел четыре раза подряд
        // засчитывался отказом YouTube, и сторож сменил рабочий способ на три нерабочих.
        string[] probes = _videoNode is { } node && !VideoNodeGuard.Load().ContainsKey(node) ? [.. Probes, node] : Probes;

        var results = await Task.WhenAll(probes.Select(h => BlockCheck.CheckAsync(h, "YouTube", cancel.Token))).ConfigureAwait(false);

        // Страница открылась, а видео нет — для человека YouTube тоже не работает.
        if (results.All(r => r.Kind == BlockKind.None))
        {
            if (_switched)
                Journal.Write("YouTube", $"YouTube снова открывается — способ обхода {YouTubeRecipes.Read() + 1}.");

            _streak = 0;
            _switched = false;
            _nodeRefreshed = false;
            return;
        }

        // Не открылся один узел, а страница и картинки — да: узел мог смениться или уйти под отказ.
        // Сначала узнаём свой узел заново, и засчитывается только отказ и нового.
        if (results.Take(Probes.Length).All(r => r.Kind == BlockKind.None) && !_nodeRefreshed)
        {
            _videoNode = null;
            _nodeRefreshed = true;
            return;
        }

        _nodeRefreshed = false;
        _streak++;

        if (!Due(_streak, DateTime.UtcNow, _lastSwitch, Switches))
        {
            if (Exhausted(_streak, DateTime.UtcNow, _lastSwitch, Switches) && ZapretPaths.FindPreset(YouTubeRecipes.FallbackPreset) is not null)
                await ToStrongAsync(settings, results).ConfigureAwait(false);

            return;
        }

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

    /// <summary>
    /// Круг рецептов пройден, ни один не помог: провайдер строже, чем рассчитан Adaptive.
    /// </summary>
    internal static bool Exhausted(int streak, DateTime now, DateTime lastSwitch, Queue<DateTime> switches) =>
        streak >= 2 && now - lastSwitch >= Gap && switches.Count >= YouTubeRecipes.Recipes.Count;

    /// <summary>
    /// Один раз за сторожа — на Zephyron Strict. Дальше сторож молчит (Applies у Strict ложно),
    /// так что качелей между пресетами нет; вернуть Adaptive можно на «Десинке».
    /// </summary>
    private static async Task ToStrongAsync(AppSettings settings, TargetReport[] results)
    {
        (settings with { PresetName = YouTubeRecipes.FallbackPreset }).Save(AppSettings.DefaultPath);
        _streak = 0;

        Journal.Write("YouTube", $"Ни один способ обхода в Zephyron Adaptive не открыл YouTube ({string.Join("; ", results.Select(r => r.Describe()))}) — "
            + $"включаю пресет {YouTubeRecipes.FallbackPreset} для строгих провайдеров. Вернуть прежний можно на вкладке «Десинк».");

        await EngineControl.RestartAsync("YouTube: пресет для строгих провайдеров", CancellationToken.None).ConfigureAwait(false);
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
