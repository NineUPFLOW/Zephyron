using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zephyron.Gui.Views;

/// <summary>
/// График сети на «Главной» («Сигнал»): линия нуля, заголовок и верх шкалы
/// внутри области и одна-две линии замеров. Вторая линия отличается не цветом, а штрихом.
/// </summary>
/// <remarks>
/// <para>
/// Рисует только то, что ему дали: настоящие замеры из StatusView.Net.cs.
/// Нет замеров — сетка и строка «нет данных», а не придуманная кривая.
/// Пропущенная точка (<c>NaN</c>) рвёт линию: ответа не было, и соединять
/// соседей значило бы нарисовать задержку, которой никто не мерил.
/// </para>
/// <para>
/// Плавность — сдвигом, а не новыми данными: пришёл замер, и линия за время
/// до следующего уезжает влево на один шаг (<see cref="ShiftProperty"/>).
/// Анимируется одно число, кадры — тридцать в секунду, и только у видимого
/// графика с включёнными анимациями.
/// </para>
/// </remarks>
public sealed class NetChart : FrameworkElement
{
    /// <param name="Dashed">Пунктирная линия: серии различаются формой, а не только цветом.</param>
    /// <param name="Opacity">Прозрачность всей серии (отдача — Text при .75).</param>
    public sealed record Series(IReadOnlyList<double> Values, Brush Stroke, bool Fill, bool Dashed = false, double Opacity = 1);

    /// <summary>Доля шага, на которую линия ещё не доехала до места (1 → 0).</summary>
    public static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(
        nameof(Shift), typeof(double), typeof(NetChart),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Shift
    {
        get => (double)GetValue(ShiftProperty);
        set => SetValue(ShiftProperty, value);
    }

    /// <summary>Сколько точек помещается в окно графика.</summary>
    public int Capacity { get; set; } = 60;

    /// <summary>Подпись единицы на шкале значений: «Мбит/с», «мс».</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Заголовок в левом верхнем углу области графика: «Скорость · 1 мин».</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Что сказать, пока замеров нет.</summary>
    public string Empty { get; set; } = "нет данных";

    /// <summary>Наименьший верх шкалы — чтобы тихая сеть не растягивалась шумом во всю высоту.</summary>
    public double Floor { get; set; } = 1;

    private IReadOnlyList<Series> _series = [];
    private IReadOnlyList<int> _marks = [];

    /// <summary>
    /// Новые замеры. <paramref name="marks"/> — номера точек с пометкой
    /// (потерянный ответ), рисуются меткой у нижнего края.
    /// </summary>
    public void Show(IReadOnlyList<Series> series, IReadOnlyList<int>? marks = null, TimeSpan? step = null)
    {
        bool grew = series.Count > 0 && _series.Count > 0 && series[0].Values.Count > 0
            && series[0].Values.Count >= _series[0].Values.Count;

        _series = series;
        _marks = marks ?? [];

        if (grew && step is { } time && Motion.Enabled && IsVisible)
        {
            var slide = new DoubleAnimation(1, 0, time) { EasingFunction = null };
            Timeline.SetDesiredFrameRate(slide, 30);
            BeginAnimation(ShiftProperty, slide);
        }
        else
        {
            BeginAnimation(ShiftProperty, null);
            Shift = 0;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;

        if (w < 40 || h < 30)
            return;

        var text = Brush("Faint");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);


        bool any = _series.Any(s => s.Values.Any(v => !double.IsNaN(v)));
        double top = any ? Nice(Math.Max(Floor, _series.SelectMany(s => s.Values).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max())) : Floor;

        // Подписи — внутри области, в полосе сверху: заголовок слева, верх шкалы справа.
        // Боковой стопки осей нет: на 60 px она съедала ширину и читалась хуже одной цифры.
        const double head = 14;
        double plotH = h - 1, plotW = w;

        var pen = new Pen(Brush("Border"), 1);
        pen.Freeze();

        // Линия нуля — единственная линия сетки.
        dc.DrawLine(pen, new Point(0, plotH + 0.5), new Point(plotW, plotH + 0.5));

        var title = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10.5, Brush("Muted"), dpi);
        dc.DrawText(title, new Point(6, 1));

        if (!any)
        {
            // Пусто — тихой подписью по центру: ни линии, ни нуля, которых никто не мерил.
            var empty = new FormattedText(Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                12, Brush("Muted"), dpi);
            dc.DrawText(empty, new Point((plotW - empty.Width) / 2, (plotH - empty.Height) / 2));
            return;
        }

        var scale = new FormattedText($"до {Format(top)} {Unit}".Trim(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 10, text, dpi);
        dc.DrawText(scale, new Point(plotW - scale.Width - 6, 2));

        double stepX = plotW / Math.Max(1, Capacity - 1);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, plotW, plotH + 1)));

        foreach (var series in _series)
        {
            int n = series.Values.Count;
            int offset = Capacity - n;

            Point At(int i) => new((offset + i + Shift) * stepX, plotH - Math.Max(0, series.Values[i]) / top * (plotH - 4 - head));

            var line = new StreamGeometry();
            var area = new StreamGeometry();

            using (var l = line.Open())
            using (var a = area.Open())
            {
                bool open = false;
                Point first = default, last = default;

                for (int i = 0; i < n; i++)
                {
                    if (double.IsNaN(series.Values[i]))
                    {
                        if (open)
                            a.LineTo(new Point(last.X, plotH), false, false);

                        open = false;
                        continue;
                    }

                    var p = At(i);

                    if (!open)
                    {
                        l.BeginFigure(p, false, false);
                        a.BeginFigure(new Point(p.X, plotH), true, true);
                        a.LineTo(p, false, false);
                        first = p;
                        open = true;
                    }
                    else
                    {
                        l.LineTo(p, true, true);
                        a.LineTo(p, false, false);
                    }

                    last = p;
                }

                if (open)
                    a.LineTo(new Point(last.X, plotH), false, false);

                _ = first;
            }

            line.Freeze();
            area.Freeze();

            dc.PushOpacity(series.Opacity);

            if (series.Fill)
            {
                dc.PushOpacity(0.16);
                dc.DrawGeometry(series.Stroke, null, area);
                dc.Pop();
            }

            var stroke = new Pen(series.Stroke, 1.8) { LineJoin = PenLineJoin.Round };

            if (series.Dashed)
                stroke.DashStyle = new DashStyle([3, 2], 0);

            stroke.Freeze();
            dc.DrawGeometry(null, stroke, line);
            dc.Pop();
        }

        // Метки потерь у нижнего края — ромбы цвета Warn (форма, а не только цвет).
        if (_marks.Count > 0 && _series.Count > 0)
        {
            var warn = Brush("Warn");
            int offset = Capacity - _series[0].Values.Count;

            foreach (int i in _marks)
            {
                double x = (offset + i + Shift) * stepX;
                var diamond = new StreamGeometry();

                using (var d = diamond.Open())
                {
                    d.BeginFigure(new Point(x, plotH - 9), true, true);
                    d.PolyLineTo([new Point(x + 4, plotH - 5), new Point(x, plotH - 1), new Point(x - 4, plotH - 5)], false, false);
                }

                diamond.Freeze();
                dc.DrawGeometry(warn, null, diamond);
            }
        }

        dc.Pop();
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Круглый верх шкалы: 1, 2, 5 × 10ⁿ.</summary>
    internal static double Nice(double value)
    {
        if (value <= 0)
            return 1;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double share = value / magnitude;

        return (share <= 1 ? 1 : share <= 2 ? 2 : share <= 5 ? 5 : 10) * magnitude;
    }

    private static string Format(double value) =>
        value >= 10 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);
}
