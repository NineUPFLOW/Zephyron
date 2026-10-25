using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>
/// Анимации окна — в одном месте и с одним выключателем.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 28.09: анимации мастера понравились, «давай добавим по всей
/// программе, с возможностью их выключить». Выключатель — в «Оформлении»
/// (<see cref="AppSettings.Animations"/>); выключенная анимация в самой
/// Windows тоже их гасит: тот, кто её выключил, сделал это не просто так.
/// </para>
/// <para>
/// Всё короткое, до трети секунды, и только на появление: движение, которое
/// заставляет ждать, раздражает с третьего раза. Исчезает сразу, кроме
/// уведомления — у него короткое угасание, чтобы не пропадало рывком.
/// </para>
/// </remarks>
public static class Motion
{
    private static AppSettings? _cfg;
    private static EffectsProfile? _profile;

    /// <summary>
    /// «Движение выключено» для шаблонов: наследуемое свойство, которое Motion
    /// выставляет каждому окну (оно же перевыставляется по <see cref="Changed"/>).
    /// </summary>
    /// <remarks>
    /// Триггер шаблона не умеет ни DynamicResource в Value, ни x:Static с
    /// обновлением; присоединённое свойство — умеет, и оно же перезапускает
    /// сториборд сразу, а не при следующем показе. Значение по умолчанию —
    /// «движение есть»: окно, которого Motion не видел, анимируется как прежде.
    /// </remarks>
    public static readonly DependencyProperty StillProperty = DependencyProperty.RegisterAttached(
        "Still", typeof(bool), typeof(Motion),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetStill(DependencyObject element) => (bool)element.GetValue(StillProperty);

    public static void SetStill(DependencyObject element, bool value) => element.SetValue(StillProperty, value);

    // Окна живут в разных потоках (в проверках — не в потоке приложения), поэтому
    // Application.Windows не годится: свой список со слабыми ссылками.
    private static readonly List<WeakReference<Window>> Hosts = [];

    private static bool IsStill => !Enabled || Profile == EffectsProfile.Lite;

    private static void Track(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;

        lock (Hosts)
        {
            Hosts.RemoveAll(h => !h.TryGetTarget(out _));
            Hosts.Add(new WeakReference<Window>(window));
        }

        SetStill(window, IsStill);
    }

    private static void ApplyStill()
    {
        bool still = IsStill;
        List<Window> alive;

        lock (Hosts)
            alive = Hosts.Select(h => h.TryGetTarget(out var w) ? w : null!).Where(w => w is not null).ToList();

        foreach (var window in alive)
            window.Dispatcher.BeginInvoke(() => SetStill(window, still));
    }

    /// <summary>Вызов из App.OnStartup: запускает статический конструктор до первого окна.</summary>
    public static void Hook()
    {
    }

    static Motion()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Track));
        Changed += (_, _) => ApplyStill();

        // Один раз и навсегда: статические источники живут весь процесс, а
        // отписываться нашему статическому обработчику не от кого.
        RenderCapability.TierChanged += (_, _) => Refresh(reload: false);

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))
                Refresh(reload: false);
        };
    }

    /// <summary>Анимировать ли сейчас.</summary>
    public static bool Enabled => (_cfg ??= Read()).Animations && (_windows ?? SystemParameters.ClientAreaAnimation);

    /// <summary>Настройка анимаций Windows, подставленная проверкой; <c>null</c> — настоящая.</summary>
    /// <remarks>
    /// Раннер сборки на сервере без экрана держит анимации Windows выключенными,
    /// и проверки движения там видели «Лёгкий» при любых настройках (25.10).
    /// </remarks>
    private static bool? _windows;

    /// <summary>
    /// Действующий профиль эффектов. Lite — когда анимации выключены (тумблером
    /// или Windows), включён высокий контраст, или настройка «авто» на слабом
    /// видео (программная отрисовка, Tier &lt; 2).
    /// </summary>
    public static EffectsProfile Profile => _profile ??= Resolve();

    /// <summary>
    /// Поднимается, когда изменилось то, что решает, движется ли окно:
    /// выключатель, профиль, высокий контраст, уровень отрисовки.
    /// </summary>
    /// <remarks>
    /// Событие статическое и держит подписчиков за обработчик: элемент,
    /// подписавшийся на нём, обязан отписаться (Unloaded / закрытие окна),
    /// иначе он не соберётся сборщиком — окно пересоздаётся из трея.
    /// </remarks>
    public static event EventHandler? Changed;

    /// <summary>Перечитать выключатель — после его смены в «Оформлении».</summary>
    public static void Refresh() => Refresh(reload: true);

    private static void Refresh(bool reload)
    {
        if (reload)
            _cfg = Read();

        _profile = null;

        // Прежде рассылки не было, и выключенные «Анимации» не останавливали
        // уже запущенные бесконечные анимации до перезапуска окна.
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Подставить настройки без файла — для проверок; анимации Windows — включены, если не сказано иначе.</summary>
    internal static void Use(AppSettings settings, bool windowsAnimations = true)
    {
        _cfg = settings;
        _windows = windowsAnimations;
        _profile = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static EffectsProfile Resolve()
    {
        var cfg = _cfg ??= Read();

        if (!Enabled || SystemParameters.HighContrast)
            return EffectsProfile.Lite;

        if (cfg.Effects is { } chosen)
            return chosen;

        return RenderCapability.Tier >> 16 < 2 ? EffectsProfile.Lite : EffectsProfile.Balanced;
    }

    private static AppSettings Read()
    {
        try
        {
            return AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    // Замороженная: статический объект делят потоки (окна, тесты), а живой Freezable
    // принадлежит потоку-создателю.
    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    /// <summary>Знак программы проявляется и чуть наезжает — мягко, без рывка.</summary>
    /// <remarks>
    /// Отдельно от <see cref="Arrive"/>: там сдвиг вниз на десять точек,
    /// а знаку в шапке меню он ни к чему — он и так стоит на месте.
    /// Владелец 12.10: «нет плавных анимаций» — знак появлялся первым
    /// и резко.
    /// </remarks>
    public static void Mark(FrameworkElement element)
    {
        if (!Enabled)
            return;

        var time = TimeSpan.FromMilliseconds(400);

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, time) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop });

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var grow = new ScaleTransform(0.92, 0.92);
        element.RenderTransform = grow;

        var scale = new DoubleAnimation(0.92, 1.0, time) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    /// <summary>Элемент проявляется и доезжает на место.</summary>
    public static void Arrive(FrameworkElement element, double dx = 0, double dy = 6, int ms = 200, int delay = 0)
    {
        if (!Enabled)
            return;

        var time = TimeSpan.FromMilliseconds(ms + delay);

        element.BeginAnimation(UIElement.OpacityProperty, Held(0, 1, delay, ms, time));

        // Свой сдвиг — только если элементу не назначен чужой: у полосы
        // запуска на «Главной» трансформ живёт своей анимацией, и затереть
        // его значило бы сломать её.
        if (element.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or TranslateTransform))
            return;

        // В теме со стеклом — без сдвига. Кусок размытого фона под карточкой
        // пересчитывается при перекладке окна (Glass), а сдвиг анимацией
        // её не вызывает: карточка ехала, стекло стояло, и после анимации
        // оставалось сдвинутым на десять точек. Владелец 28.09: «дёргано,
        // в VPN и десинке особенно» — тема с фоном, карточек много.
        if (Glass.Image is not null)
            return;

        var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = shift;

        if (dx != 0)
            shift.BeginAnimation(TranslateTransform.XProperty, Held(dx, 0, delay, ms, time));

        if (dy != 0)
            shift.BeginAnimation(TranslateTransform.YProperty, Held(dy, 0, delay, ms, time));
    }

    /// <summary>
    /// Анимация со стартовым значением на всё время задержки.
    /// </summary>
    /// <remarks>
    /// Не <c>BeginTime</c>: до начала анимация не действует, и элемент стоял бы
    /// видимым, а потом мигнул бы в ноль. Здесь стартовое значение держится
    /// с первого кадра, а по окончании анимация снимается — дальше элемент
    /// живёт своим обычным значением, и ничто его не держит.
    /// </remarks>
    private static AnimationTimeline Held(double from, double to, int delay, int ms, TimeSpan total)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = total };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        if (delay > 0)
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));

        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(total), Ease));
        frames.FillBehavior = FillBehavior.Stop;

        return frames;
    }

    /// <summary>
    /// Раздел проявляется целиком — одним коротким движением.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прежде карточки проявлялись по очереди и со сдвигом, а начало ждало,
    /// пока раздел достроится. Владелец 28.09 дважды: «дёргано», и
    /// «диагностика долго является пустой». Замер: кадры при этом шли ровно
    /// (7 мс), так что дело не в частоте — а в том, что сдвиг вёл текст
    /// ступеньками, очередь из восьми карточек растягивала появление, а
    /// ожидание простоя держало раздел пустым, пока он считал (у «Диагностики»
    /// 0,4 с). Теперь — одно проявление всей страницы за 180 мс.
    /// </para>
    /// <para>
    /// Начинается на Loaded раздела: подписка встаёт после его собственной
    /// (она в конструкторе), и синхронная работа раздела к этому моменту
    /// уже сделана — анимация не тратит свои миллисекунды на чужую паузу.
    /// </para>
    /// </remarks>
    public static void Page(FrameworkElement page)
    {
        if (!Enabled)
            return;

        // Скрыт с создания: первый кадр рисуется раньше Loaded (приоритет
        // отрисовки выше), и без этого раздел успевал показаться целиком,
        // а проявление шло уже по видимому — от 0,92 к 1 (замер 28.09,
        // владелец: «анимации пропали»). Через пять секунд, если так и не
        // дождались, проявится сам.
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 0, TimeSpan.FromSeconds(5)) { FillBehavior = FillBehavior.Stop });

        // Раздел проявляется и всплывает на 6 точек за 180 мс (шкала движения,
        // раздел 7 спеки): виден как смена экрана, а не моргание. Без «оседания»
        // масштабом — оно читалось как зум. Одним куском, без очереди карточек:
        // ровно это владелец 28.09 назвал «дёргано». Под стеклом темы — без
        // сдвига: стекло следует раскладке, а не отрисовке (см. Arrive).
        double rise = Glass.Image is null ? 6 : 0;
        var lift = new TranslateTransform(0, rise);
        page.RenderTransform = lift;

        // Начало — на первом кадре после Loaded: к нему синхронная работа
        // раздела сделана. Отсчёт свой, по часам, а не анимацией WPF: её
        // часы начинали с времени до паузы раздела, и уже второй кадр
        // выходил на 0,83 (замер 28.09) — проявление снова выглядело
        // появлением. Здесь первый показанный кадр — ноль, дальше по
        // реальному времени, с мягким замедлением к концу.
        var clock = new Stopwatch();
        const double Length = 180;

        void Frame(object? sender, EventArgs e)
        {
            if (!clock.IsRunning)
            {
                page.BeginAnimation(UIElement.OpacityProperty, null);
                page.Opacity = 0;
                clock.Start();
                return;
            }

            double x = Math.Min(1, clock.Elapsed.TotalMilliseconds / Length);

            if (x >= 1)
            {
                CompositionTarget.Rendering -= Frame;
                page.ClearValue(UIElement.OpacityProperty);
                page.RenderTransform = Transform.Identity;
                return;
            }

            // Кубическое замедление: быстрый старт и долгая мягкая посадка.
            double eased = 1 - Math.Pow(1 - x, 3);
            page.Opacity = Math.Min(1, eased * 1.4);
            lift.Y = rise * (1 - eased);
        }

        void Start(object sender, RoutedEventArgs e)
        {
            page.Loaded -= Start;
            CompositionTarget.Rendering += Frame;
        }

        page.Loaded += Start;
    }
    /// <summary>Угасает и прячется; без анимации — сразу.</summary>
    public static void Leave(FrameworkElement element, Action? after = null)
    {
        if (!Enabled)
        {
            element.Visibility = Visibility.Collapsed;
            after?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop };

        fade.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            after?.Invoke();
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
