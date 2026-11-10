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
    }

    private static async Task RunAsync()
    {
        while (true)
        {
            await Task.Delay(Every).ConfigureAwait(false);

            try
            {
                bool on = Core.AppSettings.Load(Core.AppSettings.DefaultPath).GuardVideoNodes;

                if (!on)
                {
                    if (_wasOn && VideoNodeGuard.Clear() is var cleared and > 0)
                        Journal.Write("видео YouTube", $"сторож выключен — сняты отказы узлов: {cleared}");

                    _wasOn = false;
                    continue;
                }

                _wasOn = true;

                if (!EngineControl.IsRunning)
                    continue;

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
