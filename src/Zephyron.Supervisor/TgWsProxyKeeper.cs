using Zephyron.Core;

namespace Zephyron.Supervisor;

/// <summary>
/// Прокси Telegram без движков: его держит сам процесс окна (<see cref="AppSettings.TelegramProxyAlways"/>).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 12.10: прокси должен жить и при остановленных движках. Поднимается
/// тем же <see cref="TgWsProxyService"/> под своим <see cref="ProcessSupervisor"/>:
/// тот же перезапуск при падении, тот же журнал <c>runtime\tg-ws-proxy.log</c>,
/// а объект задания уносит прокси вместе с окном. Файл состояния — свой:
/// общий говорил бы «Главной», что движки запущены.
/// </para>
/// <para>
/// С 26.11 окно держит прокси лишь без движков: с движками его держит их надзор
/// (<see cref="EnginesHoldIt"/>) — иначе «выйти, движки оставить» уносил прокси
/// вместе с окном. Порт один: окно зовёт <see cref="Sync"/> снова после каждого
/// запуска и остановки движков.
/// </para>
/// </remarks>
public static class TgWsProxyKeeper
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _stop;
    private static Task? _running;

    public static string StatePath => Path.Combine("runtime", "tg-ws-proxy-state.json");

    /// <summary>Держит ли окно прокси прямо сейчас.</summary>
    public static bool Running
    {
        get
        {
            lock (Gate)
                return _running is { IsCompleted: false };
        }
    }

    /// <summary>Держит ли прокси надзор движков — тогда окну свой не поднимать.</summary>
    /// <remarks>
    /// С 26.11 надзор берёт прокси при любом включённом (SupervisorHost.BuildArguments):
    /// прокси окна жил в его объекте задания и умирал с «выйти, движки оставить».
    /// Файл состояния надзор пишет, лишь подняв все службы (прокси — последним),
    /// и пока движки поднимаются или файла ещё нет, верим настройке: окно заняло
    /// бы порт раньше надзора, а тот при старте снимает чужие tg-ws-proxy (FindOrphans).
    /// </remarks>
    /// <param name="starting">Окно прямо сейчас поднимает движки.</param>
    /// <param name="enginesUp">Надзор движков жив (замок, файл состояния или свой PID).</param>
    public static bool EnginesHoldIt(AppSettings settings, bool starting, bool enginesUp, SupervisorState? state)
    {
        if (starting)
            return settings.TelegramProxy;

        if (!enginesUp)
            return false;

        // Надзор, поднятый прежней версией при «всегда», прокси не держит — тогда окно.
        return state is not null && state.IsSupervisorAlive()
            ? state.Services.Any(s => s.Name == TgWsProxyService.ServiceName)
            : settings.TelegramProxy;
    }

    /// <summary>Поднимает или гасит прокси по настройкам.</summary>
    /// <param name="enginesHoldIt">Надзор движков сам держит прокси — тогда ждём его ухода.</param>
    public static void Sync(AppSettings settings, bool enginesHoldIt)
    {
        bool wanted = settings.TelegramProxy
            && settings.TelegramProxyAlways
            && TgWsProxy.IsSecret(settings.TelegramProxySecret)
            && !enginesHoldIt;

        if (wanted)
            Start(settings);
        else
            Stop();
    }

    private static void Start(AppSettings settings)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false })
                return;

            var service = new TgWsProxyService(
                TgWsProxy.Executable(),
                settings.TelegramProxyPort,
                settings.TelegramProxySecret!,
                Path.GetFullPath("."),
                TgWsProxyRoutes.From(settings))
            {
                OutputLogPath = Path.Combine("runtime", "tg-ws-proxy.log"),
                Routes = ct => Zephyron.Proxy.TgProxyTune.RoutesAsync(AppSettings.Load(AppSettings.DefaultPath), ct),
            };

            if (service.ValidatePrerequisites() is not null)
                return;

            var supervisor = new ProcessSupervisor([service], new SupervisorOptions { StatePath = StatePath });
            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            _running = Task.Run(() => supervisor.RunAsync(token));
        }
    }

    /// <summary>
    /// Поднимает прокси окна заново — с ключами из нынешних настроек.
    /// </summary>
    /// <remarks>
    /// Ключи прокси читает только при запуске: путь к Telegram, сменённый на
    /// вкладке, без перезапуска не действует. Не держит окно прокси — ничего
    /// не делает: у движков свой, он меняется с их перезапуском.
    /// </remarks>
    public static void Restart(AppSettings settings, bool enginesHoldIt)
    {
        if (!Running)
            return;

        Stop();

        Task? slow;

        lock (Gate)
        {
            slow = _running;
        }

        // Прежний не успел уйти за 10 с — поднимем новый, когда уйдёт, а не оставим без прокси.
        if (slow is not null)
        {
            slow.ContinueWith(_ => { Stop(); Sync(settings, enginesHoldIt); }, TaskScheduler.Default);
            return;
        }

        Sync(settings, enginesHoldIt);
    }

    /// <summary>Гасит прокси окна и ждёт, пока он уйдёт (не дольше 10 с).</summary>
    public static void Stop()
    {
        Task? running;
        CancellationTokenSource? stop;

        lock (Gate)
        {
            _stop?.Cancel();
            running = _running;
            stop = _stop;
        }

        if (running is null)
            return;

        try
        {
            running.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Упал сам — гасить уже нечего.
        }

        // Не успел уйти — ссылку держим: Start увидит живой прокси и не займёт порт вторым.
        lock (Gate)
        {
            if (!running.IsCompleted || !ReferenceEquals(_running, running))
                return;

            _running = null;
            _stop = null;
            stop?.Dispose();
        }
    }
}
