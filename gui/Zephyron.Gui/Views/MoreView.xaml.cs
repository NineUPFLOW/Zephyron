using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Core.Themes;
using Zephyron.Core.Updates;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>
/// Всё, что не заслужило своего раздела, но нужно.
/// </summary>
/// <remarks>
/// Режим и автозапуск отсюда ушли на «Главную», а подписки — в «VPN».
/// Здесь они и правда лежали по остаточному принципу: режим задаёт рамку,
/// внутри которой имеют смысл маршруты, и место ему рядом с кнопкой запуска,
/// а подписке — там, где видно, что она приносит.
/// </remarks>
public partial class MoreView : UserControl
{
    public MoreView()
    {
        InitializeComponent();

        ShowAbout();

        Loaded += (_, _) => Reload();
    }

    /// <summary>
    /// Открывает мастер первого запуска заново.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Только просит окно показать мастер — ничего из уже настроенного
    /// (подписку, режим, пресет) не трогает.
    /// </para>
    /// <para>
    /// Отметку «пройден» (<see cref="AppSettings.OnboardingDone"/>) не снимает.
    /// До 07.10 снимала, а ставилась она обратно только «Готово» или
    /// «Пропустить»: открыл мастер посмотреть, ушёл в другой раздел — и он
    /// встречал при каждом следующем запуске. Владелец 07.10: «почему опять
    /// запускается мастер первого запуска при билде» — три сборки подряд.
    /// </para>
    /// </remarks>
    private void OnRestartOnboarding(object sender, RoutedEventArgs e)
    {
        try
        {
            (Window.GetWindow(this) as MainWindow)?.RestartOnboarding();

            Status.Text = "Мастер открыт на «Главной».";
        }
        catch (Exception ex)
        {
            Status.Text = "Мастер открыть не удалось: " + ex.GetBaseException().Message;
        }
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowRoutes();

        ShowUpdates(settings);

        RootValue.Text = Path.GetFullPath(".");

        Status.Text = "Изменения сохраняются сразу, а вступают в силу после следующего запуска движков.";

        StartDefenderCheck();
    }

    /// <summary>
    /// Спрашивает Defender, исключена ли наша папка.
    /// </summary>
    /// <remarks>
    /// В стороне от показа: <c>Get-MpPreference</c> поднимает PowerShell
    /// и отвечает секунду-полторы, а раздел должен открыться сразу.
    /// </remarks>
    private void StartDefenderCheck()
    {
        DefenderValue.Text = "идёт проверка…";
        DefenderButton.IsEnabled = false;

        var path = Path.GetFullPath(".");
        var dispatcher = Dispatcher;

        // Слабо: ушли из раздела раньше ответа PowerShell — вопрос не держит раздел в памяти.
        var weak = new WeakReference<MoreView>(this);

        _ = Task.Run(() =>
        {
            bool excluded = IsExcluded(path);

            dispatcher.Invoke(() =>
            {
                if (!weak.TryGetTarget(out var view))
                    return;

                view.DefenderValue.Text = excluded ? "добавлена" : "нет";
                view.DefenderButton.IsEnabled = !excluded;
            });
        });
    }

    private void OnExclude(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(".");

        DefenderButton.IsEnabled = false;
        Status.Text = "Прошу Defender об исключении…";

        _ = Task.Run(() =>
        {
            int code = Run(
                "powershell",
                $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"");

            // Проверяем делом, а не кодом возврата: при включённой защите
            // от изменений команда проходит и не делает ничего.
            bool excluded = IsExcluded(path);

            Dispatcher.Invoke(() =>
            {
                DefenderValue.Text = excluded ? "добавлена" : "нет";
                DefenderButton.IsEnabled = !excluded;

                Status.Text = excluded
                    ? $"В исключениях: {path}"
                    : code == 0
                        ? "Команда прошла, а исключение не появилось — включена защита от "
                          + "изменений. Добавьте вручную: Безопасность Windows → Защита от "
                          + "вирусов → Исключения."
                        : $"Не получилось, код {code}.";
            });
        });
    }

    private static bool IsExcluded(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-MpPreference).ExclusionPath -join [char]10\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return output.Split('\n').Any(line => string.Equals(
                line.Trim().TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Defender может быть выключен, подменён другим антивирусом или
            // не отвечать вовсе. Ни один из случаев не повод не открыть раздел.
            return false;
        }
    }

    private void ShowRoutes()
    {
        try
        {
            var count = UserRulesFile.Load(UserRulesFile.DefaultPath).Entries.Count;

            RoutesValue.Text = count == 0
                ? "Своих маршрутов нет: соединения идут по общим правилам."
                : $"Своих маршрутов: {count}. Это выборы «напрямую», «десинк» и «через VPN», "
                  + "сделанные руками поверх общих правил.";

            ForgetRoutesButton.IsEnabled = count > 0;
        }
        catch (Exception ex)
        {
            RoutesValue.Text = "Не удалось прочитать файл своих маршрутов: " + ex.GetBaseException().Message;
            ForgetRoutesButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Работает ли супервизор прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Стереть рабочие файлы под живым супервизором значит потерять след
    /// запущенных процессов: остановить их станет нечем, а следующий запуск
    /// упрётся в занятый драйвер и осиротевший TUN.
    /// </remarks>
    private static bool EnginesRunning()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        return state is not null && state.IsSupervisorAlive();
    }

    /// <summary>Копия настроек под паролем: ключи подписок внутри, поэтому открытой не бывает.</summary>
    private async void OnBackupSave(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Куда сохранить копию настроек",
            FileName = "zephyron-backup" + SettingsBackup.Extension,
            Filter = "Копия настроек Zephyron (*.zybackup)|*.zybackup",
        };

        if (dialog.ShowDialog() != true)
            return;

        var cloud = CloudFolder.Of(Path.GetDirectoryName(Path.GetFullPath(dialog.FileName)) ?? dialog.FileName);

        if (cloud is not null && !ConfirmDialog.Ask(Window.GetWindow(this), "Папка синхронизации",
                $"Папка принадлежит {cloud}: файл уйдёт в облако. В копии ключи подписок и серверов; она зашифрована паролем, но потеряете пароль — не откроете, а слабый пароль можно подобрать. Сохранить сюда?",
                "Сохранить", "Отмена", defaultNo: true))
        {
            return;
        }

        var password = PasswordDialog.Ask(Window.GetWindow(this), "Пароль копии",
            $"Без него копию не открыть, восстановить пароль нельзя. Не короче {SettingsBackup.MinPassword} знаков.",
            confirm: true, SettingsBackup.MinPassword);

        if (password is null)
            return;

        BackupSave.IsEnabled = BackupLoad.IsEnabled = false;
        Status.Text = "Шифрую копию…";

        try
        {
            await Task.Run(() => SettingsBackup.Export(dialog.FileName, password));
            Status.Text = $"Копия сохранена: {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Сохранить копию не удалось: " + ex.GetBaseException().Message;
        }
        finally
        {
            BackupSave.IsEnabled = BackupLoad.IsEnabled = true;
        }
    }

    private async void OnBackupLoad(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Откуда восстановить настройки",
            Filter = "Копия настроек Zephyron (*.zybackup)|*.zybackup|Все файлы|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        var running = EnginesRunning();

        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Восстановить настройки?",
                "Текущие настройки, маршруты, подписки и свои списки будут заменены содержимым копии. Прежние файлы сохранятся в runtime/backups. Пины особых маршрутов из копии встанут в hosts."
                + (running ? " Движки будут остановлены." : string.Empty),
                "Восстановить", "Отмена", danger: true, defaultNo: true))
        {
            return;
        }

        var password = PasswordDialog.Ask(Window.GetWindow(this), "Пароль копии", "Тот, что вводили при сохранении.",
            confirm: false, SettingsBackup.MinPassword);

        if (password is null)
            return;

        BackupSave.IsEnabled = BackupLoad.IsEnabled = false;
        Status.Text = "Открываю копию…";
        bool stopped = false;

        try
        {
            // Сначала пароль и целость: неверный пароль не должен гасить движки.
            var opened = await Task.Run(() => SettingsBackup.Read(dialog.FileName, password));

            if (running)
            {
                await EngineControl.StopAsync("восстановление копии настроек", CancellationToken.None);
                stopped = true;
            }

            var restored = await Task.Run(() => SettingsBackup.Apply(opened));

            // Пины особых маршрутов — в hosts, как при загрузке набора маршрутов.
            int pinned = 0;
            string? pinProblem = null;

            try
            {
                foreach (var group in RulesShare.Pins(UserRulesFile.Load().Entries).GroupBy(p => p.Desync))
                {
                    var result = Zephyron.Proxy.HostsEditor.Pin(
                        group.ToDictionary(p => p.Name, p => p.Address, StringComparer.OrdinalIgnoreCase),
                        note: group.Key ? $"копия настроек {Zephyron.Proxy.HostsEditor.DesyncMark}" : "копия настроек");
                    pinned += result.Reverted is null ? group.Count() : 0;
                }

                if (pinned > 0)
                    _ = Zephyron.Proxy.HostsEditor.FlushDnsAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pinProblem = ex.GetBaseException().Message;
            }

            Journal.Write("настройки", $"восстановлено из копии: {restored.Files.Count} файлов, прежние — в {restored.BackupDir}"
                + (pinProblem is null ? string.Empty : "; пины в hosts не встали: " + pinProblem));

            // Движки — уже по восстановленным настройкам; окно читает их заново
            // только при запуске, поэтому программа перезапускается сама.
            if (running)
                await EngineControl.StartAsync("восстановление копии настроек", CancellationToken.None);

            App.Relaunch();
        }
        catch (SettingsBackup.BadBackupException ex)
        {
            Status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Status.Text = "Восстановить не удалось: " + ex.GetBaseException().Message
                + (stopped ? " Часть файлов могла замениться, прежние — в runtime/backups. Движки запускаю снова." : string.Empty);

            // Остановили ради записи — не оставляем человека без обхода.
            if (stopped)
                await EngineControl.StartAsync("восстановление копии не удалось", CancellationToken.None);
        }
        finally
        {
            BackupSave.IsEnabled = BackupLoad.IsEnabled = true;
        }
    }

    private static bool Confirm(string question) =>
        ConfirmDialog.Show(
            question,
            "Zephyron",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Отодвигает файл в сторону вместо удаления.
    /// </summary>
    /// <remarks>
    /// Настройки и маршруты набираются руками и месяцами, а кнопка стоит
    /// в разделе, куда заходят посмотреть версию. Копия не стоит ничего
    /// и однажды окупается целиком.
    /// </remarks>
    private static void SetAside(string path)
    {
        if (!File.Exists(path))
            return;

        var backup = path + ".bak";

        // Затираем предыдущую копию: две правки подряд означают, что
        // разбираются прямо сейчас, и интересна последняя.
        if (File.Exists(backup))
            File.Delete(backup);

        File.Move(path, backup);
    }

    /// <summary>«Искать обновления при запуске» — тумблер и слово рядом.</summary>
    private void ShowUpdates(AppSettings settings)
    {
        UpdatesSwitch.IsChecked = settings.CheckForUpdates;
        UpdatesWord.Text = settings.CheckForUpdates ? "включено" : "не включено";
        AutoUpdateSwitch.IsChecked = settings.AutoInstallUpdates;
        AutoUpdateWord.Text = settings.AutoInstallUpdates ? "включено" : "не включено";
    }

    private void OnAutoUpdateSwitch(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = AppSettings.Load(AppSettings.DefaultPath) with { AutoInstallUpdates = AutoUpdateSwitch.IsChecked == true };

            next.Save(AppSettings.DefaultPath);
            ShowUpdates(next);

            Status.Text = next.AutoInstallUpdates
                ? (next.CheckForUpdates
                    ? "Сохранено: найденное при запуске обновление поставится само."
                    : "Сохранено, но поиск обновлений при запуске выключен — включите его выше.")
                : "Сохранено: обновление будет предлагаться окном, как раньше.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
            ShowUpdates(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnUpdatesSwitch(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = AppSettings.Load(AppSettings.DefaultPath) with { CheckForUpdates = UpdatesSwitch.IsChecked == true };

            next.Save(AppSettings.DefaultPath);
            ShowUpdates(next);

            // Движкам безразлично: читается окном при его запуске.
            Status.Text = next.CheckForUpdates
                ? "Сохранено: обновления будут проверяться при запуске программы."
                : "Сохранено: при запуске обновления не проверяются, это можно сделать кнопкой на «Главной».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не записалось: " + ex.GetBaseException().Message;
            ShowUpdates(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (EnginesRunning())
        {
            Status.Text = "Сначала выключите движки: под ними лежит состояние супервизора, и без него их будет нечем остановить.";

            return;
        }

        if (!Confirm(
            "Сбросить настройки до заводских?\n\n"
            + "Режим, пресет, выбранный сервер и свои маршруты будут забыты, рабочие файлы "
            + "удалены. Ссылка подписки останется: её выдаёт поставщик, и восстановить её "
            + "программа не может.\n\n"
            + "Прежние настройки и маршруты лягут рядом с расширением .bak."))
        {
            return;
        }

        try
        {
            var subscription = AppSettings.Load(AppSettings.DefaultPath).SubscriptionUrl;

            // Runtime первым: он единственный, что не откатить, и если занят —
            // настройки с маршрутами ещё на месте.
            if (Directory.Exists("runtime"))
                Directory.Delete("runtime", recursive: true);

            try
            {
                SetAside(AppSettings.DefaultPath);
                SetAside(UserRulesFile.DefaultPath);

                (AppSettings.Fresh with { SubscriptionUrl = subscription }).Save(AppSettings.DefaultPath);
            }
            catch
            {
                // Отложенное возвращаем, чтобы не остаться без файлов.
                foreach (var path in new[] { AppSettings.DefaultPath, UserRulesFile.DefaultPath })
                {
                    try
                    {
                        if (!File.Exists(path) && File.Exists(path + ".bak"))
                            File.Move(path + ".bak", path);
                    }
                    catch (Exception)
                    {
                    }
                }

                throw;
            }

            Reload();
            Status.Text = "Настройки сброшены, подписка осталась. Прежние сохранены рядом с расширением .bak.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не получилось: " + ex.GetBaseException().Message;
        }
    }

    private void OnForgetRoutes(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
            "Забыть все свои маршруты?\n\n"
            + "Выборы «напрямую», «десинк» и «через VPN», сделанные руками, будут сняты — "
            + "решать станут общие правила.\n\n"
            + "Прежний файл ляжет рядом с расширением .bak."))
        {
            return;
        }

        try
        {
            SetAside(UserRulesFile.DefaultPath);

            ShowRoutes();
            Status.Text = "Выбор по сайтам сброшен. Прежний файл лежит рядом с расширением .bak.";

            // В отличие от сброса настроек, здесь движки могли остаться
            // работать — и работают они по прежним маршрутам, пока их
            // не перезапустить.
            this.Offer("выбор по сайтам");
        }
        catch (Exception ex)
        {
            Status.Text = "Не получилось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Убирает журналы и конфиги прошлых запусков.
    /// </summary>
    /// <remarks>
    /// Повторяет отбор консольной команды clean: удаляются только json и log,
    /// действующий конфиг остаётся. Без него не запуститься, а собрать заново
    /// можно лишь с подпиской под рукой.
    /// </remarks>
    private void OnClean(object sender, RoutedEventArgs e)
    {
        if (EnginesRunning())
        {
            Status.Text = "Супервизор работает и держит эти файлы. Сначала выключите движки.";
            return;
        }

        try
        {
            const string runtime = "runtime";

            if (!Directory.Exists(runtime))
            {
                Status.Text = "Каталог runtime пуст или не существует — убирать нечего.";
                return;
            }

            var keep = Path.GetFullPath(Path.Combine(runtime, "singbox.json"));

            var removed = 0;
            var busy = 0;
            long freed = 0;

            foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
                    continue;

                var extension = Path.GetExtension(file);

                bool removable = extension is ".json" or ".log"
                    || Path.GetFileName(file).Contains(".log.", StringComparison.OrdinalIgnoreCase);

                if (!removable)
                    continue;

                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);

                    removed++;
                    freed += size;
                }
                catch (Exception)
                {
                    // Один занятый файл не повод бросать уборку на половине.
                    busy++;
                }
            }

            Status.Text = $"Удалено файлов: {removed}, освобождено {freed / 1024.0:0.#} КБ"
                + (busy > 0 ? $", не удалось убрать: {busy} (заняты). " : ". ")
                + "Действующий конфиг сохранён.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не получилось: " + ex.GetBaseException().Message;
        }
    }

    private void OnResetNetwork(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
            "Сбросить сетевой стек Windows?\n\n"
            + "winsock и TCP/IP вернутся к исходным настройкам. Потребуется перезагрузка "
            + "компьютера: без неё сеть останется в переходном состоянии, то есть хуже "
            + "исходного."))
        {
            return;
        }

        ResetNetworkButton.IsEnabled = false;
        Status.Text = "Сбрасываю сетевой стек…";

        _ = Task.Run(() =>
        {
            string? failed = null;

            foreach (var arguments in new[] { "int ip reset", "winsock reset" })
            {
                int code = Run("netsh", arguments);

                if (code != 0)
                {
                    failed = code == TimedOut
                        ? "netsh не ответил за 30 с"
                        : $"netsh {arguments} завершился с кодом {code}. "
                          + "Обычно это значит, что не хватило прав администратора.";

                    break;
                }
            }

            Dispatcher.Invoke(() =>
            {
                ResetNetworkButton.IsEnabled = true;

                Status.Text = failed
                    ?? "Сетевой стек сброшен. Перезагрузите компьютер: без этого сеть "
                       + "останется в переходном состоянии.";
            });
        });
    }

    private const int TimedOut = -2;

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return -1;

            return process.WaitForExit(30_000) ? process.ExitCode : TimedOut;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>Карточка «О программе»: неизменна, заполняется один раз.</summary>
    private void ShowAbout()
    {
        AuthorValue.Text = $"Zephyron {UpdateCheck.Current}. Автор — {About.Author}; идеи и отчёты об ошибках "
            + "приносят участники обсуждений и issues на GitHub. Лицензия MIT, программа бесплатна.";

        RepositoryLink.Tag = About.Repository;
        IssuesLink.Tag = About.Issues;
        DiscussionsLink.Tag = About.Discussions;
        TelegramLink.Tag = About.Telegram;

        Components.ItemsSource = About.Components;

        ShowDownloads();
    }

    /// <summary>Скачиваний на GitHub — спрашиваем один раз за запуск окна.</summary>
    private static long? _downloads;

    /// <remarks>Ответ ждём по слабой ссылке: GitHub думает до 10 с, и раздел, из которого ушли, не должен ждать его в памяти.</remarks>
    private void ShowDownloads()
    {
        if (_downloads is { } known)
        {
            ShowDownloads(known);
            return;
        }

        var dispatcher = Dispatcher;
        var weak = new WeakReference<MoreView>(this);

        DownloadStats.TotalAsync(CancellationToken.None).ContinueWith(work => dispatcher.InvokeAsync(() =>
        {
            _downloads ??= work.Result;

            if (_downloads is { } total && weak.TryGetTarget(out var view))
                view.ShowDownloads(total);
        }));
    }

    private void ShowDownloads(long total)
    {
        DownloadsValue.Text = $"Скачано с GitHub: {total:N0} раз. Это скачивания, а не установки: "
            + "обновление из программы тоже засчитывается. Своего счётчика у программы нет — "
            + "она не сообщает о вас никуда.";
        DownloadsValue.Visibility = Visibility.Visible;
    }

    private void OnDonate(object sender, RoutedEventArgs e) => DonateMenu.Show(sender);

    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;

        Links.Open(url);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder })
            return;

        var target = Path.GetFullPath(folder);

        if (!Directory.Exists(target))
        {
            Status.Text = $"Папки пока нет: {target}. Она появится вместе с первым файлом.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Status.Text = "Открыть не удалось: " + ex.GetBaseException().Message;
        }
    }

    private void OnOpenCheck(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.Open("check");
}
