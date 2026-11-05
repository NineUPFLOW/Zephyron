using Zephyron.Core;
using Zephyron.Proxy;

namespace Zephyron.Gui;

/// <summary>Фоновый сторож: при работающих движках запускает плановую проверку пинов, когда подошёл срок.</summary>
internal static class PinRefreshWatch
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    private static bool _started;

    public static void Start()
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        bool wasRunning = false;

        while (true)
        {
            await Task.Delay(Tick).ConfigureAwait(false);

            bool running = EngineControl.IsRunning;
            bool ready = running && wasRunning;
            wasRunning = running;

            if (!ready || !AppSettings.Load(AppSettings.DefaultPath).AutoRepin || !PinRefresh.IsDue(DateTimeOffset.Now))
                continue;

            try
            {
                if (await PinRefresh.RunIfDueAsync(CancellationToken.None).ConfigureAwait(false) is { } result)
                    Write("сама", result);
            }
            catch (Exception ex)
            {
                Journal.Write("пин", "плановая проверка пинов не удалась: " + ex.GetBaseException().Message);
            }
        }
    }

    internal static void Write(string how, PinRefreshResult result)
    {
        if (result.Changes.Count > 0)
            Journal.Write("пин", $"пин заменён, прежний адрес молчал ({how}): " + string.Join("; ", result.Changes)
                + (result.Backup is null ? string.Empty : $"; копия hosts: {result.Backup}"));

        if (result.Moved.Count > 0)
            Journal.Write("пин", $"пин перенесён за посредником ({how}): " + string.Join("; ", result.Moved));

        if (result.Reordered.Count > 0)
            Journal.Write("пин", $"живой адрес поставлен первым ({how}): " + string.Join("; ", result.Reordered));

        if (result.Dead.Count > 0)
            Journal.Write("пин", $"без замены остались ({how}): {string.Join(", ", result.Dead.Take(8))}"
                + (result.Dead.Count > 8 ? $" и ещё {result.Dead.Count - 8}" : string.Empty));

        if (result.Silent.Count > 0)
            Journal.Write("пин", $"посредники не ответили по DoH ({how}): {string.Join(", ", result.Silent)}");

        if (result.Error is { } error)
            Journal.Write("пин", $"проверка пинов ({how}): {error}");

        if (result.Changes.Count == 0 && result.Moved.Count == 0 && result.Reordered.Count == 0
            && result.Dead.Count == 0 && result.Error is null)
        {
            Journal.Write("пин", $"проверка пинов ({how}): просмотрено {result.Checked}, менять нечего"
                + (result.Suspects > 0 ? $"; промолчали {result.Suspects}, при повторном молчании будут заменены" : string.Empty));
        }
    }
}
