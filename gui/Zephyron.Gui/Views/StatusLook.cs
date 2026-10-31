using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Zephyron.Gui.Views;

/// <summary>
/// Строка состояния вкладки в виде «Сигнала»: обычное сообщение — серая подпись,
/// ошибка — панель ErrorState (DangerSoft, DangerLine, треугольник), пустое
/// место — кольцо «нет».
/// </summary>
/// <remarks>
/// <para>
/// Тип элемента не меняется: вкладки пишут в свой TextBlock .Text и .Visibility,
/// как и прежде. Сверху лежит рамка, и она лишь следит за этим текстом. Ошибку
/// узнаём по обороту фразы («Не удалось …», «… не читаются») без учёта регистра —
/// текстов десятки, и менять каждый вызов ради цвета не стоит. Фразу, которую
/// оборот не ловит, вкладка может показать через <see cref="Fail"/>.
/// </para>
/// <para>
/// Следим привязками на свои присоединённые свойства, а не
/// <c>DependencyPropertyDescriptor.AddValueChanged</c>: дескриптор лежит
/// в статическом кэше и держал TextBlock — а с ним всё дерево раздела — до
/// конца работы программы (QaSignalTests, 0.18). Привязка живёт на самом
/// TextBlock и уходит в мусор вместе с ним.
/// </para>
/// </remarks>
internal static class StatusLook
{
    private static readonly string[] Failed =
    [
        "не удалось", "не читаются", "не прочитались", "не найден", "не скопировалось", "не записалось", "не вышло",
        "не удался", "не разбирается", "не ответил", "не открыл", "не удалась", "уже записан",
    ];

    private static readonly string[] Empty = ["Подписок нет", "Пока пусто", "Пусто"];

    private sealed class Parts(Border box, Control mark)
    {
        public Border Box { get; } = box;

        public Control Mark { get; } = mark;

        /// <summary>Текст, объявленный ошибкой через <see cref="Fail"/>.</summary>
        public string? Forced { get; set; }
    }

    private static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(Parts), typeof(StatusLook));

    private static readonly DependencyProperty WatchedTextProperty = DependencyProperty.RegisterAttached(
        "WatchedText", typeof(string), typeof(StatusLook), new PropertyMetadata(null, OnWatched));

    private static readonly DependencyProperty WatchedVisibilityProperty = DependencyProperty.RegisterAttached(
        "WatchedVisibility", typeof(Visibility), typeof(StatusLook), new PropertyMetadata(Visibility.Visible, OnWatched));

    /// <summary>
    /// Привязывает рамку и знак к тексту строки состояния.
    /// </summary>
    /// <param name="box">Рамка вокруг строки; прячется вместе с пустым текстом.</param>
    /// <param name="text">Сама строка — её Text и Visibility пишет вкладка.</param>
    /// <param name="mark">Знак слева (стиль StateMark); виден только у ошибки и пустого места.</param>
    public static void Attach(Border box, TextBlock text, Control mark)
    {
        text.SetValue(PartsProperty, new Parts(box, mark));
        Watch(text, WatchedTextProperty, TextBlock.TextProperty);
        Watch(text, WatchedVisibilityProperty, UIElement.VisibilityProperty);
        Refresh(text);
    }

    /// <summary>
    /// Показывает в строке ошибку, даже если оборот фразы её не выдаёт.
    /// </summary>
    /// <remarks>Действует, пока в строке этот же текст: следующее обычное сообщение снова судится по обороту.</remarks>
    public static void Fail(TextBlock text, string message)
    {
        if (text.GetValue(PartsProperty) is Parts parts)
            parts.Forced = message;

        text.Text = message;
        text.Visibility = Visibility.Visible;
        Refresh(text);
    }

    private static void Watch(TextBlock text, DependencyProperty mirror, DependencyProperty source) =>
        BindingOperations.SetBinding(text, mirror, new Binding { Source = text, Path = new PropertyPath(source), Mode = BindingMode.OneWay });

    private static void OnWatched(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBlock text)
            Refresh(text);
    }

    private static void Refresh(TextBlock text)
    {
        if (text.GetValue(PartsProperty) is not Parts parts)
            return;

        var (box, mark) = (parts.Box, parts.Mark);
        bool shown = text.Visibility == Visibility.Visible && text.Text.Length > 0;
        bool bad = shown && (text.Text == parts.Forced || Failed.Any(f => text.Text.Contains(f, StringComparison.OrdinalIgnoreCase)));
        bool empty = shown && !bad && Empty.Any(f => text.Text.StartsWith(f, StringComparison.Ordinal));

        box.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        mark.Visibility = bad || empty ? Visibility.Visible : Visibility.Collapsed;
        mark.Tag = bad ? "Failed" : "Off";

        if (bad)
        {
            box.SetResourceReference(Border.BackgroundProperty, "DangerSoft");
            box.SetResourceReference(Border.BorderBrushProperty, "DangerLine");
            box.SetResourceReference(Border.BorderThicknessProperty, "Stroke");
            box.SetResourceReference(Border.CornerRadiusProperty, "RMd");
            box.Padding = new Thickness(12, 8, 12, 8);
            return;
        }

        box.ClearValue(Border.BackgroundProperty);
        box.ClearValue(Border.BorderBrushProperty);
        box.ClearValue(Border.BorderThicknessProperty);
        box.ClearValue(Border.CornerRadiusProperty);
        box.ClearValue(Border.PaddingProperty);
    }
}
