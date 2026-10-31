using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zephyron.Core;

namespace Zephyron.Gui.Views;

/// <summary>
/// Состояние «Главной»: одно на плашку, знак, слово и дорожки. Имена совпадают
/// со значениями <c>Tag</c> стиля <c>StateMark</c>.
/// </summary>
public enum HomeState { Off, Starting, Running, Degraded, Failed }

/// <summary>
/// Трассы на «Главной»: от компьютера к сети три пути — десинк, напрямую
/// и туннель, — и по ним бежит поток, пока движок пути работает.
/// </summary>
/// <remarks>
/// <para>
/// Знак Zephyron: программа о том, что у каждого сайта свой путь, и трассы
/// показывают ровно это — какие пути открыты сейчас. Это не украшение: путь
/// окрашен состоянием своего движка (то же слово и цвет, что у плашки), а скорость
/// потока — от настоящего приёма по адаптерам (<see cref="Flow"/>).
/// </para>
/// <para>
/// Ничего не придумывает. Выключенный обход — «напрямую» сплошной, прочие пунктиром,
/// потока нет; движок, который в этот запуск не поднимали, — пунктиром без потока;
/// сбойный — линия с разрывом и крестом. Бег — одна анимация смещения пунктира на трассу,
/// только пока путь работает, окно видно, анимации включены и профиль не «лёгкий»:
/// тридцать кадров в секунду, при остановке — неподвижная раскраска (<see cref="Motion.Changed"/>).
/// Кисти — ресурсы палитры (SetResourceReference): смена темы перекрашивает их сама.
/// </para>
/// </remarks>
public sealed class FlowLanes : Grid
{
    /// <summary>Шаг пунктира потока: точка и просвет. Смещение на шаг — один цикл анимации.</summary>
    private const double Dot = 0.1;
    private const double Gap = 7;

    /// <summary>Полуширина разрыва у сбойной трассы.</summary>
    private const double Break = 9;

    private static readonly DoubleCollection Dashed = Frozen(3, 4);
    private static readonly DoubleCollection Packets = Frozen(Dot, Gap);

    private sealed class Lane(string name, string packets, bool direct)
    {
        public readonly bool Direct = direct;
        public readonly string PacketKey = packets;
        public readonly Path Rail = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
        public readonly Path Stream = new()
        {
            StrokeThickness = 4,
            StrokeDashCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = Packets,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false,
        };
        public readonly Path Cross = new()
        {
            Data = Geometry.Parse("M -4,-4 L 4,4 M -4,4 L 4,-4"),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        public readonly TextBlock Label = new() { FontSize = 11, IsHitTestVisible = false, Text = name };
        public Geometry? Full, Broken;
        public HomeState State;
        public AnimationClock? Clock;
    }

    private readonly Lane _desync = new("десинк", "AccentFlow", direct: false);
    private readonly Lane _direct = new("напрямую", "Accent2", direct: true);
    private readonly Lane _tunnel = new("туннель", "AccentFlow", direct: false);
    private readonly Ellipse _from = Node();
    private readonly Ellipse _to = Node();
    private readonly Canvas _canvas = new() { ClipToBounds = false };

    private HomeState _state;
    private double _flow;
    private Window? _window;

    public FlowLanes()
    {
        // 88, а не 104: плашка «Главной» не должна перерастать «Компоненты» рядом (26.10).
        Height = 88;
        SnapsToDevicePixels = false;
        _ease.Tick += OnEase;
        Children.Add(_canvas);

        foreach (var lane in Lanes())
        {
            lane.Rail.SetResourceReference(Shape.StrokeProperty, "Faint");
            lane.Cross.SetResourceReference(Shape.StrokeProperty, "Danger");
            lane.Label.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            lane.Label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");

            _canvas.Children.Add(lane.Rail);
            _canvas.Children.Add(lane.Stream);
            _canvas.Children.Add(lane.Cross);
            _canvas.Children.Add(lane.Label);
        }

        _canvas.Children.Add(_from);
        _canvas.Children.Add(_to);

        SizeChanged += (_, _) => Layout();
        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) =>
        {
            // Свёрнутое окно IsVisible не меняет — следим за ним сами. Подписка на
            // статические и оконные события — только пока дорожки на экране.
            _window = Window.GetWindow(this);

            if (_window is not null)
            {
                _window.StateChanged -= OnWindowState;
                _window.StateChanged += OnWindowState;
            }

            Motion.Changed -= OnMotion;
            Motion.Changed += OnMotion;

            Animate();
        };
        Unloaded += (_, _) =>
        {
            Motion.Changed -= OnMotion;

            if (_window is not null)
                _window.StateChanged -= OnWindowState;

            Stop();
        };
    }

    private void OnWindowState(object? sender, EventArgs e) => Animate();

    // Motion.Changed может прийти не из потока окна (настройки системы).
    private void OnMotion(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Animate);

    /// <summary>Анимации выключены или профиль «лёгкий»: дорожки только раскрашены.</summary>
    private static bool Still => !Motion.Enabled || Motion.Profile == EffectsProfile.Lite;

    /// <summary>
    /// Состояние путей: общее и каждого движка (<see cref="HomeState.Off"/> —
    /// движок в этот запуск не поднимали).
    /// </summary>
    public void Show(HomeState state, HomeState desync, HomeState tunnel)
    {
        bool ignite = state == HomeState.Starting && _state != HomeState.Starting;
        _state = state;

        // «Напрямую» открыт, пока обход жив; у сбоя и выключенного — как у выключенного.
        _direct.State = state is HomeState.Running or HomeState.Degraded ? HomeState.Running
            : state == HomeState.Starting ? HomeState.Starting : HomeState.Off;
        _desync.State = desync;
        _tunnel.State = tunnel;

        foreach (var lane in Lanes())
            Paint(lane);

        Animate();

        if (ignite)
            LightUp();
    }

    /// <summary>
    /// Начался запуск — трассы загораются по очереди, сверху вниз.
    /// </summary>
    /// <remarks>
    /// Единственная поставленная сцена «Главной»: запуск — главное событие
    /// программы, и он должен быть виден. Полсекунды, один раз за запуск.
    /// </remarks>
    private void LightUp()
    {
        if (Still)
            return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        int step = 0;

        foreach (var lane in Lanes())
        {
            var begin = TimeSpan.FromMilliseconds(110 * step++);
            var fade = new DoubleAnimationUsingKeyFrames { Duration = begin + TimeSpan.FromMilliseconds(420), FillBehavior = FillBehavior.Stop };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(lane.Rail.Opacity, KeyTime.FromTimeSpan(begin + TimeSpan.FromMilliseconds(420)), ease));

            lane.Rail.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>
    /// Насколько бежит поток: приём по всем адаптерам, Мбит/с. Ноль — поток
    /// едва ползёт, но не стоит: трасса открыта, просто по ней сейчас тихо.
    /// </summary>
    public double Flow
    {
        get => _flow;
        set
        {
            _flow = Math.Max(0, value);

            // Логарифм: разница между 1 и 10 Мбит/с видна так же, как между 10 и 100.
            // Диапазон уже прежнего (0,4–2,6): скачок приёма не должен бросать бег втрое.
            _target = 0.7 + Math.Min(1, Math.Log10(1 + _flow) / 2.3) * 0.9;

            if (!_ease.IsEnabled && Lanes().Any(l => l.Clock is not null))
                _ease.Start();
        }
    }

    /// <summary>Скорость, к которой бег подтягивается, и та, что сейчас.</summary>
    private double _target = 0.7, _speed = 0.7;

    /// <summary>
    /// Плавный переход скорости (жалоба 28.10: «то очень быстро, то медленно»): замер
    /// приёма приходит раз в секунду, и прежде скорость менялась на нём скачком. Теперь
    /// бег подтягивается к цели по 6 % за кадр 20 раз в секунду — около секунды на переход.
    /// </summary>
    private readonly DispatcherTimer _ease = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void OnEase(object? sender, EventArgs e)
    {
        _speed += (_target - _speed) * 0.06;

        if (Math.Abs(_target - _speed) < 0.005)
        {
            _speed = _target;
            _ease.Stop();
        }

        ApplySpeed();
    }

    private void ApplySpeed()
    {
        foreach (var lane in Lanes())
        {
            // У частично работающего пути пакеты идут замедленно.
            if (lane.Clock?.Controller is { } controller)
                controller.SpeedRatio = lane.State == HomeState.Degraded ? _speed * 0.4 : _speed;
        }
    }

    private IEnumerable<Lane> Lanes() => [_desync, _direct, _tunnel];

    private static DoubleCollection Frozen(params double[] values)
    {
        var collection = new DoubleCollection(values);
        collection.Freeze();
        return collection;
    }

    private static Ellipse Node()
    {
        var node = new Ellipse { Width = 10, Height = 10, StrokeThickness = 2 };
        node.SetResourceReference(Shape.StrokeProperty, "Muted");
        node.SetResourceReference(Shape.FillProperty, "Surface");
        return node;
    }

    /// <summary>
    /// Раскладка: узлы по краям на середине высоты, три трассы расходятся
    /// от левого узла плавной S-кривой, идут ровно и сходятся у правого.
    /// </summary>
    private void Layout()
    {
        double w = ActualWidth, h = ActualHeight;

        if (w < 80 || h < 40)
            return;

        double mid = h / 2;
        double spread = Math.Min(34, h / 2 - 14);
        double left = 5, right = w - 5;
        double bend = Math.Min(110, (right - left) * 0.16);
        double centre = (left + right) / 2;

        Place(_desync, mid - spread);
        Place(_direct, mid);
        Place(_tunnel, mid + spread);

        Canvas.SetLeft(_from, left - 5);
        Canvas.SetTop(_from, mid - 5);
        Canvas.SetLeft(_to, right - 5);
        Canvas.SetTop(_to, mid - 5);

        foreach (var lane in Lanes())
            Paint(lane);

        void Place(Lane lane, double y)
        {
            lane.Full = Build(centre: null);
            lane.Broken = Build(centre);

            lane.Stream.Data = lane.Full;

            Canvas.SetLeft(lane.Cross, centre);
            Canvas.SetTop(lane.Cross, y);

            // Подпись — над ровным участком, у его начала.
            Canvas.SetLeft(lane.Label, left + bend + 10);
            Canvas.SetTop(lane.Label, y - 18);

            // Целая трасса или две половины с разрывом вокруг середины.
            Geometry Build(double? centre)
            {
                var geometry = new StreamGeometry();

                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(left, mid), isFilled: false, isClosed: false);
                    context.BezierTo(new Point(left + bend * 0.55, mid), new Point(left + bend * 0.45, y), new Point(left + bend, y), true, true);

                    if (centre is { } gap)
                    {
                        context.LineTo(new Point(gap - Break, y), true, true);
                        context.BeginFigure(new Point(gap + Break, y), isFilled: false, isClosed: false);
                    }

                    context.LineTo(new Point(right - bend, y), true, true);
                    context.BezierTo(new Point(right - bend * 0.45, y), new Point(right - bend * 0.55, mid), new Point(right, mid), true, true);
                }

                geometry.Freeze();
                return geometry;
            }
        }
    }

    /// <summary>Раскраска трассы по её состоянию (таблица в docs/design-system.md, «Главная»).</summary>
    private void Paint(Lane lane)
    {
        var (key, opacity, dash) = lane.State switch
        {
            HomeState.Starting => ("Accent", 0.7, null),
            HomeState.Running => ("Success", 0.7, null),
            HomeState.Degraded => ("Warn", 0.7, Dashed),
            HomeState.Failed => ("Danger", 0.7, null),
            _ => lane.Direct ? ("Muted", 0.5, null) : ("Faint", 0.35, Dashed),
        };

        lane.Rail.SetResourceReference(Shape.StrokeProperty, key);
        lane.Rail.Opacity = opacity;
        lane.Rail.StrokeDashArray = dash;
        lane.Rail.Data = lane.State == HomeState.Failed ? lane.Broken : lane.Full;
        lane.Cross.Visibility = lane.State == HomeState.Failed ? Visibility.Visible : Visibility.Collapsed;
        lane.Label.SetResourceReference(TextBlock.ForegroundProperty, lane.State == HomeState.Off ? "Faint" : "Text");
        lane.Stream.SetResourceReference(Shape.StrokeProperty, lane.State == HomeState.Degraded ? "Warn" : lane.PacketKey);

        // Узлы — кольца цвета общего состояния.
        string node = _state switch
        {
            HomeState.Starting => "Accent",
            HomeState.Running => "Success",
            HomeState.Degraded => "Warn",
            _ => "Muted",
        };

        _from.SetResourceReference(Shape.StrokeProperty, node);
        _to.SetResourceReference(Shape.StrokeProperty, node);
    }

    /// <summary>Запускает бег по работающим трассам или гасит его.</summary>
    private void Animate()
    {
        bool run = IsVisible && IsLoaded && !Still && _window?.WindowState != WindowState.Minimized;

        foreach (var lane in Lanes())
        {
            bool flows = run && lane.State is HomeState.Running or HomeState.Degraded;

            lane.Stream.Visibility = flows ? Visibility.Visible : Visibility.Hidden;

            if (flows && lane.Clock is null)
            {
                // Пунктир бежит слева направо: смещение уменьшается на шаг за цикл.
                var animation = new DoubleAnimation(0, -(Dot + Gap) * 6, TimeSpan.FromSeconds(1.6))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                Timeline.SetDesiredFrameRate(animation, 30);

                lane.Clock = animation.CreateClock();
                lane.Stream.ApplyAnimationClock(Shape.StrokeDashOffsetProperty, lane.Clock);
            }
            else if (!flows && lane.Clock is not null)
            {
                lane.Stream.ApplyAnimationClock(Shape.StrokeDashOffsetProperty, null);
                lane.Clock = null;
            }
        }

        // Новые часы — сразу с нынешней скоростью, без рывка к 1,0.
        ApplySpeed();
    }

    private void Stop()
    {
        _ease.Stop();
        foreach (var lane in Lanes())
        {
            lane.Stream.ApplyAnimationClock(Shape.StrokeDashOffsetProperty, null);
            lane.Clock = null;
        }
    }
}
