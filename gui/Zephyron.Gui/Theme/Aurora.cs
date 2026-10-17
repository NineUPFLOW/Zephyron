using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>
/// Волны под всем окном (макет, <c>.bg-svg</c> с фильтром <c>liquid</c>).
/// </summary>
/// <remarks>
/// <para>
/// В макете это три горизонтальные полосы, прогнанные через
/// <c>feTurbulence</c> + <c>feDisplacementMap</c>: края полос медленно
/// колышутся, и фон дышит. Фильтров SVG в WPF нет, поэтому полосы рисуются
/// здесь: у каждой верхняя и нижняя границы — сумма двух синусоид с разными
/// периодами, а заливка — тот же переход сиреневого, голубого и розового,
/// что у <c>waveGrad</c>.
/// </para>
/// <para>
/// Рисуется сама, как <see cref="Particles"/>: три полосы — это три фигуры,
/// и заводить на них разметку незачем. Живёт по тем же правилам, что прочая
/// декорация фона: только при выключенном фоне темы и включённых анимациях.
/// </para>
/// <para>
/// Разложение по синусоидам — не подражание ради подражания: у турбулентности
/// макета нет повторяющегося кадра, и заметить, где она начинается заново,
/// нельзя. Сумма двух волн с несоизмеримыми периодами даёт то же самое:
/// рисунок не повторяется на глаз.
/// </para>
/// </remarks>
public sealed class Aurora : FrameworkElement
{
    /// <summary>Полоса: доля высоты от верха, высота и сила.</summary>
    private readonly record struct Band(double Top, double Height, double Strength);

    /// <summary>Три полосы макета: <c>y</c>, высота и <c>opacity</c> его прямоугольников.</summary>
    private static readonly Band[] Bands =
    [
        new(0.00, 0.25, 0.55),
        new(0.35, 0.18, 0.35),
        new(0.72, 0.22, 0.45),
    ];

    /// <summary>Сколько долей ширины: чем больше, тем глаже колыхание.</summary>
    private const int Slices = 96;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public Aurora()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _tick.Stop();
    }

    /// <summary>Пускает волны — при первом показе, когда известен размер.</summary>
    public void Start()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || !Enabled())
        {
            _tick.Stop();
            return;
        }

        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
    }

    /// <summary>Живы ли волны: фон темы не мешает и анимации включены.</summary>
    public static bool Enabled() => Themes.BackdropDeco && SystemParameters.ClientAreaAnimation;

    private void OnTick(object? sender, EventArgs e)
    {
        if (!Enabled())
        {
            _tick.Stop();
            return;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;

        if (width <= 0 || height <= 0)
            return;

        if (!Enabled())
            return;

        double t = _clock.Elapsed.TotalSeconds;

        // Переход waveGrad макета: сиреневый → голубой → розовый. Цвета
        // берём у темы (Purple, Accent, Danger) — с чужой темой волны
        // перекрасятся вместе с окном, как и сферы по углам.
        var grad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(((SolidColorBrush)FindResource("Purple")).Color, 0.00),
                new GradientStop(((SolidColorBrush)FindResource("Accent")).Color, 0.50),
                new GradientStop(((SolidColorBrush)FindResource("Danger")).Color, 1.00),
            },
        };

        foreach (var band in Bands)
            Draw(drawingContext, band, width, height, t, grad);
    }

    /// <summary>Одна полоса: границы колышутся, заливка — переходом слева направо.</summary>
    private static void Draw(DrawingContext context, Band band, double width, double height, double t, Brush fill)
    {
        // Сила полосы макета — общая и на её прямоугольник, и на переход
        // (у waveGrad стопы заданы с alpha). Умножаем на силу полосы.
        double top = band.Top * height;
        double bottom = top + band.Height * height;

        var geometry = new StreamGeometry();

        using (var path = geometry.Open())
        {
            var points = new List<Point>(Slices * 2 + 1);

            for (int i = 0; i <= Slices; i++)
            {
                double x = width * i / Slices;
                double u = (double)i / Slices;

                // Две волны с разными периодами и скоростями — как разные
                // октавы турбулентности макета.
                double wave = Math.Sin(u * Math.PI * 3.4 + t * 0.55) * 0.55
                    + Math.Sin(u * Math.PI * 7.1 - t * 0.31) * 0.45;

                double shift = wave * height * 0.035;

                points.Add(new Point(x, top + shift));
            }

            for (int i = Slices; i >= 0; i--)
            {
                double x = width * i / Slices;
                double u = (double)i / Slices;

                double wave = Math.Sin(u * Math.PI * 2.7 - t * 0.42) * 0.6
                    + Math.Sin(u * Math.PI * 6.3 + t * 0.27) * 0.4;

                points.Add(new Point(x, bottom + wave * height * 0.03));
            }

            path.BeginFigure(points[0], isFilled: true, isClosed: true);
            path.PolyLineTo(points.Skip(1).ToArray(), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();

        context.PushOpacity(band.Strength);
        context.DrawGeometry(fill, null, geometry);
        context.Pop();
    }
}
