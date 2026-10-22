using System.Windows;
using System.Windows.Controls;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>
/// Счётчики движков и меню у «Остановить» на «Главной».
/// </summary>
public partial class StatusView
{
    /// <summary>Нагрузка движков между замерами: доля ядра считается по разнице, поэтому замер один на окно.</summary>
    private static readonly EngineLoad Load = new();

    private bool _readingMeters;

    /// <summary>
    /// Строка под кнопкой: процессор и память движков, трафик движка туннеля с запуска.
    /// </summary>
    /// <remarks>
    /// В пуле: замер процессов и запрос к Clash API — ожидания, а поток окна
    /// на «Главной» уже ловили на ожиданиях (сторож подвисаний, 01.10).
    /// </remarks>
    private async Task ReadMetersAsync(SupervisorState? state, bool running, bool tunnel)
    {
        if (!running)
        {
            EngineMeters.Visibility = Visibility.Collapsed;
            return;
        }

        if (_readingMeters)
            return;

        _readingMeters = true;

        try
        {
            var (engines, totals) = await Task.Run(async () =>
            {
                var used = Load.Sample(state);
                (long Up, long Down)? moved = null;

                if (tunnel)
                {
                    using var api = new ClashApi();
                    moved = await api.TotalsAsync(CancellationToken.None);
                }

                return (used, moved);
            });

            var size = Zephyron.Subscriptions.SubscriptionUsage.Size;
            var parts = new List<string>
            {
                "ЦП " + EngineLoad.Cores(engines),
                "ПАМЯТЬ " + (engines.Count > 0 ? size(engines.Sum(e => e.Memory)) : "—"),
            };

            // Трафик — только у туннеля: у десинка своего счёта нет.
            if (totals is { } moved)
                parts.Add($"ТУННЕЛЬ ↑ {size(moved.Up)} ↓ {size(moved.Down)}");

            EngineMeters.Text = string.Join("  ·  ", parts);
            EngineMeters.ToolTip = EngineLoad.Explain(engines, size)
                + (totals is null ? string.Empty : "\n\nТрафик — через движок туннеля с его запуска.");
            EngineMeters.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // Счётчики — показ, а не работа: не прочиталось — покажем в следующий раз.
        }
        finally
        {
            _readingMeters = false;
        }
    }

    /// <summary>
    /// Меню у «Остановить»: перезапуск и оба выхода, как в трее.
    /// </summary>
    private void OnEngineMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = EngineMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        var restart = new MenuItem { Header = "Перезапустить движки", IsEnabled = EngineControl.IsRunning };
        restart.Click += async (_, _) =>
        {
            BeginStarting();

            var outcome = await EngineControl.RestartAsync("меню у «Остановить» на «Главной»", CancellationToken.None);

            if (!outcome.Ok)
            {
                EndStarting();
                ShowProblem(outcome.Message);
                Update();
            }
        };
        menu.Items.Add(restart);

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        // «Убрать окно» и «выключить обход» — разные желания, и ошибка
        // в любую сторону дорогая: оба выхода названы прямо.
        var keep = new MenuItem { Header = "Выйти, движки остаются работать" };
        keep.Click += (_, _) => TrayIcon.Exit(stopEngines: false);
        menu.Items.Add(keep);

        var stop = new MenuItem { Header = "Выйти и остановить движки" };
        stop.SetResourceReference(ForegroundProperty, "Danger");
        stop.Click += (_, _) => TrayIcon.Exit(stopEngines: true);
        menu.Items.Add(stop);

        menu.IsOpen = true;
    }
}
