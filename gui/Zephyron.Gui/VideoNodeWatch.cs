using System.Windows.Threading;
using Zephyron.Proxy;

namespace Zephyron.Gui;

/// <summary>
/// Сторож узлов видео YouTube: раз в 3 с смотрит, к каким узлам ходил плеер,
/// и отключает те, что фильтр режет по адресу (<see cref="VideoNodeGuard"/>).
/// </summary>
/// <remarks>
/// Только пока работают движки и включён выключатель: без движков пишем в hosts
/// не мы, а человек. Выключатель выключили — свои отказы снимаем сразу.
/// </remarks>
internal static class VideoNodeWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    /// <summary>Адреса, уже проверенные в этом сеансе: живой узел не мерим на каждом круге.</summary>
    private static readonly HashSet<string> Known = [];

    private static bool _started;
    private static bool _wasOn = true;
    private static string? _lastFailure;

    public static void Start(Dispatcher ui)
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(RunAsync);
        _ = Task.Run(HangLoopAsync);
    }

    /// <summary>Узлы, замолчавшие на запросы плеера (<see cref="HangTracker"/>), — их отключает основной круг.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string Host, System.Net.IPAddress Address)> Hung = new();

    private static volatile bool _on;

    private static readonly TimeSpan RunningEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Раз в четверть секунды — байты соединений браузера к узлам видео. Таблица TCP и счётчики
    /// только к этим адресам: пакеты не трогаются, нагрузка — опрос таблицы.
    /// </summary>
    private static async Task HangLoopAsync()
    {
        var tracker = new HangTracker();
        var enabled = new HashSet<HangTracker.Key>();
        var hosts = new Dictionary<uint, string>();
        var addresses = new Dictionary<string, System.Net.IPAddress>(StringComparer.OrdinalIgnoreCase);
        var refreshed = DateTime.MinValue;
        int? supervisor = null;
        bool running = false;
        int? holder = null;
        var runningAt = DateTime.MinValue;

        while (true)
        {
            await Task.Delay(250).ConfigureAwait(false);

            try
            {
                // Жив ли надзор — раз в две секунды, а не на каждом круге: проверка читает файлы
                // состояния и замка и перебирает процессы системы (замер 26.11: главный источник
                // выделений окна в покое — четыре раза в секунду).
                if (DateTime.UtcNow - runningAt >= RunningEvery)
                {
                    runningAt = DateTime.UtcNow;
                    running = EngineControl.IsRunning;
                    holder = Zephyron.Supervisor.SupervisorLock.Holder();
                }

                // Движки перезапустились — счёт заново, с прогревом (HangTracker.WarmUp).
                if (!_on || !running || holder != supervisor)
                {
                    supervisor = holder;
                    tracker = new HangTracker();
                    enabled.Clear();
                    continue;
                }

                var now = DateTime.UtcNow;

                if (now - refreshed >= Every)
                {
                    refreshed = now;
                    hosts.Clear();
                    addresses.Clear();

                    foreach (var (host, address) in VideoNodeGuard.Candidates())
                    {
                        hosts[TcpBytes.Raw(address)] = host;
                        addresses[host] = address;
                    }
                }

                foreach (var host in tracker.Observe(TcpBytes.Read(hosts, enabled), now))
                {
                    if (addresses.TryGetValue(host, out var address))
                        Hung.Enqueue((host, address));
                }
            }
            catch (Exception)
            {
                // Таблица TCP недоступна (нет прав) — без этого сигнала сторож работает как прежде.
            }
        }
    }

    /// <summary>Сколько ждать после запуска движков, прежде чем судить узлы.</summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(90);

    private static int? _judgedSupervisor;
    private static bool _released;

    /// <summary>Снимает отказы узлов из hosts — при остановке движков и выходе из программы.</summary>
    public static void Release(string why)
    {
        // Снимать нечего, пока сторож с прошлого снятия ничего не отключал: без этого
        // при стоящих движках hosts и файл состояния переписывались бы каждые три секунды.
        if (_released)
            return;

        try
        {
            if (VideoNodeGuard.Clear() is var cleared and > 0)
                Journal.Write("видео YouTube", $"{why} — сняты отказы узлов: {cleared}");

            _released = true;
        }
        catch (Exception)
        {
            // hosts занят или недоступен — снимет следующий круг сторожа.
        }
    }
    private static DateTime _upSince = DateTime.UtcNow;

    private static async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(Every).ConfigureAwait(false);

            try
            {
                var settings = Core.AppSettings.Load(Core.AppSettings.DefaultPath);
                bool on = settings.GuardVideoNodes;

                if (!on)
                {
                    _on = false;
                    if (_wasOn && VideoNodeGuard.Clear() is var cleared and > 0)
                        Journal.Write("видео YouTube", $"сторож выключен — сняты отказы узлов: {cleared}");

                    _wasOn = false;
                    continue;
                }

                _wasOn = true;
                _on = true;

                if (!EngineControl.IsRunning)
                {
                    // Отказы живут в hosts и без программы: движки встали — узлы вернуть сразу,
                    // иначе YouTube оставался отрезан от них, пока окно снова не поднимет сторож.
                    Release("движки остановлены");
                    continue;
                }

                // Судить узлы можно только при поднятом и прогретом десинке. Сразу после запуска
                // (а в «Туннеле» — всегда) узел не отвечает не по своей вине, и сторож отключал
                // живые узлы на полчаса через hosts: YouTube «вообще не открывался» (замер 27.11).
                // Надзор — по замку, а не по своему запуску: движки поднимают и автозапуск, и --start.
                var holder = Zephyron.Supervisor.SupervisorLock.Holder();

                if (!settings.Engines.Desync || holder != _judgedSupervisor)
                {
                    if (holder != _judgedSupervisor)
                    {
                        _judgedSupervisor = holder;
                        _upSince = DateTime.UtcNow;
                    }

                    if (!settings.Engines.Desync)
                    {
                        while (Hung.TryDequeue(out _)) { }
                        Release("десинк выключен");
                        continue;
                    }
                }

                if (DateTime.UtcNow - _upSince < WarmUp)
                {
                    while (Hung.TryDequeue(out _)) { }
                    continue;
                }

                _released = false;

                var hung = new List<(string, System.Net.IPAddress)>();

                while (Hung.TryDequeue(out var node))
                    hung.Add(node);

                if (hung.Count > 0 && VideoNodeGuard.RefuseHung(hung) is { Count: > 0 } refusedHung)
                    Journal.Write("видео YouTube", $"узлы молчат на запросы плеера — отключены на {VideoNodeGuard.HangHold.TotalMinutes:0} мин, плеер возьмёт другие: {string.Join(", ", refusedHung)}");

                var round = await VideoNodeGuard.RunAsync(Known, CancellationToken.None).ConfigureAwait(false);

                if (round.Refused.Count > 0)
                    Journal.Write("видео YouTube", $"узлы не ожили и под малым окном — отключены, плеер возьмёт другие: {string.Join(", ", round.Refused)}");

                if (round.Restored.Count > 0)
                    Journal.Write("видео YouTube", $"узлы снова отвечают или отказ устарел — возвращены: {string.Join(", ", round.Restored)}");

                _lastFailure = null;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;

                if (message != _lastFailure)
                    Journal.Write("видео YouTube", "сторож узлов не смог проверить или записать: " + message);

                _lastFailure = message;
            }
        }
    }
}
