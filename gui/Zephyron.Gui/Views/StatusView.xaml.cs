using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Zephyron.Core;
using Zephyron.Core.Diagnostics;
using Zephyron.Core.Rules;
using Zephyron.Core.Themes;
using Zephyron.Core.Updates;
using Zephyron.Proxy;
using Zephyron.Supervisor;

namespace Zephyron.Gui.Views;

/// <summary>Строка про один движок.</summary>
/// <param name="Word">Отметка в карточке одним-двумя словами (EngineHealth.Word).</param>
/// <param name="Detail">Строка под ней: причина надзора, когда движок не в порядке.</param>
/// <param name="Mark">Состояние движка: знак в строке «Компоненты» и цвет его трассы.</param>
public sealed record EngineRow(string Name, string Word, string Detail, HomeState Mark, int? ProcessId = null);

/// <summary>Предупреждение, которое стоит прочитать до запуска.</summary>
public sealed record WarningRow(string Title, string Body);

/// <summary>
/// Состояние настройки и движков, плюс запуск и остановка.
/// </summary>
/// <remarks>
/// <para>
/// Читает то же, что читает консоль, и теми же библиотеками:
/// <see cref="AppSettings"/>, <see cref="SupervisorState"/>,
/// <see cref="HostsEditor"/>, <see cref="SecuritySoftware"/>. Ни одна строка
/// в <c>src\</c> ради этого не тронута.
/// </para>
/// <para>
/// Запуск и остановка идут отдельным процессом — этой же программой
/// с ключом супервизора. Это не лень:
/// супервизор должен пережить закрытие окна, а значит быть отдельным
/// процессом. Консоль поступает ровно так же, запуская саму себя с ключом
/// <c>start</c>, — и раз путь один, у окна и меню не разойдётся поведение.
/// </para>
/// </remarks>
public partial class StatusView : UserControl
{
    /// <summary>Обычный период опроса.</summary>
    private static readonly TimeSpan CalmInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Период опроса во время запуска: подпись фазы и счётчик секунд должны
    /// успевать за движками, иначе окно выглядит замершим ровно тогда, когда
    /// на него и смотрят.
    /// </summary>
    private static readonly TimeSpan StartingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Сколько показывать запуск, прежде чем показать состояние как есть.
    /// </summary>
    /// <remarks>
    /// С запасом больше суммы таймаутов готовности обоих движков и первых
    /// перезапусков: объявить «не поднялось» раньше этого срока значило бы
    /// соврать про ещё идущую работу.
    /// </remarks>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

    private readonly DispatcherTimer _refresh = new() { Interval = CalmInterval };

    private DateTimeOffset? _startingSince;

    private void OnTelegram(object sender, RoutedEventArgs e) => OpenLink(About.Telegram);

    private void OnGitHub(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>Описание — README на GitHub: он и есть документация, другой нет.</summary>
    private void OnDocs(object sender, RoutedEventArgs e) => OpenLink(About.Repository + "#readme");

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — те же ссылки есть в «Ещё», в карточке «О программе».
        }
    }

    private void OnArtSize(object sender, SizeChangedEventArgs e) => PlaceArt();

    /// <summary>Уже этой колонки персонажа в ней не ставим — картинка ляжет по центру страницы.</summary>
    private const double ArtColumnMin = 220;

    /// <summary>Затемнение над персонажем: текста там нет, читаемость беречь незачем.</summary>
    private const double ArtDim = 0.12;

    /// <summary>
    /// Кладёт картинку темы так, чтобы её середина стояла посередине правой
    /// колонки, и затемняет её под карточками так же, как тема.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По высоте страницы, с сохранением пропорций: персонаж в картинках
    /// стоит по центру, и при вписывании по высоте он целиком в колонке.
    /// Слева картинка может не доставать до края — там сплошная подложка.
    /// Край картинки растворяется в ней (маска прозрачности), и растворение
    /// кончается до колонки с артом. Прежде я рассчитывал, что края у артов
    /// тёмные и шва под затемнением не видно: у «Слойки» так, а у «Trident»
    /// светлая картина обрывалась вертикальной чертой посреди карточек
    /// (владелец, 30.09: «темы обрываются»).
    /// </para>
    /// <para>
    /// Под карточками — затемнение темы (BackdropDim): с ним тема прошла
    /// проверку читаемости, и текст на полупрозрачных карточках читается
    /// так же, как на прочих вкладках. Переход к лёгкому затемнению —
    /// у правого края содержимого, по ширине одной карточки.
    /// </para>
    /// <para>
    /// Колонка уже 220 (окно по умолчанию, 1080): персонажу места нет,
    /// картинка ложится по центру страницы под сплошным затемнением темы —
    /// как на прочих вкладках. Прежде в такой колонке торчала полоска арта
    /// в несколько точек и читалась ошибкой отрисовки (снимок 30.09).
    /// </para>
    /// </remarks>
    private void PlaceArt()
    {
        double width = ArtLayer.ActualWidth, height = ArtLayer.ActualHeight;

        // Арт — картинка темы или её живой фон: видео в словаре ресурсов
        // не лежит (BackdropVideo), его рисунок берётся оттуда, а пропорции —
        // у кадра.
        (Size Size, Func<TileBrush> Make)? art = TryFindResource("SideArt") is ImageBrush { ImageSource: { } image }
            ? (new Size(image.Width, image.Height), () => new ImageBrush(image))
            : BackdropVideo.Current is { } video
                ? (video.Size, () => video.Brush())
                : null;

        if (art is not { } content || width <= 0 || height <= 0 || content.Size.Height <= 0 || content.Size.Width <= 0)
        {
            Art.Fill = null;
            ArtGround.Fill = null;
            ArtShade.Fill = null;
            return;
        }

        double aspect = content.Size.Width / content.Size.Height;

        double column = (_artLeft ? ContentColumn : ArtColumn).ActualWidth;
        bool side = column >= ArtColumnMin;
        double centre = !side ? width / 2 : _artLeft ? column / 2 : width - column / 2;

        // Уложенная руками на миниатюре в редакторе тем (владелец 15.10) —
        // где положили, а под ней градиент; иначе как прежде: по высоте
        // страницы, середина — посередине колонки.
        var placement = TryFindResource(HomePlacement.Key) as HomePlacement;

        var (left, top, w, h) = placement is { } placed
            ? SideArtLayout.Place(width, height, aspect, placed.X, placed.Y, placed.Size)
            : SideArtLayout.Fit(width, height, centre, aspect);

        var fill = content.Make();
        fill.Stretch = Stretch.Fill;
        fill.TileMode = TileMode.None;
        fill.ViewportUnits = BrushMappingMode.Absolute;
        fill.Viewport = new Rect(left, top, w, h);
        Art.Fill = fill;

        if (placement is not null)
        {
            ArtGround.Fill = TryFindResource(HomePlacement.GroundKey) as Brush;
            Art.OpacityMask = HomePlacement.Feather(new Rect(left, top, w, h));
            Shade(width, column, side);
            return;
        }

        ArtGround.Fill = null;

        // Левый край картинки — в подложку, без черты. Растворение кончается
        // до колонки с артом (не позже 80 % пути от края картинки до неё),
        // чтобы персонаж стоял целиком; без колонки — на четверти картинки.
        // Картинка, начинающаяся левее страницы, края не показывает — маска
        // не нужна.
        double fade = side ? Math.Max(120, (width - column - left) * 0.8) : w * 0.25;

        Art.OpacityMask = left <= 0
            ? null
            : new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(left, 0),
                EndPoint = new Point(left + fade, 0),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 1),
                },
            };

        Shade(width, column, side);
    }

    /// <summary>Колонка арта слева, карточки справа (HomeSide.Right).</summary>
    private bool _artLeft;

    /// <summary>Затемнение поверх арта: плотное под карточками, лёгкое в колонке.</summary>
    private void Shade(double width, double column, bool side)
    {
        var backdrop = TryFindResource("BackdropColor") is Color c ? c : Colors.Black;
        double dim = TryFindResource("BackdropDim") is double d ? d : 1.0;

        var under = Color.FromArgb((byte)Math.Round(dim * 255), backdrop.R, backdrop.G, backdrop.B);

        if (!side)
        {
            ArtShade.Fill = new SolidColorBrush(under);
            return;
        }

        // Арт слева (HomeSide.Right) — тот же переход зеркально: от карточек влево.
        double edge = _artLeft ? column : width - column;
        double toward = _artLeft ? -1 : 1;
        var over = Color.FromArgb((byte)Math.Round(ArtDim * 255), backdrop.R, backdrop.G, backdrop.B);

        // Переход начинается под карточками и тянется на большую часть
        // колонки. Прежде он шёл 300 точек от края карточек: затемнение
        // менялось прямо по волосам персонажа, у края колонки стоит ещё
        // и полоса прокрутки страницы, и картинка читалась обрубленной
        // по вертикальной черте (владелец, 30.09). Середина — не прямая:
        // промежуточная точка держит затемнение дольше у карточек и
        // быстрее отпускает у персонажа.
        var middle = Color.FromArgb((byte)Math.Round((dim * 0.45 + ArtDim * 0.55) * 255), backdrop.R, backdrop.G, backdrop.B);

        ArtShade.Fill = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(edge - 160 * toward, 0),
            EndPoint = new Point(edge + column * 0.7 * toward, 0),
            GradientStops =
            {
                new GradientStop(under, 0),
                new GradientStop(middle, 0.4),
                new GradientStop(over, 1),
            },
        };
    }

    private void OnQuickCheck(object sender, RoutedEventArgs e) => Open("check");

    private void OnQuickLog(object sender, RoutedEventArgs e) => LogWindow.Open();

    private void OnQuickDoctor(object sender, RoutedEventArgs e) => Open("doctor");

    private void OnDesyncSettings(object sender, RoutedEventArgs e) => Open("desync");

    private void Open(string section) => (Window.GetWindow(this) as MainWindow)?.Open(section);

    /// <summary>
    /// Папка настроек в проводнике, с выделенным файлом.
    /// </summary>
    /// <remarks>
    /// Папку, а не файл в блокноте: окно работает от администратора, и блокнот,
    /// открытый им, тоже был бы администраторским — правка настроек мимо окна
    /// с полными правами без нужды. Проводник открывается от человека.
    /// </remarks>
    private void OnQuickConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Path.GetFullPath(AppSettings.DefaultPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { File.Exists(file) ? "/select," + file : Path.GetDirectoryName(file)! },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            ShowProblem("Проводник не открылся: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>То же окно, что у шестерёнки на вкладке VPN.</summary>
    private void OnTunnelSettings(object sender, RoutedEventArgs e)
    {
        var window = new TunnelSettingsWindow { Owner = Window.GetWindow(this) };

        window.ShowDialog();

        if (window.Changed)
        {
            // И режим: «Игнорировать исключения» живёт в этом окне и меняет
            // пояснение «Гибрида» и «Туннеля», а ShowModes зовётся не по времени.
            ShowModes(AppSettings.Load(AppSettings.DefaultPath));
            Update();
            this.Offer("Настройки туннеля изменены");
        }
    }

    public StatusView()
    {
        InitializeComponent();

        // Опрос по времени, а не подписка на события: супервизор — отдельный
        // процесс и пишет своё состояние в файл. Две секунды достаточно,
        // чтобы нажатие «Запустить» отозвалось раньше, чем человек усомнится.
        _refresh.Tick += (_, _) => Update();
        _clock.Tick += (_, _) => ShowUptime();

        // Задержка меряется раз в пять секунд — на графике две минуты.
        LatencyChart.TimeLabels = ["−2 мин", "−1 мин", "сейчас"];

        // Лента событий из журнала (StatusView.Events.cs) — пока раздел на экране.
        Loaded += (_, _) => StartEvents();
        Unloaded += (_, _) => StopEvents();

        // Раскладка по ширине: компактная — на узком окне (880 и меньше).
        SizeChanged += (_, e) => SetCompact(e.NewSize.Width < CompactBelow);

        Loaded += (_, _) =>
        {
            // В Unloaded раздел уже вне дерева и Window.GetWindow — null, поэтому окно запоминается здесь.
            _host = Window.GetWindow(this) as MainWindow;
            Update();
            _clock.Start();

            // Не по времени, в отличие от прочего: режим меняется только
            // отсюда, а автозапуск спрашивается у планировщика запуском
            // schtasks — раз в две секунды это был бы процесс на ровном месте.
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ShowModes(settings);
            ShowAutostart();
            ShowWidth(settings);
            ShowSupport(settings);

            // Проверка обновлений идёт при запуске окна и может закончиться
            // уже после того, как «Главная» показана, — поэтому и подписка.
            // Отписка обязательна: событие статическое, а раздел пересоздаётся
            // при каждом возврате.
            UpdateNotice.Changed += OnUpdateChanged;
            ShowUpdate();

            // Слой частиц узнаёт о запуске, если «Главную» открыли посреди него.
            Pulse();

            _refresh.Start();
        };

        Unloaded += (_, _) =>
        {
            UpdateNotice.Changed -= OnUpdateChanged;
            _refresh.Stop();
            _clock.Stop();
            StopNet();

            // «Главная» ушла с экрана — частицам больше нечего показывать от неё.
            FieldPulse(null, null);
        };
    }

    private void OnUpdateChanged() => Dispatcher.InvokeAsync(ShowUpdate);

    private void ShowSupport(AppSettings settings) =>
        SupportCard.Visibility = settings.OnboardingDone && !settings.SupportCardHidden
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Звезда ставится на странице репозитория — на неё и ведём.</summary>
    private void OnStar(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>«Поддержать» ведёт на страницу донатов владельца.</summary>
    private void OnSupport(object sender, RoutedEventArgs e) => OpenLink(About.Support);

    private void OnSupportHide(object sender, RoutedEventArgs e)
    {
        SupportCard.Visibility = Visibility.Collapsed;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { SupportCardHidden = true })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — карточка вернётся при следующем запуске, беды в том нет.
        }
    }

    /// <summary>Карточка «вышла новая версия» — если есть что и о нём не сказали «не сейчас».</summary>
    private void ShowUpdate()
    {
        var release = UpdateNotice.Available;

        if (!UpdateNotice.ShouldOffer(release, AppSettings.Load(AppSettings.DefaultPath)))
        {
            UpdateCard.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateTitle.Text = $"Вышла {release!.Version}";

        var highlights = UpdateNotice.Highlights(release.Notes);

        UpdateWhat.Text = highlights.Count > 0
            ? string.Join(" · ", highlights) + "."
            : "Установлена " + UpdateCheck.Current + ".";

        UpdateCard.Visibility = Visibility.Visible;
    }

    // Установка та же, что в блоке «Обновление» ниже, и на той же странице:
    // прежде кнопка уводила в «Ещё» ради одного вопроса «обновить?».
    private void OnUpdateNow(object sender, RoutedEventArgs e) => Updates.Install();

    // «Что нового» — окно обновления (владелец, 06.10), а не страница выпуска:
    // там примечания всех пропущенных версий, а браузер — одной кнопкой в нём.
    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(release);
    }

    /// <summary>Перечитать карточку обновления — после «Пропустить версию» в окне.</summary>
    internal void RefreshUpdate() => ShowUpdate();

    /// <summary>
    /// «Не сейчас»: об этой версии больше не напоминать. Точка у «Ещё»
    /// остаётся — она тихая и уходит, только когда версия поставлена.
    /// </summary>
    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { DismissedUpdate = release.Version })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — напомним при следующем запуске, беды в том нет.
        }

        UpdateCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>То, что «Главная» читает с диска и у системы на каждом тике.</summary>
    /// <param name="Problem">Настройки не прочитались — почему; тогда прочее пусто.</param>
    /// <param name="SingBoxUp">Есть ли процесс sing-box — для фазы запуска, только пока он идёт.</param>
    /// <param name="ConfigBuilt">Когда собран конфиг туннеля; <c>null</c> — файла нет.</param>
    private sealed record Seen(
        AppSettings? Settings,
        string? Problem,
        string Subscriptions,
        SupervisorState? State,
        bool Running,
        bool SingBoxUp,
        DateTime? ConfigBuilt,
        bool Coming = false);

    private static Seen Read(bool starting)
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            return new Seen(null, $"Настройки не читаются: {ex.Message}", string.Empty, null, false, false, null);
        }

        // Ссылки на подписки — пароли, и в окне им не место. Показываем лишь
        // счёт: его хватает, чтобы понять, почему нет серверов.
        string subscriptions;

        try
        {
            subscriptions = SubscriptionBook.Load().Describe(settings);
        }
        catch (Exception)
        {
            subscriptions = string.Empty;
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        DateTime? built = null;

        try
        {
            // Конфиг собирается при каждом запуске, и серверы подписок берутся
            // тогда же, — поэтому время файла и есть время, когда их взяли.
            // Отдельного «подписки обновлены» программа не хранит.
            var config = new FileInfo(Path.Combine("runtime", "singbox.json"));

            if (config.Exists)
                built = config.LastWriteTime;
        }
        catch (Exception)
        {
        }

        return new Seen(settings, null, subscriptions, state, running, starting && !running && IsRunning("sing-box"), built,
            !running && EngineControl.Coming);
    }

    private bool _updating;
    private bool _updateAgain;

    /// <summary>
    /// Перечитывает состояние в пуле и показывает его.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Чтение — не в потоке окна. Сторож подвисаний 01.10 писал на «Главной»
    /// при запуске движков 300–860 мс «занят» при 0–80 мс процессора: поток
    /// окна ждал. В покое замер того же чтения — единицы миллисекунд, но
    /// в нём есть ожидания: занятый файл настроек читается повтором через
    /// 60 мс до пяти раз, процессы перебираются системой, а при запуске
    /// файлы пишут сразу надзор и движки. Какое из ожиданий подвешивало
    /// окно, не выяснено — уведены все.
    /// </para>
    /// <para>
    /// Вызов посреди идущего чтения не теряется и не множит чтения:
    /// отмечается, и чтение повторяется по окончании.
    /// </para>
    /// </remarks>
    private async void Update()
    {
        if (_updating)
        {
            _updateAgain = true;
            return;
        }

        _updating = true;

        try
        {
            do
            {
                _updateAgain = false;

                bool starting = _startingSince is not null;
                Show(await Task.Run(() => Read(starting)));
            }
            while (_updateAgain);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Show(Seen seen)
    {
        if (seen.Settings is not { } settings)
        {
            ShowProblem(seen.Problem!);
            return;
        }

        Problem.Visibility = Visibility.Collapsed;

        PresetValue.Text = settings.DescribePreset();
        ShowPresetNote(settings.PresetName);
        ServerValue.Text = TunnelStatus.ServerLine(settings, _exit);
        DnsValue.Text = settings.UseRouterDns
            ? RouterDnsConnection.ConfigWarning(settings) is not null
                ? $"Запасной DoH · {settings.DnsServer} (DNS роутера недоступен)"
                : $"Роутер · {settings.RouterDnsAddress}:53 · UDP"
            : settings.DnsServer;
        SubscriptionValue.Text = seen.Subscriptions;

        var state = seen.State;
        bool running = seen.Running;

        // Движки поднимает не эта кнопка — перезапуск из всплывающего
        // сообщения, трей, автозапуск: запуск показывается и тогда.
        if (_startingSince is null && seen.Coming)
        {
            _startingSince = DateTimeOffset.Now;
            _refresh.Interval = StartingInterval;
            Pulse();
        }

        ShowState(settings, state, running, seen.SingBoxUp, seen.Coming);
        ShowFooter(state, running, seen.ConfigBuilt);

        // Строка сети меряет, пока обход идёт или поднимается (владелец, 22.10):
        // запустили — пошла, остановили — встала.
        SyncNet(running || _startingSince is not null);
        ShowWarnings(settings);

        bool tunnelUp = running && state!.Services.Any(s => s.Name == "sing-box");
        _ = ReadExitAsync(settings, tunnelUp);
        _ = ReadMetersAsync(state, running, tunnelUp);
    }

    /// <summary>Выход, который держит движок; <c>null</c> — не знаем или туннеля нет.</summary>
    private string? _exit;

    private bool _readingExit;

    /// <summary>
    /// Спрашивает у движка, через какой сервер идёт трафик, — для строки «Сервер».
    /// </summary>
    /// <remarks>
    /// Владелец 26.09: «нигде так и не написано, какой сервер активен». Строка
    /// говорила «авто (по задержке)» — то, что в настройках, а не то, чем идёт
    /// трафик. Спрашиваем движок, как zy status: 23.09 настройки говорили
    /// «авто», а движок держался WARP из кэша. Прежний ответ держится до нового,
    /// чтобы строка не мигала на каждом тике.
    /// </remarks>
    private async Task ReadExitAsync(AppSettings settings, bool tunnel)
    {
        if (!tunnel)
        {
            _exit = null;
            return;
        }

        if (_readingExit)
            return;

        _readingExit = true;

        try
        {
            // В пуле целиком: до первого ожидания запрос идёт синхронно
            // (клиент, поиск прокси системы), а ответ пишется в журнал
            // недавних выходов — файлом.
            var (server, _) = await Task.Run(() => TunnelStatus.CurrentExitAsync(CancellationToken.None));

            if (server is null || server == _exit)
                return;

            _exit = server;
            ServerValue.Text = TunnelStatus.ServerLine(settings, server);
        }
        finally
        {
            _readingExit = false;
        }
    }

    private void ShowState(AppSettings settings, SupervisorState? state, bool running, bool singBoxUp, bool coming)
    {
        // Сборка конфига кнопкой «Запустить» показывает свой запуск: чтение состояния
        // посреди неё сказало бы «Выключен», а запуск уже идёт.
        if (_building)
            return;

        // Запуск показывается своим чередом: пока он идёт, «остановлено»
        // означает не отказ, а то, что супервизор ещё не дописал состояние.
        if (_startingSince is not null && ShowStarting(settings, state, running, singBoxUp, coming))
            return;

        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        _runningSince = running ? state!.StartedAt : null;

        if (!running)
        {
            ShowHero(HomeState.Off, settings.NeedsProxy || settings.NeedsDesync
                ? "Обход не работает: трафик идёт напрямую."
                : "В этом режиме запускать нечего: VPN выключен, пресет не выбран.");

            StartButton.IsEnabled = settings.NeedsProxy || settings.NeedsDesync;

            // Движки остаются на виду и выключенными, просто серыми. Пустое
            // место на их месте читается как «их нет вовсе», тогда как раздел
            // отвечает на другой вопрос: что должно работать и работает ли.
            SetEngines(Planned(settings, "остановлен", HomeState.Off));
            SetDnsWarning(null);

            return;
        }

        var services = state!.Services;

        // Резолвер, который не отвечает, — не «Работает»: 25.10 у владельца умер
        // Comss DNS, каждое имя ждало 11–12 с, «всё виснет», а «Главная» была зелёной.
        bool dnsDown = services.Any(s => s.DnsDown);
        bool healthy = services.All(s => s.Health == ServiceHealth.Healthy) && !dnsDown;

        SetDnsWarning(services.FirstOrDefault(s => s.DnsNotice is { Length: > 0 }) is { } dns
            ? DnsWarning(dns)
            : null);

        // Три состояния, а не два. «Оговорки» бывают разной тяжести: движок,
        // которому не удалась глубокая проверка, ещё несёт трафик, а умерший
        // не несёт ничего. Одним жёлтым это выглядело одинаково, и разницу
        // приходилось искать в списке ниже.
        bool broken = services.Any(s =>
            s.Health is ServiceHealth.Dead or ServiceHealth.Faulted);

        ShowHero(
            healthy ? HomeState.Running : broken ? HomeState.Failed : HomeState.Degraded,
            healthy
                ? Carrying(settings)
                : broken
                    ? BrokenHint(services)
                    : dnsDown
                        ? "Резолвер движка не отвечает — сайты открываются с задержкой или не открываются. Подробности ниже."
                        : "Часть движков не в порядке — подробности ниже.");

        // Причина упавшего движка — целиком строкой ниже, а не только подсказкой
        // его строки: та в одну строку и обрезается. 22.10 у знакомого владельца
        // «кодом 433» с объяснением, что делать, было видно лишь при наведении.
        if (broken && Failures(services) is { Length: > 0 } failures)
            ShowProblem(failures);

        SetEngines(services.Select(Row).ToList());
    }

    /// <summary>Пояснение под «Сбой»: какой движок упал.</summary>
    /// <remarks>Куда при этом идёт трафик, не утверждаем: в «Маршрутах» второй движок может работать.</remarks>
    internal static string BrokenHint(IEnumerable<ServiceState> services)
    {
        var broken = services.Where(s => s.Health is ServiceHealth.Dead or ServiceHealth.Faulted).ToList();
        var names = string.Join(" и ", broken.Select(s => s.Name));
        var verb = broken.Count > 1 ? "не работают" : "не работает";
        var faulted = broken.Any(s => s.Health == ServiceHealth.Faulted)
            ? $" ({(broken.Count > 1 ? "супервизор перестал их поднимать" : "супервизор перестал его поднимать")})"
            : string.Empty;

        return $"{(broken.Count > 1 ? "Движки" : "Движок")} {names} {verb}{faulted} — {(broken.Count > 1 ? "их части" : "его части")} обхода нет. Подробности ниже.";
    }

    /// <summary>Что несёт трафик при исправном обходе — по выбранному режиму.</summary>
    private static string Carrying(AppSettings settings) => WorkModes.Of(settings.Engines) switch
    {
        WorkMode.Desync => "Трафик идёт через десинк.",
        WorkMode.Tunnel => "Трафик идёт через туннель.",
        WorkMode.Route => "Маршруты — через туннель, остальное — напрямую, закрытое чинит десинк.",
        _ => "Движки работают.",
    };

    /// <summary>
    /// Показывает ход запуска. Возвращает <c>false</c>, когда показывать
    /// больше нечего и состояние пора рисовать обычным путём.
    /// </summary>
    /// <remarks>
    /// Умерший движок здесь не считается концом: супервизор его перезапустит,
    /// и до тех пор запуск продолжается. Концом считается только «сдался»
    /// либо исчерпанное терпение — иначе окно объявляло бы отказ, пока
    /// внизу ещё идут попытки.
    /// </remarks>
    private bool ShowStarting(AppSettings settings, SupervisorState? state, bool running, bool singBoxUp, bool coming)
    {
        var since = _startingSince!.Value;
        var services = running ? state!.Services : [];

        bool ready = services.Count > 0 && services.All(s => s.Health == ServiceHealth.Healthy);
        bool gaveUp = services.Any(s => s.Health == ServiceHealth.Faulted);

        // Ни состояния, ни живого супервизора, ни запуска в очереди — значит,
        // он ушёл, не подняв движков. Ждать тут все 90 с незачем.
        bool gone = !running && !coming && DateTimeOffset.Now - since > TimeSpan.FromSeconds(3);

        if (ready || gaveUp || gone || DateTimeOffset.Now - since > StartupPatience)
        {
            EndStarting();
            return false;
        }

        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;

        var seconds = (int)(DateTimeOffset.Now - since).TotalSeconds;
        ShowHero(HomeState.Starting, $"{DescribePhase(settings, running, singBoxUp, services)} — {seconds} с");

        // Каждый тик продлевает навязанный окну «Запуск»: срок у него свой, а запуск идёт до 90 с.
        Pulse();

        // Пока супервизор не дописал состояние, движки показываются как
        // «запускается». Прежде список опустошался, и они пропадали ровно
        // на те десятки секунд, когда на них и смотрят: раздел отвечал «их
        // нет» на вопрос «поднимаются ли они».
        SetEngines(services.Count > 0
            ? services.Select(Row).ToList()
            : Planned(settings, "запускается", HomeState.Starting));

        return true;
    }

    /// <summary>
    /// Чем занят запуск прямо сейчас.
    /// </summary>
    /// <remarks>
    /// До появления состояния фаза определяется по живым процессам, а не по
    /// файлу: супервизор пишет состояние впервые лишь после того, как поднял
    /// все службы, — то есть ровно после окончания промежутка, который здесь
    /// и показывается. Файл в это время либо отсутствует, либо остался от
    /// прошлого запуска.
    /// </remarks>
    private static string DescribePhase(
        AppSettings settings,
        bool running,
        bool singBoxUp,
        IReadOnlyList<ServiceState> services)
    {
        if (!running)
        {
            if (!singBoxUp)
                return "Собираем конфиг и поднимаем супервизор";

            // Проверка прохода трафика уходит в сеть и занимает основную часть
            // ожидания, поэтому названа отдельно: иначе эти секунды выглядят
            // как необъяснённая пауза.
            return settings.VerifyTraffic
                ? "Туннель поднимается, проверяем проход трафика"
                : "Туннель поднимается";
        }

        var singBox = services.FirstOrDefault(s => s.Name == "sing-box");

        return singBox?.Health switch
        {
            ServiceHealth.Degraded => "Туннель поднят, проверка ещё не прошла",
            ServiceHealth.Dead => "Туннель не поднялся, идёт перезапуск",
            _ => "Движки поднимаются",
        };
    }

    private static bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);

        // Каждый Process держит системный дескриптор, а опрос идёт раз
        // в секунду: без освобождения они копятся всё время запуска.
        foreach (var process in found)
            process.Dispose();

        return found.Length > 0;
    }

    private void BeginStarting()
    {
        _startingSince = DateTimeOffset.Now;
        _refresh.Interval = StartingInterval;

        Pulse();
        Update();
    }

    private void EndStarting()
    {
        _startingSince = null;
        _refresh.Interval = CalmInterval;

        Pulse();
    }

    /// <summary>
    /// Слою частиц в окне — запуск идёт или кончился (успехом или ошибкой) и
    /// настоящая скорость приёма; <c>null</c> — замера нет.
    /// </summary>
    private void Pulse() =>
        FieldPulse(_startingSince is not null || _building ? FieldMode.Starting : null, _mbps);

    private void FieldPulse(FieldMode? mode, double? mbps) =>
        (Window.GetWindow(this) as MainWindow ?? _host)?.FieldPulse(mode, mbps);

    /// <summary>Окно, в котором раздел показан (с Loaded): им гасится слой при уходе.</summary>
    private MainWindow? _host;

    /// <summary>Последний настоящий замер приёма, Мбит/с; <c>null</c> — не меряется.</summary>
    private double? _mbps;

    /// <summary>Идёт сборка конфига после «Запустить»: состояние — «Запуск», чтение с диска его не перебивает.</summary>
    private bool _building;

    /// <summary>Ниже этой ширины раздела — компактная раскладка (880x560).</summary>
    private const double CompactBelow = 980;

    private bool _compact;

    /// <summary>
    /// Состояние на плашке: слово, цвет, знак, тинт и дорожки — одним местом.
    /// </summary>
    /// <remarks>
    /// Слово и знак меняются только при смене состояния (слово проявляется 200 мс,
    /// знаки — кроссфейд 160, при «Работает» один раз расходится кольцо), а
    /// пояснение и время обновляются на каждом тике. Движение — через Motion.Enabled.
    /// </remarks>
    private void ShowHero(HomeState state, string hint)
    {
        StateHint.Text = hint;

        if (!_homeShown || state != _home)
        {
            bool animate = _homeShown && IsLoaded && Motion.Enabled;
            var key = state switch
            {
                HomeState.Starting => "Accent",
                HomeState.Running => "Success",
                HomeState.Degraded => "Warn",
                HomeState.Failed => "Danger",
                _ => "Muted",
            };

            _home = state;
            _homeShown = true;

            StateLine.Text = state switch
            {
                HomeState.Starting => "Запуск",
                HomeState.Running => "Работает",
                HomeState.Degraded => "Работает частично",
                HomeState.Failed => "Сбой",
                _ => "Выключен",
            };
            StateLine.SetResourceReference(TextBlock.ForegroundProperty, key);
            Fade(StateLine, 0, 1, 200, animate);

            // Тонкий тинт цвета состояния; у «Выключен» его нет.
            Tint.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
            Tint.Visibility = state == HomeState.Off ? Visibility.Collapsed : Visibility.Visible;

            // Знак: приходящий проявляется, уходящий гаснет.
            var (front, back) = _markFrontIsA ? (MarkA, MarkB) : (MarkB, MarkA);
            back.Tag = state.ToString();
            Fade(back, 0, 1, 160, animate);
            Fade(front, 1, 0, 160, animate);
            _markFrontIsA = !_markFrontIsA;

            FooterMark.Tag = state.ToString();
            ShowLanes();

            if (state == HomeState.Running && animate)
                Burst();
        }

        ShowUptime();
    }

    /// <summary>
    /// Только для снимков экрана: показывает состояние плашки и трасс без поднятых
    /// движков (тест называет его сам). Опрос гасится, чтобы настоящее не перебило.
    /// </summary>
    internal void Present(HomeState state, string hint, HomeState desync, HomeState tunnel)
    {
        _refresh.Stop();
        _clock.Stop();

        bool on = state != HomeState.Off;
        StartButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _runningSince = state is HomeState.Running or HomeState.Degraded or HomeState.Failed ? DateTimeOffset.Now.AddSeconds(-768) : null;

        ShowHero(state, hint);
        SetEngines([new("winws2", desync.ToString(), string.Empty, desync), new("sing-box", tunnel.ToString(), string.Empty, tunnel)]);
    }

    private HomeState _home;
    private bool _homeShown;
    private bool _markFrontIsA = true;

    /// <summary>Значение сразу в конечном виде; при <paramref name="animate"/> — с проявлением от <paramref name="from"/>.</summary>
    private static void Fade(UIElement element, double from, double to, int ms, bool animate)
    {
        element.Opacity = to;

        if (animate)
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            });
        }
    }

    /// <summary>Разовое кольцо у знака «Работает»: прозрачность .5 → 0, масштаб 1 → 1.8 за 600 мс.</summary>
    private void Burst()
    {
        var time = TimeSpan.FromMilliseconds(600);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 0, time) { FillBehavior = FillBehavior.Stop });

        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            RingScale.BeginAnimation(axis, new DoubleAnimation(1, 1.8, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// Компактная раскладка: плашка на всю ширину, «Компоненты» двумя плитками
    /// под ней, числа сети 2x2, графики друг под другом, слово 20, кнопки 36.
    /// </summary>
    private void SetCompact(bool compact)
    {
        if (compact == _compact)
            return;

        _compact = compact;

        Grid.SetColumnSpan(HeroCell, compact ? 3 : 1);
        Grid.SetRow(ComponentsCell, compact ? 1 : 0);
        Grid.SetColumn(ComponentsCell, compact ? 0 : 2);
        Grid.SetColumnSpan(ComponentsCell, compact ? 3 : 1);
        ComponentsCell.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);

        Chips.Columns = compact ? 2 : 1;
        DesyncChip.Margin = new Thickness(0, 0, compact ? 8 : 0, compact ? 0 : 8);

        // Узко: числа строкой под заголовком, по два; широко — в строке заголовка.
        NetMetrics.Columns = compact ? 2 : 4;
        Grid.SetRow(NetMetrics, compact ? 1 : 0);
        Grid.SetColumn(NetMetrics, compact ? 0 : 1);
        Grid.SetColumnSpan(NetMetrics, compact ? 3 : 1);
        NetMetrics.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);

        foreach (var cell in new FrameworkElement[] { NetDownCell, NetUpCell, NetPingCell, NetLossCell })
            cell.Margin = new Thickness(0, 0, 16, compact ? 4 : 0);

        ChartRowGap.Height = new GridLength(compact ? 8 : 0);
        Grid.SetColumnSpan(ThroughputChart, compact ? 3 : 1);
        Grid.SetRow(LatencyChart, compact ? 2 : 0);
        Grid.SetColumn(LatencyChart, compact ? 0 : 2);
        Grid.SetColumnSpan(LatencyChart, compact ? 3 : 1);

        // Слово 20, кнопки 36 — свои значения, а не стилевые 24 / 40.
        if (compact)
            StateLine.FontSize = 20;
        else
            StateLine.ClearValue(TextBlock.FontSizeProperty);

        foreach (var button in new Button[] { StartButton, StopButton, EngineMenuButton })
            button.Height = compact ? 36 : 40;
    }

    /// <summary>
    /// Время работы под словом: сколько работает обход, а при запуске — сколько
    /// идёт запуск. Остановленный обход времени не показывает.
    /// </summary>
    /// <remarks>
    /// Время начала — супервизора (SupervisorState.StartedAt), то есть то же,
    /// что «Запущено N мин назад» и строка в подвале: окно, открытое через
    /// час после запуска, покажет час, а не ноль.
    /// </remarks>
    private void ShowUptime()
    {
        var now = DateTimeOffset.Now;

        if (_startingSince is { } starting)
        {
            Uptime.Text = Clock(now - starting);
            UptimeBox.ToolTip = "Сколько идёт запуск";
        }
        else if (_runningSince is { } since)
        {
            Uptime.Text = Clock(now - since);
            UptimeBox.ToolTip = "Обход работает с " + since.ToString("HH:mm") + (since.Date != now.Date ? since.ToString(" dd.MM") : string.Empty);
        }
        else
        {
            UptimeBox.Visibility = Visibility.Collapsed;
            return;
        }

        UptimeBox.Visibility = Visibility.Visible;
    }

    /// <summary>С какого момента обход работает; <c>null</c> — не работает.</summary>
    private DateTimeOffset? _runningSince;

    /// <summary>Часы таймера: секунды должны идти, а опрос состояния — раз в две.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>«04:12:09», а больше суток — «2д 04:12:09».</summary>
    internal static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        var time = $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}д {time}" : time;
    }

    /// <summary>
    /// Строка внизу: состояние с длительностью и время сборки конфига туннеля.
    /// </summary>
    private void ShowFooter(SupervisorState? state, bool running, DateTime? configBuilt)
    {
        FooterState.Text = running && _startingSince is null
            ? $"{StateLine.Text} · {Span(DateTimeOffset.Now - state!.StartedAt)}"
            : StateLine.Text;

        FooterConfig.Text = configBuilt is { } at
            ? $"Конфиг туннеля собран {at:dd.MM.yyyy} в {at:HH:mm}"
            : string.Empty;
    }

    /// <summary>«23 дня, 4 часа», «4 ч 12 мин», «7 мин».</summary>
    internal static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            int days = (int)span.TotalDays;
            return $"{days} {Plural(days, "день", "дня", "дней")}, {span.Hours} {Plural(span.Hours, "час", "часа", "часов")}";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
            : $"{Math.Max(0, span.Minutes)} мин";
    }

    private static string Plural(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return many;

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Пресет, описание которого уже показано, — чтобы не читать файл на каждом тике.</summary>
    private string? _notedPreset = "\0";

    /// <summary>
    /// Первая фраза описания пресета — из его же файла.
    /// </summary>
    /// <remarks>
    /// Читается при смене пресета, а не каждые две секунды: разбор файла —
    /// сотни строк, а меняется выбор раз в неделю.
    /// </remarks>
    private void ShowPresetNote(string? preset)
    {
        if (preset == _notedPreset)
            return;

        _notedPreset = preset;
        PresetNote.Text = preset is null ? "Десинк без пресета не поднимается." : string.Empty;

        if (preset is null)
            return;

        try
        {
            if (Zephyron.Zapret.ZapretPaths.FindPreset(preset) is not { } file)
            {
                PresetNote.Text = "Файла пресета нет в папке presets.";
                return;
            }

            var description = new Zephyron.Zapret.PresetReader().Read([file]).FirstOrDefault()?.Description;

            PresetNote.Text = FirstSentence(description) ?? "Описания в файле пресета нет.";
        }
        catch (Exception)
        {
            PresetNote.Text = string.Empty;
        }
    }

    /// <summary>
    /// До первой точки, двоеточия или тире с пробелом после — дальше в описаниях
    /// пресетов начинаются подробности.
    /// </summary>
    /// <remarks>
    /// С пробелом, а не любая точка: в описаниях стоят версии и имена
    /// («v1.0.3», «www.facebook.com»), и по голой точке фраза рвалась бы на них.
    /// </remarks>
    internal static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        var end = System.Text.RegularExpressions.Regex.Match(text, @"[.:](\s|$)|\s—\s");

        return end.Success && end.Index > 0 ? text[..end.Index].TrimEnd() + "." : text;
    }

    /// <summary>
    /// Выпадающий список пресетов у «Изменить».
    /// </summary>
    /// <remarks>
    /// Список тот же и в том же порядке, что в «Десинке» (PresetNewness), и запись
    /// та же — PresetName. Последним пунктом — сам раздел: там видно, чем
    /// пресеты отличаются, а здесь только имена.
    /// </remarks>
    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetMenu, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string? chosen;

        try
        {
            chosen = AppSettings.Load(AppSettings.DefaultPath).PresetName;

            var presets = Zephyron.Zapret.PresetNewness.Order(
                new Zephyron.Zapret.PresetReader().Read(Zephyron.Zapret.ZapretPaths.PresetFiles),
                preset => Path.GetFileNameWithoutExtension(preset.FilePath),
                preset => preset.BuiltinVersion);

            foreach (var preset in presets)
            {
                var name = Path.GetFileNameWithoutExtension(preset.FilePath);
                var item = new MenuItem { Header = name, Tag = name };

                if (string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                    item.Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = (Brush)FindResource("Accent") };

                item.Click += (_, _) => ChoosePreset(name);
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Пресеты не читаются: " + ex.GetBaseException().Message);
            return;
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        var all = new MenuItem { Header = "Все пресеты — раздел «Десинк»" };
        all.Click += (_, _) => Open("desync");
        menu.Items.Add(all);

        menu.IsOpen = true;
    }

    private void ChoosePreset(string name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.Equals(settings.PresetName, name, StringComparison.OrdinalIgnoreCase))
                return;

            (settings with { PresetName = name }).Save(AppSettings.DefaultPath);

            Update();
            this.Offer($"Выбран пресет «{name}»");
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать выбор: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Движки, которые поднимутся при нынешней настройке.
    /// </summary>
    /// <remarks>
    /// Список тот же, что собирает супервизор: туннель — когда режим ведёт
    /// хоть что-то через VPN, десинк — когда выбран пресет. В режиме, где
    /// не запускается ничего, список пуст, и это верно: запускать нечего.
    /// </remarks>
    private static IReadOnlyList<EngineRow> Planned(AppSettings settings, string detail, HomeState mark)
    {
        var rows = new List<EngineRow>();

        if (settings.NeedsProxy)
            rows.Add(new EngineRow("sing-box", detail, string.Empty, mark));

        if (settings.NeedsDesync)
            rows.Add(new EngineRow("winws2", detail, string.Empty, mark));

        return rows;
    }

    private EngineRow Row(ServiceState service)
    {
        // Слова те же, что в консоли. Degraded — это «процесс жив, а проверка
        // не проходит»: самый коварный случай, и называть его «работает»
        // нельзя, иначе окно будет уверять в исправности молчащей трубы.
        // Слова — общие с треем и zy status (EngineHealth.Status): причина
        // надзора и время начала, а не «запущен, но не отвечает» (26.09).
        // Пока идёт запуск, не поднявшийся ещё движок — «запуск», а не оговорка или сбой;
        // «сдался» (Faulted) — сбой всегда.
        bool starting = _startingSince is not null;

        var mark = service.Health switch
        {
            ServiceHealth.Healthy => HomeState.Running,
            ServiceHealth.Degraded or ServiceHealth.Dead when starting => HomeState.Starting,
            ServiceHealth.Degraded => HomeState.Degraded,
            ServiceHealth.Dead or ServiceHealth.Faulted => HomeState.Failed,
            _ => HomeState.Off,
        };

        // Причина — только у того, что не работает: у работающего Status
        // говорит «работает, процесс N», а номер и так стоит строкой ниже.
        // У работающего — только примечание надзора («временная замена…»).
        var detail = service.Health == ServiceHealth.Healthy
            ? service.Remark ?? string.Empty
            : EngineHealth.Status(service);

        return new EngineRow(service.Name, EngineHealth.Word(service.Health), detail, mark, service.ProcessId);
    }

    /// <summary>
    /// То, что стоит знать до запуска.
    /// </summary>
    /// <remarks>
    /// Измерено на живых машинах и приходит молча: защитник со своим сетевым
    /// фильтром обесценивает десинк, ничего об этом не сообщая. Что Kaspersky
    /// вернул hosts к своему умолчанию, с 24.10 говорит только «Диагностика»
    /// (а ещё «Файл hosts» и проверка блокировок), не «Главная» — владелец:
    /// «предупреждение … прям на главной, а должно быть в диагностике».
    /// </remarks>
    private IReadOnlyList<EngineRow>? _engines;
    private IReadOnlyList<WarningRow>? _warnings;
    private DateTimeOffset _warningsAt = DateTimeOffset.MinValue;
    private bool _warningsBusy;

    /// <summary>Сколько держать проверку антивирусов, прежде чем повторить.</summary>
    private static readonly TimeSpan WarningsInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Карточки движков — только если что-то в них изменилось.
    /// </summary>
    /// <remarks>
    /// Прежде список собирался заново каждые две секунды: новый ItemsSource —
    /// это новые элементы со всеми шаблонами, и окно дёргалось раз в два
    /// тика даже на RTX 4060 (владелец, 24.09: «интерфейс подвисать может»).
    /// Строки — записи, и сравниваются по значению: одинаковые не трогаем.
    /// </remarks>
    private void SetEngines(IReadOnlyList<EngineRow> rows)
    {
        // Те же строки — знаки не трогаем, но трассам общее состояние могло смениться.
        if (_engines is not null && _engines.SequenceEqual(rows))
        {
            ShowLanes();
            return;
        }

        _engines = rows;
        ShowEngines(rows);
    }

    private void ShowEngines(IReadOnlyList<EngineRow> rows)
    {
        // Движки — в карточке «Режим» (04.10), оба при любом режиме (владелец:
        // «пусть эти движки будут и в десинке, и в туннеле»): не нужный режиму
        // скажет «выключен», как в трее, а высота карточки от режима не зависит.
        var desync = rows.FirstOrDefault(r => r.Name == "winws2");
        var tunnel = rows.FirstOrDefault(r => r.Name == "sing-box");

        ShowEngine(desync, DesyncMark, DesyncState, DesyncPid, _desyncIdle);
        ShowEngine(tunnel, TunnelMark, TunnelState, TunnelPid);

        ShowLanes();
    }

    /// <summary>Трассы — тем же состоянием, что знаки движков; нет движка — «выключен».</summary>
    private void ShowLanes() => Lanes.Show(
        _home,
        _engines?.FirstOrDefault(r => r.Name == "winws2")?.Mark ?? HomeState.Off,
        _engines?.FirstOrDefault(r => r.Name == "sing-box")?.Mark ?? HomeState.Off);

    /// <summary>
    /// Десинк включён, а не поднимается: всё забрал туннель.
    /// </summary>
    /// <remarks>
    /// Так при «Игнорировать исключения» (<see cref="EngineChoice.DesyncRuns"/>).
    /// 19.10 в чате у XesSarix «Гибрид» был выбран, а десинк стоял «выключен»,
    /// и он спросил почему: причина была только красной строкой под карточкой.
    /// </remarks>
    private string? _desyncIdle;

    /// <summary>Строка состояния движка в его карточке.</summary>
    /// <remarks>
    /// Нет строки — движок в этот запуск не поднимали: выключен или нет
    /// подписки. «Остановлен» здесь соврало бы, что он был.
    /// </remarks>
    /// <param name="idle">Почему не поднимали включённый; <c>null</c> — выключен.</param>
    private void ShowEngine(EngineRow? row, Control mark, TextBlock state, TextBlock pid, string? idle = null)
    {
        if (row is null)
        {
            mark.Tag = nameof(HomeState.Off);
            state.Text = idle is null ? "выключен" : "не нужен";
            pid.Text = idle is null ? string.Empty : "всё через VPN";
            pid.ToolTip = idle;
            return;
        }

        mark.Tag = row.Mark.ToString();
        state.Text = row.Word;

        // Номер процесса — чтобы найти движок в диспетчере задач (владелец,
        // 26.09). У движка не в порядке на его месте причина надзора: она
        // важнее номера. В строке плитки места мало — целиком она в подсказке.
        pid.Text = row.Detail.Length > 0
            ? row.Detail
            : row.ProcessId is { } id ? $"№{id}" : string.Empty;
        pid.ToolTip = row.Detail.Length > 0
            ? row.Detail
            : row.ProcessId is { } shown ? $"Процесс {shown} — так его ищут в диспетчере задач" : null;
    }

    /// <summary>
    /// Предупреждения — в фоне и раз в полминуты, а не в потоке окна каждые две секунды.
    /// </summary>
    /// <remarks>
    /// Проверка антивирусов перебирает все процессы системы. За две секунды
    /// это не меняется, а делалось в потоке окна на каждом тике.
    /// </remarks>
    private void ShowWarnings(AppSettings settings)
    {
        if (_warningsBusy || DateTimeOffset.Now - _warningsAt < WarningsInterval)
            return;

        _warningsBusy = true;
        bool needsDesync = settings.NeedsDesync;
        bool hybrid = WorkModes.Of(settings.Engines) == WorkMode.Route;

        _ = Task.Run(() => CollectWarnings(needsDesync, hybrid)).ContinueWith(task =>
        {
            _warningsBusy = false;
            _warningsAt = DateTimeOffset.Now;

            if (task.IsCompletedSuccessfully)
                SetWarnings(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Предупреждения о защитниках — из фоновой проверки раз в полминуты.</summary>
    private IReadOnlyList<WarningRow> _guardWarnings = [];

    /// <summary>Карточка о резолвере движка — из состояния надзора, каждые две секунды.</summary>
    private WarningRow? _dnsWarning;

    private void SetWarnings(IReadOnlyList<WarningRow> rows)
    {
        _guardWarnings = rows;
        ShowAllWarnings();
    }

    private void SetDnsWarning(WarningRow? row)
    {
        if (row == _dnsWarning)
            return;

        _dnsWarning = row;
        ShowAllWarnings();
    }

    /// <summary>Резолвер — первым: без имён не работает ничего, и это важнее защитника.</summary>
    private void ShowAllWarnings()
    {
        IReadOnlyList<WarningRow> rows = _dnsWarning is { } dns ? [dns, .. _guardWarnings] : _guardWarnings;

        if (_warnings is not null && _warnings.SequenceEqual(rows))
            return;

        _warnings = rows;
        Warnings.ItemsSource = rows;
    }

    /// <summary>Карточка о резолвере (SingBoxService, DnsLadder): что с ним и что делать.</summary>
    private static WarningRow DnsWarning(ServiceState service)
    {
        var notice = service.DnsNotice!;

        return service.DnsDown
            ? new WarningRow("Резолвер движка не отвечает",
                char.ToUpper(notice[0]) + notice[1..] + ". Выберите другой резолвер на вкладке DNS и перезапустите движки.")
            : new WarningRow("Резолвер движка не отвечает — работает запасной путь",
                char.ToUpper(notice[0]) + notice[1..] + ". Сайты открываются; вернётся сам, как только резолвер ответит. "
                + "Чтобы не зависеть от него, выберите другой на вкладке DNS.");
    }

    private static IReadOnlyList<WarningRow> CollectWarnings(bool needsDesync, bool hybrid)
    {
        var rows = new List<WarningRow>();

        // «Гибрид» решает, что через VPN, по именам. Браузер со своим
        // шифрованным DNS спрашивает имена мимо системы — наш резолвер их
        // не видит, правило не срабатывает, и сайт, которому нужен туннель,
        // идёт напрямую и не открывается. Со стороны это неотличимо от
        // «маршрут не работает», а лечится одной галочкой в браузере.
        if (hybrid)
        {
            foreach (var browser in BrowserDns.Scan())
            {
                rows.Add(new WarningRow(
                    $"{browser.Browser} спрашивает имена сам, мимо системы",
                    "В «Гибриде» путь имени (через VPN или напрямую) решает наш резолвер, "
                    + "а такой браузер его не спрашивает: сайт, которому нужен туннель, "
                    + "пойдёт напрямую и не откроется. Выключите свой DNS в браузере: "
                    + browser.Setting + "."));
            }
        }

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0 && needsDesync)
        {
            rows.Add(new WarningRow(
                $"Работает {string.Join(", ", guards.Select(g => g.Name))}",
                "Его сетевой фильтр встаёт на тот же слой, что и наш, проверка защищённых "
                + "соединений переустанавливает TLS своим клиентом, а WinDivert он помечает "
                + "как RiskTool и может увезти в карантин. Мы этого отсюда не видим; если "
                + "обход не помогает без внятной причины — отключите защиту на десять минут "
                + "и повторите."));
        }

        return rows;
    }

    /// <summary>Причины упавших движков, по строке на движок: «Десинк: …».</summary>
    private static string Failures(IEnumerable<ServiceState> services) => string.Join("\n", services
        .Where(s => s.Health is ServiceHealth.Dead or ServiceHealth.Faulted && s.LastError is { Length: > 0 })
        .Select(s => $"{EngineTitle(s.Name)}: {s.LastError}"));

    private static string EngineTitle(string name) => name switch
    {
        "winws2" => "Десинк",
        "sing-box" => "Туннель",
        _ => name,
    };

    private void ShowProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    private static string Ago(DateTimeOffset since)
    {
        var passed = DateTimeOffset.Now - since;

        return passed switch
        {
            { TotalMinutes: < 1 } => "только что",
            { TotalHours: < 1 } => $"{(int)passed.TotalMinutes} мин назад",
            { TotalDays: < 1 } => $"{(int)passed.TotalHours} ч назад",
            _ => $"{(int)passed.TotalDays} дн назад",
        };
    }

    /// <summary>
    /// Пять режимов с ценой каждого.
    /// </summary>
    /// <remarks>
    /// Цена названа в самом описании, а не выясняется опытом: «всё через VPN
    /// без исключений» ломает отечественные сервисы, и узнавать об этом
    /// по неработающим госуслугам человек не должен.
    /// </remarks>
    private void ShowModes(AppSettings settings)
    {
        var engines = settings.Engines;
        var mode = WorkModes.Of(engines);

        ModeDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        ModeTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        ModeRouteName.Text = WorkModes.Name(WorkMode.Route);

        // Пояснение — выбранного режима. «Без исключений» — настройка внутри
        // «Туннеля»: включённая, она меняет смысл режима, и молчать о ней нельзя.
        // У «Гибрида» она отменяет само пояснение: маршруты не решают ничего,
        // всё уходит в VPN, а десинк не поднимается (EngineChoice.DesyncRuns).
        ModeText.Text = mode is not { } chosen
            ? "Режим не выбран — движки не поднимутся."
            : chosen == WorkMode.Route && engines.IgnoreExclusions
                ? "Сейчас без исключений: всё идёт через VPN, маршруты и десинк не действуют."
                : WorkModes.Explain(chosen)
                    + (chosen == WorkMode.Tunnel && engines.IgnoreExclusions
                        ? " Сейчас без исключений: в VPN идёт и «напрямую»."
                        : string.Empty);
        ModeText.ToolTip = ModeText.Text;

        // Метка у заголовка: какие движки стоят за режимом — их имена,
        // как в журнале и в диспетчере задач.
        ModeTag.Text = mode switch
        {
            WorkMode.Desync => "desync · winws2",
            WorkMode.Tunnel => engines.IgnoreExclusions ? "tunnel · sing-box · без исключений" : "tunnel · sing-box",
            WorkMode.Route => engines.DesyncRuns ? "hybrid · winws2 + sing-box" : "hybrid · sing-box",
            _ => "не выбран",
        };

        _desyncIdle = engines.Desync && !engines.DesyncRuns
            ? "Включено «Игнорировать исключения» в настройках туннеля: всё идёт через VPN, "
                + "и десинку чинить нечего. Выключите его — заработают маршруты и десинк."
            : null;

        if (_engines is { } shown)
            ShowEngines(shown);

        // «Ничего не поднято» режимом не считается — ни один сегмент не выбран.
        ModeDesync.IsChecked = mode == WorkMode.Desync;
        ModeTunnel.IsChecked = mode == WorkMode.Tunnel;
        ModeRoute.IsChecked = mode == WorkMode.Route;

        // Режиму с туннелем без выхода везти некуда: «Туннель» не поднимет
        // ничего, «Гибрид» сработает как «Десинк». Сказать об этом здесь,
        // у выбора, а не узнавать по неработающему VPN.
        var complaint = engines.Complaint
            ?? (engines.Tunnel && !settings.HasTunnelExit
                ? (engines.Desync
                    ? $"VPN не задан — «{WorkModes.RouteName}» работает как «{WorkModes.Name(WorkMode.Desync)}». Подписка или WARP — на вкладке VPN."
                    : $"Для «{WorkModes.Name(WorkMode.Tunnel)}» нужна подписка или WARP — вкладка VPN.")
                : null);

        EnginesLine.Text = complaint ?? string.Empty;
        EnginesLine.Visibility = complaint is null ? Visibility.Collapsed : Visibility.Visible;

        // Автозапуск поднимает ровно это. Сказано здесь же, где задано:
        // иначе про связь пришлось бы догадываться, а догадка — источник
        // того самого «трей запускается, а движки нужно поднимать кнопкой».
        AutostartRaises.Text = mode is { } raised
            ? $"При входе в Windows поднимется режим «{WorkModes.Name(raised)}»."
            : "При входе в Windows ничего не поднимется: режим не выбран.";
    }

    /// <summary>
    /// Переключает движок.
    /// </summary>
    /// <remarks>
    /// Через <see cref="AppSettings.With(EngineChoice)"/>: тот пишет заодно
    /// и выведенный режим, на языке которого говорят конфиг движка, отчёты
    /// и консоль. Оставленный отставшим, режим развёл бы показания.
    /// </remarks>
    private void Choose(Func<EngineChoice, EngineChoice> change)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings.With(change(settings.Engines));

            next.Save(AppSettings.DefaultPath);

            ShowModes(next);
            Update();

            this.Offer("Режим: " + next.Engines.Describe());
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Выбран режим — выключатели под него (Rules.WorkModes) и предложение перезапустить.</summary>
    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WorkMode>(tag, out var mode))
            Choose(c => WorkModes.Apply(c, mode));
    }

    /// <summary>Что планировщик ответил в прошлый раз — на время работы окна.</summary>
    private static (bool Installed, bool Stale)? _autostart;

    /// <remarks>
    /// Планировщик спрашивается в фоне, а до ответа показывается прошлый.
    /// Замер 28.09: два запуска schtasks — «заведена ли» и «не устарела ли» —
    /// 73–91 мс на главном потоке при каждом открытии «Главной», и это была
    /// большая часть паузы, о которую спотыкалась анимация раздела.
    /// </remarks>
    private async void ShowAutostart()
    {
        if (_autostart is { } known)
        {
            ApplyAutostart(known);
        }
        else
        {
            AutostartValue.Text = "Задача планировщика: проверяю…";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
        }

        try
        {
            var fresh = await Task.Run(() =>
            {
                bool installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);
                return (Installed: installed, Stale: installed && IsStale());
            });

            _autostart = fresh;
            ApplyAutostart(fresh);
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "Задача планировщика не читается.";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
            ShowProblem("Планировщик не отвечает: " + ex.GetBaseException().Message);
        }
    }

    private void ApplyAutostart((bool Installed, bool Stale) state)
    {
        bool installed = state.Installed;

        AutostartValue.Visibility = Visibility.Collapsed;
        AutostartSwitch.IsChecked = installed;
        AutostartSwitch.IsEnabled = true;

        if (state.Stale)
        {
            AutostartValue.Text = "Задача устарела — выключите и включите снова.";
            AutostartValue.Visibility = Visibility.Visible;

            ShowProblem(
                "Задача автозапуска осталась от прежней версии и запускает не то, "
                + "что нужно: до 0.5.0 это была консольная программа, которой в поставке "
                + "больше нет. Выключите автозапуск и включите снова — это два щелчка.");
        }
    }

    /// <summary>
    /// Запускает ли задача не ту программу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задача переживает обновление, а её команда — нет: она записана внутрь
    /// задачи целиком, путём и ключами. До 0.5.0 автозапуск поднимал
    /// zephyron.exe, которого в поставке уже нет, и «заведена» при мёртвой
    /// команде — худший из возможных ответов: обход при входе не поднимется,
    /// а окно скажет, что всё в порядке.
    /// </para>
    /// <para>
    /// Сверяется имя файла, а не путь целиком, и не ключи. Ключи менялись
    /// и ещё будут меняться, а запуск исчезнувшей программы — беда другого
    /// порядка. Путь целиком не годится: он может содержать кириллицу,
    /// а schtasks при перенаправлении отвечает однобайтовой OEM — имя же
    /// латинское и переживает любую из них.
    /// </para>
    /// </remarks>
    private static bool IsStale()
    {
        try
        {
            // Кодировка не задаётся намеренно. Заданная Unicode здесь уже
            // стояла и всё ломала: schtasks пишет однобайтовый текст, а тот,
            // прочитанный парами байт, превращался в кашу, не содержащую
            // вообще ничего, — и всякая задача объявлялась устаревшей.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/query", "/tn", AutostartTask.DefaultTaskName, "/xml" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var xml = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (xml.Length == 0)
                return false;

            return Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name
                && !xml.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Не прочиталось — молчим: пугать задачей, о которой мы ничего
            // не выяснили, хуже, чем не сказать.
            return false;
        }
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        // Задача поднимает эту же программу в роли супервизора. Прежде она
        // звала консольную рядом, и без неё автозапуск молча не работал.
        var exe = Environment.ProcessPath;

        if (exe is null)
        {
            ShowProblem("Не удалось определить путь к программе — задача не заведена.");
            return;
        }

        try
        {
            if (AutostartTask.IsInstalled(AutostartTask.DefaultTaskName))
            {
                var (removed, output) = AutostartTask.Remove(AutostartTask.DefaultTaskName);

                if (!removed)
                    ShowProblem("Не удалось убрать автозапуск: " + output);
            }
            else
            {
                var (ok, output) = AutostartTask.Install(new AutostartOptions
                {
                    TaskName = AutostartTask.DefaultTaskName,
                    ExecutablePath = exe,

                    // Задача поднимает интерфейс в трей, а движки он заводит
                    // сам — теми же ключами, что и кнопка «Запустить». Прежде
                    // задача поднимала один супервизор, и при входе не было
                    // ни значка, ни способа остановить обход, кроме как
                    // открыть программу заново.
                    Arguments = TrayIcon.Switch,
                    WorkingDirectory = Path.GetFullPath("."),
                    // SID, а не имя: см. AutostartTask.CurrentUserId.
                    UserId = AutostartTask.CurrentUserId(),
                });

                if (!ok)
                    ShowProblem("Не удалось завести автозапуск: " + output);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Планировщик отказал: " + ex.GetBaseException().Message);
        }

        // Задачу только что завели или убрали — запомненный ответ устарел.
        _autostart = null;
        ShowAutostart();
    }

    /// <summary>
    /// Собирает конфиг и поднимает движки.
    /// </summary>
    /// <remarks>
    /// Сборка идёт при каждом запуске, как в меню консоли. Без неё окно
    /// поднимало движки с тем конфигом, что лежал на диске: смена сервера,
    /// правки маршрутов и переключение режима показывались новыми, а до
    /// туннеля не доходили, пока конфиг не соберут отдельно.
    /// </remarks>
    /// <summary>Ctrl+Enter: та кнопка, что сейчас на виду, — «Запустить» или «Остановить».</summary>
    internal void Toggle()
    {
        if (StartButton is { IsVisible: true, IsEnabled: true })
            OnStart(StartButton, new RoutedEventArgs());
        else if (StopButton is { IsVisible: true, IsEnabled: true })
            OnStop(StopButton, new RoutedEventArgs());
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        _building = true;
        ShowHero(HomeState.Starting, "Собираю конфиг: читаю подписку и правила.");
        Pulse();

        var outcome = await EngineControl.StartAsync("кнопка «Запустить» на «Главной»", CancellationToken.None);

        _building = false;
        StartButton.IsEnabled = true;

        if (!outcome.Ok)
        {
            Pulse();
            ShowProblem(outcome.Message);
            Update();
            return;
        }

        // Прежде кнопки просто гасли на три секунды. Этого хватало, пока
        // запуск был мгновенным; с проверкой прохода трафика он занимает
        // десятки секунд, и кнопки оживали посреди подъёма, показывая
        // «остановлено» у ещё запускающегося движка.
        BeginStarting();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        EndStarting();

        StopButton.IsEnabled = false;
        StateHint.Text = "Останавливаю движки…";

        await EngineControl.StopAsync("кнопка «Остановить» на «Главной»", CancellationToken.None);

        StopButton.IsEnabled = true;
        Update();
    }


    /// <summary>
    /// «На всю ширину» (Оформление, 01.10): без предела у содержимого и без колонки под арт.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Фон тогда — как у прочих вкладок: своя картинка «Главной» прячется,
    /// подложка становится прозрачной (Chrome), и видна картинка окна под
    /// затемнением темы. Владелец 01.10: растянули — «обои темы не должны
    /// сдвигаться вправо».
    /// </para>
    /// <para>
    /// Своя картинка пересчитывается только по размеру своего слоя, а он
    /// при смене колонок не меняется: стоило лишь обнулить колонку, и арт
    /// оставался на месте, рассчитанном под неё, — сдвинутым вправо.
    /// </para>
    /// </remarks>
    private void ShowWidth(AppSettings settings)
    {
        // Тема тоже может попросить всю ширину — NyaView (08.10): её видео
        // в узкой колонке справа — размытое пятно (Theme.HomeFullWidth).
        if (settings.HomeFullWidth || Themes.WideHome)
        {
            Layout(column: double.PositiveInfinity, page: double.PositiveInfinity, art: false);
            SetResourceReference(BackgroundProperty, "Chrome");
        }
        else if (HasSideArt() && TryFindResource(HomePlacement.Key) is HomePlacement placed)
        {
            // Уложенная руками картинка: карточки слева (арт справа, как до 0.20), по центру
            // или справа (арт слева) — как выбрано в редакторе темы (HomeSide, 28.10).
            if (placed.Side == HomeSide.Center)
                Layout(column: double.PositiveInfinity, page: (double)FindResource("ContentMax"), art: true);
            else
                Layout(column: 940, page: double.PositiveInfinity, art: true, artLeft: placed.Side == HomeSide.Right);

            SetResourceReference(BackgroundProperty, "Backdrop");

            // Слой мог прятаться — разложить заново, когда станет известен размер.
            Dispatcher.BeginInvoke(PlaceArt, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            // Без арта (с 0.16, встроенные темы) — по центру окна, как в макете:
            // колонке справа нечего показывать, и страница не жмётся влево.
            // Подложка прозрачная — под ней сетка и свечения окна.
            Layout(column: double.PositiveInfinity, page: (double)FindResource("ContentMax"), art: false);
            SetResourceReference(BackgroundProperty, "Chrome");
        }
    }

    /// <summary>Раскладка страницы: предел колонки содержимого, предел самой страницы и колонка под арт.</summary>
    private void Layout(double column, double page, bool art, bool artLeft = false)
    {
        // Колонка под арт — только при пределе карточек (сторона «слева» или «справа»);
        // «справа» — колонки меняются местами: слева арт, справа страница.
        bool side = art && !double.IsInfinity(column);
        _artLeft = side && artLeft;

        var content = _artLeft ? ArtColumn : ContentColumn;
        var rest = _artLeft ? ContentColumn : ArtColumn;

        content.Width = new GridLength(100, GridUnitType.Star);
        content.MinWidth = 560;
        content.MaxWidth = column;
        rest.Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        rest.MinWidth = 0;
        rest.MaxWidth = double.PositiveInfinity;

        ArtLayer.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(PageScroller, _artLeft ? 1 : 0);
        Grid.SetColumnSpan(PageScroller, side ? 1 : 2);

        // Только Stretch: с пределом WPF и так ставит страницу по центру, но во всю
        // ширину предела. Center сжимал её до ширины содержимого, и «Главная»
        // меняла ширину вместе с текстом состояния — прыгала при «Выключен» →
        // «Запуск» → «Работает» и переносила карточки так, что низ уходил за край (26.10).
        Page.MaxWidth = page;
    }

    /// <summary>Есть ли у темы картинка или живой фон для колонки справа.</summary>
    private bool HasSideArt() =>
        TryFindResource("SideArt") is ImageBrush { ImageSource: not null } || BackdropVideo.Current is not null;
}
