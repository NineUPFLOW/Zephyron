using System.Windows;
using System.Windows.Media;

namespace Zephyron.Gui;

/// <summary>
/// Срез правого верхнего угла у главной плашки «Главной».
/// </summary>
/// <remarks>
/// <para>
/// «Сигнал»: срез — единственная «приборная» деталь окна и стоит только
/// на плашке состояния, чтобы главное выделялось формой, а не цветом.
/// Остальные углы скруглены, как у всех карточек.
/// </para>
/// <para>
/// Срез — обрезкой (<see cref="UIElement.Clip"/>), пересчитанной при смене
/// размера. Геометрия заморожена, раскладку она не трогает. Рамка вдоль среза
/// не рисуется: кромка карточки там и так почти погасла (CardRim).
/// </para>
/// </remarks>
public static class Chamfer
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(Chamfer), new PropertyMetadata(0.0, OnSize));

    public static double GetSize(DependencyObject element) => (double)element.GetValue(SizeProperty);

    public static void SetSize(DependencyObject element, double value) => element.SetValue(SizeProperty, value);

    private static void OnSize(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.SizeChanged -= OnElementSize;

        if ((double)e.NewValue > 0)
        {
            element.SizeChanged += OnElementSize;
            Cut(element);
        }
        else
        {
            element.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnElementSize(object sender, SizeChangedEventArgs e) => Cut((FrameworkElement)sender);

    private static void Cut(FrameworkElement element)
    {
        double w = element.ActualWidth, h = element.ActualHeight;

        // Срез не больше четверти меньшей стороны: у низкой строки он не съест её целиком.
        double c = Math.Min(GetSize(element), Math.Min(w, h) / 4);

        if (w <= 0 || h <= 0 || c <= 0)
            return;

        element.Clip = Outline(w, h, c);
    }

    /// <summary>Контур со срезанным правым верхним углом.</summary>
    internal static Geometry Outline(double w, double h, double c)
    {
        var outline = new StreamGeometry();

        using (var context = outline.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.PolyLineTo(
                [new Point(w - c, 0), new Point(w, c), new Point(w, h), new Point(0, h)],
                isStroked: false, isSmoothJoin: false);
        }

        outline.Freeze();
        return outline;
    }
}
