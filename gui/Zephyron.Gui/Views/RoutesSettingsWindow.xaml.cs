using System.Windows;
using System.Windows.Controls.Primitives;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Zapret;
using Zephyron.Proxy;

namespace Zephyron.Gui.Views;

/// <summary>
/// Настройки маршрутов — отдельным окном, как «Настройки туннеля» у вкладки VPN.
/// </summary>
/// <remarks>
/// Оба выключателя Discord движкам безразличны: первый решает, предлагать ли
/// перезапуск Discord, второй читает сторож голоса (DiscordVoiceWatch)
/// на каждом круге. Перезапуск движков предлагают только пины напрямую:
/// десинк подхватывает список щита сам, а туннелю прибитые адреса имён
/// «через VPN» достаются только при сборке конфига.
/// </remarks>
public partial class RoutesSettingsWindow : Window
{
    private readonly IReadOnlyList<PinSolution> _solutions = PinSolutions.Load();

    /// <summary>Есть ли IPv6; <c>null</c> — ещё не проверено.</summary>
    private bool? _ipV6;

    public RoutesSettingsWindow()
    {
        InitializeComponent();
        StatusLook.Attach(StatusBox, Status, StatusMark);

        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40);

        Show(AppSettings.Load(AppSettings.DefaultPath));
        ShowSolutions();

        Loaded += async (_, _) =>
        {
            // До трёх секунд — соединение по IPv6 к резолверу Google.
            _ipV6 = await HostsEditor.HasIpV6Async(CancellationToken.None);
            ShowSolutions();
        };
    }

    private void ShowSolutions()
    {
        try
        {
            Solutions.ItemsSource = PinSolutionRows.Build(_solutions, _ipV6, s => PinSolutions.StateOf(s));
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось прочитать пины «напрямую»: " + ex.GetBaseException().Message;
        }
    }

    private void OnSolution(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string id }
            || _solutions.FirstOrDefault(s => s.Id == id) is not { } solution)
        {
            return;
        }

        try
        {
            var was = PinSolutions.StateOf(solution);
            PinResult result;

            if (was.On)
            {
                result = PinSolutions.Disable(solution);
            }
            else
            {
                if (PinSolutions.Unavailable(solution, _ipV6 ?? false) is { } why)
                {
                    Status.Text = $"{solution.Name}: {why}.";
                    ShowSolutions();
                    return;
                }

                result = PinSolutions.Enable(solution, _ipV6 ?? false);
            }

            _ = HostsEditor.FlushDnsAsync();

            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Щит перечитывается winws2 сам, при смене файла: пометка пина
            // действует без перезапуска. Движки стоят — файл просто перепишется
            // заново при запуске.
            TunnelConfig.WriteDesyncLists(settings);

            var said = result.Reverted
                ?? (was.On
                    ? $"{solution.Name}: пины сняты."
                    : $"{solution.Name}: прибито имён — {PinSolutions.StateOf(solution).Pinned}.")
                + (result.Shadowed.Count == 0
                    ? string.Empty
                    : $" Ниже в hosts есть чужие строки на те же имена: {string.Join(", ", result.Shadowed.Take(2))}.");

            if (!was.On && result.Reverted is null)
                said += Blockers(solution, settings);

            Status.Text = said;
            (Owner as MainWindow)?.OfferRestart("Пины в hosts изменены");
        }
        catch (Exception ex)
        {
            Status.Text = $"{solution.Name}: не записалось — " + ex.GetBaseException().Message;
        }

        ShowSolutions();
    }

    /// <summary>Чем маршруты помешают решению — пусто, если ничем.</summary>
    private static string Blockers(PinSolution solution, AppSettings settings)
    {
        try
        {
            var (engine, _) = RuleSetExpander.LoadFor(settings);
            var blockers = PinSolutions.RouteBlockers(solution, engine.RuleSet, settings.NeedsProxy);

            if (blockers.Count == 0)
                return string.Empty;

            var direct = blockers.Any(b => b.Why == DesyncBypass.Direct);

            return $" Но {blockers.Count} из {solution.Names.Count} имён "
                + (direct ? "стоят «напрямую»" : "стоят «через VPN», а туннель выключен")
                + " — десинка им не будет. Поставьте части сервиса «десинк» в «Маршрутах».";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void Show(AppSettings settings)
    {
        Restart.IsChecked = settings.OfferDiscordRestart;
        RestartWord.Text = settings.OfferDiscordRestart ? "включено" : "не включено";

        Learn.IsChecked = settings.LearnDiscordVoice;
        LearnWord.Text = settings.LearnDiscordVoice ? "включено" : "не включено";

        LearnRoblox.IsChecked = settings.LearnRobloxServers;
        LearnRobloxWord.Text = settings.LearnRobloxServers ? "включено" : "не включено";

        LearnArc.IsChecked = settings.LearnArcRaidersServers;
        LearnArcWord.Text = settings.LearnArcRaidersServers ? "включено" : "не включено";
    }

    private void OnLearnRoblox(object sender, RoutedEventArgs e)
    {
        bool on = LearnRoblox.IsChecked == true;

        Save(s => s with { LearnRobloxServers = on },
            on ? "Адреса серверов Roblox добавляются, пока его серверы идут «через VPN»."
               : "Список серверов Roblox больше не пополняется автоматически.");
    }

    private void OnLearnArc(object sender, RoutedEventArgs e)
    {
        bool on = LearnArc.IsChecked == true;

        Save(s => s with { LearnArcRaidersServers = on },
            on ? "Адреса серверов ARC Raiders добавляются, пока её серверы идут «через VPN» и игра запущена."
               : "Список серверов ARC Raiders больше не пополняется автоматически.");
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        bool on = Restart.IsChecked == true;

        Save(s => s with { OfferDiscordRestart = on },
            on ? "После смены маршруты Discord программа предложит его перезапустить."
               : "Перезапуск Discord больше не предлагается.");
    }

    private void OnLearn(object sender, RoutedEventArgs e)
    {
        bool on = Learn.IsChecked == true;

        Save(s => s with { LearnDiscordVoice = on },
            on ? "Адреса голоса Discord добавляются, пока голос идёт «через VPN»."
               : "Список голоса Discord больше не пополняется автоматически.");
    }

    /// <summary>Перечитать, изменить, записать — окно могло пережить чужую правку настроек.</summary>
    private void Save(Func<AppSettings, AppSettings> change, string said)
    {
        try
        {
            var next = change(AppSettings.Load(AppSettings.DefaultPath));

            next.Save(AppSettings.DefaultPath);
            Show(next);
            Status.Text = said;
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
            Show(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
