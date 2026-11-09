using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Supervisor;

namespace Zephyron.Gui;

/// <summary>Что показать в меню трея — снимок состояния движков.</summary>
/// <param name="Starting">
/// Надзор поднят, а движки ещё не готовы. До 04.10 трей этого не знал:
/// файл состояния появляется, лишь когда движки подняты (до 20 с), и всё
/// это время меню писало «Остановлено» и снова предлагало «Запустить» —
/// владелец нажимал по нескольку раз.
/// </param>
/// <param name="Stopping">Трей сам сейчас гасит движки.</param>
internal sealed record TrayStatus(
    bool Running,
    bool AllHealthy,
    ServiceHealth? Tunnel,
    ServiceHealth? Desync,
    bool Busy,
    bool Starting = false,
    bool Stopping = false,
    DateTimeOffset? Since = null)
{
    /// <summary>Сколько после подъёма ждать здоровья движков — как «Главная» (StatusView.StartupPatience).</summary>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

    public string Headline =>
        Stopping ? "Выключается…"
        : Busy && Running ? "Стартует заново…"
        : Starting ? "Стартует…"
        : !Running ? "Выключено"
        : AllHealthy ? "Работает"
        : Tunnel == ServiceHealth.Degraded ? "Выходы VPN молчат" : "Работает не полностью";

    /// <summary>Из файла состояния надзора — того же, что читает «Главная», — и из живого надзора.</summary>
    public static TrayStatus Read(bool busy, bool stopping = false)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        ServiceHealth? Of(string name) => running
            ? state!.Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Health
            : null;

        bool healthy = running && EngineHealth.Bypass(state!).All(s => s.Health == ServiceHealth.Healthy);

        // Запуск идёт, пока надзор жив, а состояния нет, — и после, пока
        // движки не прошли проверку, но не дольше, чем ждёт «Главная»:
        // сдавшийся движок — уже не запуск.
        bool starting = !stopping && (running
            ? !healthy
              && EngineHealth.Bypass(state!).All(s => s.Health != ServiceHealth.Faulted)
              && DateTimeOffset.Now - state!.StartedAt < StartupPatience
            : busy || EngineControl.IsRunning);

        return new TrayStatus(running, healthy, Of("sing-box"), Of("winws2"), busy, starting, stopping, running ? state!.StartedAt : null);
    }

    /// <param name="tunnel">
    /// Это sing-box: у него «не отвечает» почти всегда значит мёртвые выходы
    /// подписки, и так и говорится (26.09), — «с оговорками» не говорило ничего.
    /// </param>
    public static string Describe(ServiceHealth? health, bool tunnel = false) => health switch
    {
        ServiceHealth.Healthy => "работает",
        ServiceHealth.Degraded => tunnel ? "выходы VPN молчат" : "не откликается",
        ServiceHealth.Dead => "аварийно остановился",
        ServiceHealth.Faulted => "не поднимается",
        ServiceHealth.Stopped => "выключен",

        // Службы нет в состоянии — её и не поднимали: выключена выключателем
        // на «Главной». «Остановлен» здесь соврал бы, что она была.
        _ => "выключен",
    };
}

/// <summary>
/// Меню значка в трее — карточка в стиле окна.
/// </summary>
/// <remarks>
/// <para>
/// Окно создаётся один раз и прячется, а не закрывается: открывают его часто,
/// а тема, шрифт и масштаб у него и так живые — всё на DynamicResource.
/// </para>
/// <para>
/// Прячется, когда теряет фокус, — как любое меню: щелчок мимо закрывает его.
/// </para>
/// </remarks>
public partial class TrayMenu : Window
{
    private readonly Action _toggle;
    private readonly Action<WorkMode> _chooseMode;
    private readonly Action _showWindow;
    private readonly Action<bool> _quit;
    private readonly Action<string>? _open;
    private readonly Action? _restart;

    /// <param name="open">Открыть окно на разделе («vpn», «check»); <c>null</c> — просто показать окно.</param>
    /// <param name="restart">Перезапуск движков; <c>null</c> — кнопка ничего не делает.</param>
    internal TrayMenu(Action toggle, Action<WorkMode> chooseMode, Action showWindow, Action<bool> quit,
        Action<string>? open = null, Action? restart = null)
    {
        _toggle = toggle;
        _chooseMode = chooseMode;
        _showWindow = showWindow;
        _quit = quit;
        _open = open;
        _restart = restart;

        InitializeComponent();

        // В шапке «1.4.1», полный вид со сборкой — в подсказке.
        var full = MainWindow.Version();
        Version.Text = full.Split(' ')[0];
        Version.ToolTip = full;

        ModeDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        ModeTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        ModeRouteName.Text = WorkModes.Name(WorkMode.Route);

        _hold.Tick += (_, _) =>
        {
            _hold.Stop();
            _holding = false;
            Render(_last);
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                Overflow.Visibility = Visibility.Collapsed;
        };
    }

    private TrayStatus _last = new(false, false, null, null, false);
    private readonly System.Windows.Threading.DispatcherTimer _hold = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _holding;

    internal void Render(TrayStatus status)
    {
        _last = status;

        var state = TrayIcon.StateOf(status);

        Mark.Tag = state.ToString();
        Ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, state switch
        {
            FieldMode.Running => "Success",
            FieldMode.Starting or FieldMode.Degraded => "Warn",
            FieldMode.Failed => "Danger",
            _ => "Muted",
        });

        State.Text =
            status.Stopping ? "Выключаю…"
            : status.Busy && status.Running ? "Перезапускаю…"
            : status.Starting ? "Запускаю…"
            : !status.Running ? "Обход выключен"
            : state == FieldMode.Failed ? "Не запустился"
            : status.AllHealthy ? "Обход включён"
            : "Работает частично";

        // Строка под словом: аптайм или причина. Подсказке режима она уступает на 2.5 с (OnMode).
        if (!_holding)
            Uptime.Text =
                status.Busy || status.Starting ? "Проверяю движки"
                : !status.Running ? "Нажмите ⏻, чтобы включить"
                : state == FieldMode.Failed ? "Нажмите ⏻, чтобы повторить"
                : status.AllHealthy ? "Работает " + Span(status.Since)
                : status.Tunnel is { } t && t != ServiceHealth.Healthy ? "Туннель: " + TrayStatus.Describe(t, tunnel: true)
                : "Десинк: " + TrayStatus.Describe(status.Desync);

        Problem.Text = state == FieldMode.Failed
            ? "Ошибка: " + (status.Tunnel is ServiceHealth.Dead or ServiceHealth.Faulted
                ? "туннель — " + TrayStatus.Describe(status.Tunnel, tunnel: true)
                : "десинк — " + TrayStatus.Describe(status.Desync))
            : string.Empty;
        Problem.ToolTip = Problem.Text.Length > 0 ? Problem.Text : null;

        string name = status.Running || status.Starting ? "Выключить обход" : "Включить обход";
        bool spinner = status.Busy || status.Starting && !status.Running;

        // Запуск — спиннер E895 (без движения — статичный E712), кнопка занята.
        Toggle.Content = spinner ? (Motion.Enabled ? "" : "") : state == FieldMode.Failed ? "" : "";
        Toggle.Tag = state == FieldMode.Failed ? "Failed" : status.Running && state != FieldMode.Starting ? "On" : "Off";
        Toggle.IsEnabled = !status.Busy;
        System.Windows.Automation.AutomationProperties.SetName(Toggle, name);
        Toggle.ToolTip = name;
        Spin(status.Busy && Motion.Enabled);

        Restart.IsEnabled = status.Running && !status.Busy;

        ExitCard.Visibility = status.Running && !status.Busy ? Visibility.Visible : Visibility.Collapsed;

        if (ExitCard.Visibility == Visibility.Visible)
        {
            ShowExitMode(status);

            if (IsVisible)
                _ = ReadExitAsync();
        }

        ShowModes(status);
    }

    private static string Span(DateTimeOffset? since)
    {
        if (since is not { } at)
            return "—";

        var span = DateTimeOffset.Now - at;

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} д {span.Hours} ч"
            : span.TotalHours >= 1 ? $"{span.Hours} ч {span.Minutes} мин"
            : span.TotalMinutes >= 1 ? $"{span.Minutes} мин"
            : "меньше минуты";
    }

    /// <summary>Вращение значка запуска; без движения (Motion) — статичный значок.</summary>
    private void Spin(bool on)
    {
        if (on)
        {
            if (Toggle.RenderTransform is not RotateTransform rotate || rotate.IsFrozen)
                Toggle.RenderTransform = rotate = new RotateTransform();

            if (!_spinning)
            {
                rotate.BeginAnimation(RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2))
                    { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                _spinning = true;
            }
        }
        else if (_spinning && Toggle.RenderTransform is RotateTransform r)
        {
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            _spinning = false;
        }
    }

    private bool _spinning;

    /// <summary>Режим словами — по настройкам; движок упал — отдельной строкой Danger.</summary>
    private void ShowExitMode(TrayStatus status)
    {
        try
        {
            var engines = AppSettings.Load(AppSettings.DefaultPath).Engines;

            ExitMode.Text = engines.Tunnel && engines.Desync ? "Гибрид: туннель и десинк"
                : engines.Tunnel ? "Только туннель"
                : "Только десинк";

            bool down = engines.Tunnel && status.Tunnel is { } t && t != ServiceHealth.Healthy;

            ExitWarn.Text = down ? "Туннель не отвечает" : string.Empty;
            ExitWarn.Visibility = down ? Visibility.Visible : Visibility.Collapsed;

            if (!engines.Tunnel)
            {
                // Туннеля нет — ни сервера, ни задержки выдумывать нечего.
                ExitName.Text = "Без туннеля";
                ExitPing.Text = string.Empty;
                ExitFlag.Visibility = Visibility.Collapsed;
            }
            else if (_exitTag is null)
            {
                ExitName.Text = "—";
                ExitPing.Text = "—";
                ExitPing.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            }
        }
        catch (Exception)
        {
            ExitMode.Text = string.Empty;
        }
    }

    private string? _exitTag;
    private DateTime _exitAt = DateTime.MinValue;
    private bool _exitBusy;

    /// <summary>
    /// Через какой сервер идёт туннель и за сколько он отвечает — у самого движка.
    /// Не чаще раза в десять секунд и только у открытого меню.
    /// </summary>
    private async Task ReadExitAsync()
    {
        if (_exitBusy || DateTime.UtcNow - _exitAt < TimeSpan.FromSeconds(10))
            return;

        _exitBusy = true;
        _exitAt = DateTime.UtcNow;

        try
        {
            var (tag, ping) = await Task.Run(async () =>
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);

                if (!settings.Engines.Tunnel)
                    return ((string?)null, (TimeSpan?)null);

                var (server, _) = await Zephyron.Proxy.TunnelStatus.CurrentExitAsync(CancellationToken.None);

                if (server is null || server.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
                    || server.Equals("direct", StringComparison.OrdinalIgnoreCase))
                    return (server, null);

                using var api = new Zephyron.Proxy.ClashApi();

                return (server, await api.MeasureAsync(server, Zephyron.Core.Ping.UrlOf(settings), TimeSpan.FromSeconds(3), CancellationToken.None));
            });

            _exitTag = tag;

            if (tag is null)
                return;

            var (country, name) = Zephyron.Subscriptions.CountryTag.Split(tag);
            var flag = country.Length == 2 ? FlagImages.For(country) : null;

            ExitName.Text = name.Length > 0 ? name : tag;
            ExitName.ToolTip = tag;
            ExitFlag.Source = flag;
            ExitFlag.Visibility = flag is null ? Visibility.Collapsed : Visibility.Visible;

            // Меньше 80 мс — Success, до 200 — Warn, больше или без ответа — Danger.
            double? ms = ping?.TotalMilliseconds;

            ExitPing.Text = ms is { } m ? $"{m:0} мс" : "—";
            ExitPing.SetResourceReference(TextBlock.ForegroundProperty, ms is not { } v ? "Danger" : v < 80 ? "Success" : v <= 200 ? "Warn" : "Danger");
        }
        catch (Exception)
        {
            // Плашка — справка: не вышло, остаётся прежнее.
        }
        finally
        {
            _exitBusy = false;
        }
    }

    /// <summary>Выбранный режим — по настройкам, как бы он ни был выбран: здесь, на «Главной» или руками в файле.</summary>
    private void ShowModes(TrayStatus status)
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            Modes.IsEnabled = false;
            Modes.ToolTip = "Не удалось прочитать настройки: режим выбирается в окне";
            return;
        }

        var current = WorkModes.Of(settings.Engines);

        foreach (var (button, mode) in new[] { (ModeDesync, WorkMode.Desync), (ModeTunnel, WorkMode.Tunnel), (ModeRoute, WorkMode.Route) })
        {
            button.IsChecked = current == mode;
            button.IsEnabled = current == mode || WorkModes.CanChoose(settings, mode);
        }

        Modes.IsEnabled = !status.Busy;

        // Подсказка режима — в ToolTip, а не строкой: место в меню дорого.
        Modes.ToolTip = !settings.HasTunnelExit
            ? "Для режима «Туннель» нужна подписка или WARP во вкладке VPN"
            : status.Running
                ? "Выбор сразу перезапустит движки"
                : "Выбор сразу запустит движки";
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<WorkMode>(tag, out var mode))
        {
            _chooseMode(mode);

            // На 2.5 с строка под словом говорит, что идёт перезапуск.
            _holding = true;
            Uptime.Text = "Перезапускаю движки…";
            _hold.Stop();
            _hold.Start();
        }
    }

    /// <summary>Показывает меню у указателя, в пределах рабочей области его экрана.</summary>
    internal void PopUp()
    {
        // Размер — до показа: снимок под меню снимается раньше, чем меню
        // его закроет, а для снимка надо знать, где оно встанет.
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;

        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);

        // Точки экрана — в единицы WPF: на 150 % масштаба Windows это разное.
        double x = cursor.X / dpi.DpiScaleX;
        double y = cursor.Y / dpi.DpiScaleY;
        double left = area.Left / dpi.DpiScaleX;
        double top = area.Top / dpi.DpiScaleY;
        double right = area.Right / dpi.DpiScaleX;
        double bottom = area.Bottom / dpi.DpiScaleY;

        // Правым нижним углом к указателю — трей обычно внизу справа, — но
        // не за пределы рабочей области: панель задач бывает и слева, и сверху.
        Left = Math.Clamp(x - size.Width, left, Math.Max(left, right - size.Width));
        Top = Math.Clamp(y - size.Height, top, Math.Max(top, bottom - size.Height));

        // Вид — по настройкам «Меню в трее» в оформлении, читаются при каждом
        // открытии: сохранить там и есть применить здесь.
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var margin = Card.Margin;

        var backdrop = settings.TrayBlur
            ? Blurred(
                (int)Math.Round((Left + margin.Left) * dpi.DpiScaleX),
                (int)Math.Round((Top + margin.Top) * dpi.DpiScaleY),
                (int)Math.Round((size.Width - margin.Left - margin.Right) * dpi.DpiScaleX),
                (int)Math.Round((size.Height - margin.Top - margin.Bottom) * dpi.DpiScaleY))
            : null;

        // Размытие просили, а снимок не вышел — сплошная карточка, а не
        // прозрачная: прозрачности человек не выбирал.
        double density = settings.TrayBlur && backdrop is null
            ? 1
            : Math.Clamp(settings.TrayDensity, 0, 100) / 100.0;

        Card.Background = backdrop ?? (Brush)Brushes.Transparent;
        Plate.Background = Tint(density);

        Show();
        Activate();

        _exitAt = DateTime.MinValue;
        _ = ReadExitAsync();
    }

    /// <summary>
    /// Оттенок темы под текстом меню заданной плотности.
    /// </summary>
    /// <remarks>
    /// Цвет карточки берётся без его собственной прозрачности: у тем со стеклом
    /// она своя, и плотность тогда значила бы разное в разных темах.
    /// </remarks>
    private Brush Tint(double density)
    {
        var surface = TryFindResource("RaisedColor") is Color c ? c : Colors.Black;

        return new SolidColorBrush(Color.FromRgb(surface.R, surface.G, surface.B)) { Opacity = density };
    }

    /// <summary>Радиус размытия в точках экрана — как у стекла под карточками окна.</summary>
    private const double BlurRadius = 28;

    /// <summary>
    /// Размытый снимок участка экрана — подложка карточки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Снимок, а не живое размытие. Системное размытие Windows — подложка DWM
    /// и политика акцента — под этим окном рисовало сплошной тёмный слой:
    /// снято 24.09 поверх ярких полос, в трёх вариантах окна. Меню живёт
    /// секунды и прячется при щелчке мимо, так что снимка на это время
    /// хватает, и на Windows 10 он работает так же.
    /// </para>
    /// <para>
    /// Снимается шире карточки на радиус и обрезается после размытия: края
    /// размытия иначе тянут к прозрачному и дают тёмную рамку — ровно так
    /// сделано и у стекла в окне (Themes.RenderLayer). У края экрана запас
    /// берётся, сколько есть: за краем снимать нечего.
    /// </para>
    /// </remarks>
    private static ImageBrush? Blurred(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        try
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            int pad = (int)Math.Ceiling(BlurRadius * 1.5);

            int left = Math.Max(screen.Left, x - pad);
            int top = Math.Max(screen.Top, y - pad);
            int right = Math.Min(screen.Right, x + width + pad);
            int bottom = Math.Min(screen.Bottom, y + height + pad);

            if (right - left < width || bottom - top < height)
                return null;

            using var shot = new System.Drawing.Bitmap(right - left, bottom - top);

            using (var graphics = System.Drawing.Graphics.FromImage(shot))
                graphics.CopyFromScreen(left, top, 0, 0, shot.Size);

            var handle = shot.GetHbitmap();
            BitmapSource source;

            try
            {
                source = Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(handle);
            }

            var image = new Image
            {
                Source = source,
                Width = shot.Width,
                Height = shot.Height,
                Effect = new BlurEffect
                {
                    Radius = BlurRadius,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Quality,
                },
            };

            var whole = new Size(shot.Width, shot.Height);
            image.Measure(whole);
            image.Arrange(new Rect(whole));

            var rendered = new RenderTargetBitmap(shot.Width, shot.Height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(image);

            var cropped = new CroppedBitmap(rendered, new Int32Rect(x - left, y - top, width, height));
            cropped.Freeze();

            return new ImageBrush(cropped) { Stretch = Stretch.Fill };
        }
        catch (Exception)
        {
            // Снимок — украшение. Не вышел — карточка будет сплошной.
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        // Esc сначала закрывает ⋯, потом само меню.
        if (Overflow.Visibility == Visibility.Visible)
        {
            Overflow.Visibility = Visibility.Collapsed;
            More.Focus();
        }
        else
            Hide();
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        bool show = Overflow.Visibility != Visibility.Visible;

        Overflow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
            QuitKeep.Focus();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Hide();

        if (_open is { } open)
            open("vpn");
        else
            _showWindow();
    }

    private void OnCheck(object sender, RoutedEventArgs e)
    {
        Hide();

        if (_open is { } open)
            open("check");
        else
            _showWindow();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        Hide();
        _restart?.Invoke();
    }

    private void OnToggle(object sender, RoutedEventArgs e) => _toggle();

    private void OnShowWindow(object sender, RoutedEventArgs e)
    {
        Hide();
        _showWindow();
    }

    private void OnQuitKeep(object sender, RoutedEventArgs e)
    {
        Hide();
        _quit(false);
    }

    private void OnQuitStop(object sender, RoutedEventArgs e)
    {
        // Не прячется: пока гаснут движки, человек видит «Останавливаю…».
        _quit(true);
    }
}
