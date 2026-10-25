using System.Windows;
using System.Windows.Controls;

namespace Zephyron.Gui.Views;

/// <summary>
/// Обёртка плитки «Главной»: занимает ровно ту ширину, что даёт колонка, и не просит больше.
/// </summary>
/// <remarks>
/// <para>
/// Колонка со звёздочкой в WPF раздаётся по желаемой ширине содержимого, если та больше
/// своей доли: длинная строка «Событий» раздвигала правую колонку, и края плиток соседних
/// рядов расходились. Желаемая ширина 0 оставляет делёж пропорциям колонок.
/// </para>
/// <para>
/// Сетка может мерить плитку без предела ширины и больше не перемерить: тогда плитка
/// считает себя шире своего места и её правый край обрезается. Поэтому ширину, которую
/// колонка дала на деле, плитка получает в раскладке и перемеряется по ней.
/// </para>
/// </remarks>
public sealed class FitWidth : Decorator
{
    private double _width = double.NaN;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
            return default;

        double width = double.IsNaN(_width) ? constraint.Width : Math.Min(constraint.Width, _width);
        Child.Measure(new Size(width, constraint.Height));
        return new Size(0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is null)
            return arrangeSize;

        if (arrangeSize.Width != _width)
        {
            _width = arrangeSize.Width;
            double height = Child.DesiredSize.Height;
            Child.Measure(new Size(_width, double.PositiveInfinity));

            // Перенос строк по новой ширине поменял высоту — ряд должен её узнать.
            if (Child.DesiredSize.Height != height)
                InvalidateMeasure();
        }

        Child.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
