using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>
/// Светящиеся точки, всплывающие по всему окну (макет, .particles/.pt).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 16.10: «анимации — тоже должны быть как у референса, где
/// частицы? также возможность их отключать тоже должна быть». Рисуется
/// сама, как <see cref="Views.Sparkline"/>: точек два десятка, каждая живёт
/// своей анимацией, и заводить на них два десятка элементов незачем.
/// </para>
/// <para>
/// Точки живут только при выключенном фоне темы и включённой настройке
/// «Частицы»: под картинкой или видео они были бы второй, чужой жизнью
/// поверх первой. Выключенные анимации окна гасят и их — как в макете,
/// где <c>prefers-reduced-motion</c> убирает <c>.pt</c> вовсе.
/// </para>
/// <para>
/// Движение — целыми кадрами, от нижнего края вверх: у макета <c>rise</c>
/// идёт от <c>translateY(0)</c> к <c>translateY(-100vh)</c> и повторяется
/// без конца. Длительность и задержка у каждой точки свои, иначе они шли бы
/// строем.
/// </para>
/// </remarks>
public sealed class Particles : FrameworkElement
{
    /// <summary>Сколько точек. У макета 26.</summary>
    private const int Count = 26;

    /// <summary>Размер точки, как у макета: 2–5 точек.</summary>
    private const double MinSize = 2;
    private const double MaxSize = 5;

    private sealed class Dot
    {
        public required Brush Fill { get; init; }
        public required double X { get; init; }
        public required double Size { get; init; }
        public required double Period { get; init; }
        public required double Phase { get; init; }
    }

    private static readonly string[] Colours = ["Accent", "Purple", "Accent", "Ok", "Accent"];

    private Dot[] _dots = [];

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Particles()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _tick.Stop();
    }

    /// <summary>Заводит точки по ширине окна и пускает их.</summary>
    /// <remarks>
    /// По ширине — при первом показе: у окна её на создании ещё нет, а точки,
    /// разложенные по нулевой ширине, встали бы в столбик у левого края.
    /// </remarks>
    public void Start()
    {
        double width = ActualWidth;

        if (width <= 0 || !Enabled())
        {
            _tick.Stop();
            return;
        }

        var random = new Random(20261016);
        var dots = new List<Dot>(Count);

        for (int i = 0; i < Count; i++)
        {
            dots.Add(new Dot
            {
                Fill = (Brush)FindResource(Colours[i % Colours.Length]),
                X = random.NextDouble() * width,
                Size = MinSize + random.NextDouble() * (MaxSize - MinSize),
                Period = 12 + random.NextDouble() * 14,
                Phase = random.NextDouble(),
            });
        }

        _dots = [.. dots];
        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
    }

    /// <summary>Останавливает точки — при выключении настройки.</summary>
    public void Stop()
    {
        _tick.Stop();
        _dots = [];
        InvalidateVisual();
    }

    /// <summary>Живы ли точки сейчас: фон темы не мешает и настройка включена.</summary>
    public static bool Enabled() => Settings().Particles
        && Themes.BackdropDeco
        && SystemParameters.ClientAreaAnimation;

    private static AppSettings Settings()
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

    private void OnTick(object? sender, EventArgs e)
    {
        if (!Enabled())
        {
            Stop();
            return;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double height = ActualHeight, width = ActualWidth;

        if (height <= 0 || width <= 0 || _dots.Length == 0)
            return;

        double now = _clock.Elapsed.TotalSeconds;

        foreach (var dot in _dots)
        {
            // Доля пути от нижнего края вверх за свой период: 0 — только
            // показалась, 1 — ушла за верх. Как keyframes rise у макета.
            double x = (now / dot.Period + dot.Phase) % 1;
            double y = height - x * (height + 20);

            // Яркость: ноль у краёв, половина силы в середине пути — у макета
            // точка не вспыхивает, а разгорается и гаснет.
            double alpha = x < 0.1 ? x / 0.1 : x > 0.9 ? (1 - x) / 0.1 : 1;

            drawingContext.PushOpacity(0.55 * alpha);
            drawingContext.DrawEllipse(dot.Fill, null, new Point(dot.X, y), dot.Size / 2, dot.Size / 2);
            drawingContext.Pop();
        }
    }
}
