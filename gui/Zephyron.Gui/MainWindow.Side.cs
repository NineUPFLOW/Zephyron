using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Gui.Views;

namespace Zephyron.Gui;

/// <summary>
/// Боковое меню (макет 23.11): кнопка обхода, состояние разделов справа от пунктов, подвал.
/// </summary>
public partial class MainWindow
{
    /// <summary>Идёт запуск или остановка с кнопки обхода — второй щелчок ничего не делает.</summary>
    private bool _powerBusy;

    /// <summary>Своих правил маршрутов — читается не на каждом тике, а раз в полминуты.</summary>
    private int _routeCount = -1;
    private DateTime _routeCountAt = DateTime.MinValue;

    /// <summary>
    /// Обход одной кнопкой — тем же переключением, что Ctrl+Enter: на «Обзоре», где видно
    /// ход запуска и причину отказа. Своя кнопка «Включить» на «Обзоре» снята — их было две.
    /// </summary>
    private void OnPower(object sender, RoutedEventArgs e)
    {
        if (_powerBusy)
            return;

        Open("status");
        Dispatcher.InvokeAsync(() => (Section.Content as StatusView)?.Toggle(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Рядом с кнопкой обхода: перезапуск и режим — те же, что в трее.</summary>
    private void OnPowerMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        var restart = new MenuItem { Header = "Перезапустить движки", IsEnabled = EngineControl.IsRunning && !_powerBusy };
        restart.Click += async (_, _) => await RunPower(() => EngineControl.RestartAsync("меню у кнопки обхода", CancellationToken.None));
        menu.Items.Add(restart);
        menu.Items.Add(new Separator());

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var current = WorkModes.Of(settings.Engines);

        foreach (var mode in new[] { WorkMode.Desync, WorkMode.Tunnel, WorkMode.Route })
        {
            var item = new MenuItem
            {
                Header = "Режим «" + WorkModes.Name(mode) + "»",
                IsCheckable = true,
                IsChecked = mode == current,
                IsEnabled = WorkModes.CanChoose(settings, mode) && !_powerBusy,
            };

            item.Click += async (_, _) =>
            {
                if (mode == current)
                    return;

                WorkModes.Choose(AppSettings.Load(AppSettings.DefaultPath), mode).Save(AppSettings.DefaultPath);

                var who = $"меню у кнопки обхода: режим «{WorkModes.Name(mode)}»";
                await RunPower(() => EngineControl.IsRunning
                    ? EngineControl.RestartAsync(who, CancellationToken.None)
                    : EngineControl.StartAsync(who, CancellationToken.None));
            };

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private async Task RunPower(Func<Task<EngineOutcome>> action)
    {
        if (_powerBusy)
            return;

        _powerBusy = true;
        PowerButton.IsEnabled = false;
        PowerLabel.Text = "Перезапуск…";

        try
        {
            if (await action() is { Ok: false } outcome)
                Journal.Write("движки", "не запустились: " + outcome.Message);
        }
        catch (Exception ex)
        {
            Journal.Write("движки", "не запустились: " + ex.GetBaseException().Message);
        }
        finally
        {
            _powerBusy = false;
            PowerButton.IsEnabled = true;
            UpdateSideValues(force: true);
        }
    }

    private void OnSupport(object sender, RoutedEventArgs e) => DonateMenu.Show(sender);

    private void OnTelegram(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(About.Telegram) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет обозревателя по умолчанию — ссылка видна в «Настройках».
        }
    }

    /// <summary>«Нашли баг?» — в «Диагностику»: там проверка и отчёт для разбора.</summary>
    private void OnBug(object sender, RoutedEventArgs e) => Open("doctor");

    /// <summary>
    /// Состояние разделов справа от пунктов меню и слово на кнопке обхода.
    /// </summary>
    /// <remarks>Вызывается с тиком состояния движков (ShowTopDot) — раз в несколько секунд.</remarks>
    private void UpdateSideValues(bool force = false)
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            return;
        }

        bool running = EngineControl.IsRunning;

        if (!_powerBusy)
            PowerLabel.Text = running ? "Выключить обход" : "Включить обход";

        Side(SideVpn, running && settings.NeedsProxy ? "Вкл" : "Выкл", running && settings.NeedsProxy);
        Side(SideDesync, running && settings.NeedsDesync ? "Вкл" : "Выкл", running && settings.NeedsDesync);
        Side(SideTg, settings.TelegramProxy ? "Вкл" : "Выкл", settings.TelegramProxy && running);

        Side(SideDns,
            settings.UseRouterDns ? "роутер"
                : settings.DnsCryptActive is { } crypt ? DnsCrypt.Describe(crypt).Title
                : settings.DnsAuto ? "авто" : settings.DnsServer ?? string.Empty,
            settings.UseRouterDns || settings.DnsCryptActive is not null);

        if (force || _routeCount < 0 || DateTime.UtcNow - _routeCountAt > TimeSpan.FromSeconds(30))
        {
            try
            {
                _routeCount = UserRulesFile.Load().Entries.Count(r => r.Enabled);
            }
            catch (Exception)
            {
                _routeCount = 0;
            }

            _routeCountAt = DateTime.UtcNow;
        }

        // Числом: «32 маршрута» не помещается рядом с именем пункта в колонке меню.
        Side(SideRoutes, _routeCount > 0 ? _routeCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty, true);
        SideRoutes.ToolTip = _routeCount > 0 ? $"Своих правил: {_routeCount}" : null;
    }

    private static void Side(RadioButton item, string text, bool on)
    {
        SideValue.SetText(item, text);
        SideValue.SetOn(item, on);
    }
}
