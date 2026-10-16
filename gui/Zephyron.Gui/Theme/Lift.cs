using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Zephyron.Gui;

/// <summary>
/// Карточка приподнимается под курсором: чуть вверх и чуть светлее рамкой.
/// </summary>
/// <remarks>
/// <para>
/// Из макета владельца 15.10 (там <c>.mod:hover</c>): курсор над карточкой —
/// та отвечает, и видно, что она живая, а не нарисованная. Поднимается
/// на точку-полторы, не больше: движение должно читаться уголком глаза
/// и не превращать список в карусель.
/// </para>
/// <para>
/// Признак карточки, а не разметка: тот же приём, что у стекла
/// (<see cref="Glass"/>). Стиль — общий на полтора десятка разделов, и класть
/// триггеры в каждый значило бы чинить каждую правку столько же раз.
/// Включается присоединённым свойством, чтобы подъём был там, где он уместен:
/// у строк списков и полей он мешал бы.
/// </para>
/// <para>
/// Движение — под общим выключателем анимаций (<see cref="Motion.Enabled"/>):
/// кто их выключил, тот выключил и это. Выключено — подъём не заводится вовсе.
/// </para>
/// <para>
/// Сдвиг свой собственный (<see cref="TranslateTransform"/>), и только если
/// элементу не назначен чужой: <see cref="Motion.Arrive"/> и
/// <see cref="Motion.Page"/> двигают появление, и затирать их трансформ нельзя.
/// Заодно это значит, что появление и подъём не спорят за одно свойство.
/// </para>
/// <para>
/// В теме со стеклом подъёма нет — так же, как нет и сдвига у появления
/// раздела (<see cref="Motion.Arrive"/>). Кусок размытого фона под карточкой
/// (<see cref="Glass"/>) привязан к её месту и пересчитывается на перекладке
/// окна, а сдвиг трансформом перекладки не вызывает: карточка поехала бы,
/// стекло осталось, и на 140 мс под курсором было бы видно шов. Ровно эту
/// беду чинили 28.09 у появления — повторять её ради точки-полутора незачем.
/// </para>
/// </remarks>
public static class Lift
{
    /// <summary>На сколько поднимается карточка.</summary>
    private const double Rise = 1.5;

    /// <summary>Длительность подъёма и возврата.</summary>
    private static readonly TimeSpan Time = TimeSpan.FromMilliseconds(140);

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(Lift), new PropertyMetadata(false, OnIsOnChanged));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.MouseEnter += OnEnter;
            element.MouseLeave += OnLeave;
        }
        else
        {
            element.MouseEnter -= OnEnter;
            element.MouseLeave -= OnLeave;
            Down(element, animate: false);
        }
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            Up(element);
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            Down(element, animate: true);
    }

    /// <summary>Поднимает карточку. Трансформ заводится свой, чужой не трогается.</summary>
    private static void Up(FrameworkElement element)
    {
        if (!Motion.Enabled)
            return;

        // В теме со стеклом не двигаем: сдвиг трансформом не вызывает
        // перекладки, а стекло под карточкой за ней не едет (Glass.cs).
        if (Glass.Image is not null)
            return;

        // Занятый трансформ — не наш: им двигает появление раздела
        // (Motion.Arrive) или стекло. Молча уступаем, а не перебиваем.
        if (element.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or TranslateTransform))
            return;

        var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = shift;

        shift.BeginAnimation(TranslateTransform.YProperty, Slide(-Rise));
    }

    /// <summary>Возвращает карточку на место.</summary>
    private static void Down(FrameworkElement element, bool animate)
    {
        if (element.RenderTransform is not TranslateTransform shift)
            return;

        if (!animate || !Motion.Enabled)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 0;

            // Свой трансформ снимаем, если он остался единичным: иначе он
            // держался бы на карточке до конца её жизни и мешал бы появлению
            // завести свой (Motion.Arrive проверяет тот же вид).
            if (shift.X == 0)
                element.RenderTransform = Transform.Identity;

            return;
        }

        var back = Slide(0);

        // По возврате анимация снимается: держать её дальше значило бы
        // оставить на карточке удерживаемое значение вместо обычного.
        back.Completed += (_, _) =>
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = 0;

            if (shift.X == 0)
                element.RenderTransform = Transform.Identity;
        };

        shift.BeginAnimation(TranslateTransform.YProperty, back);
    }

    /// <summary>
    /// Плавный сдвиг по вертикали.
    /// </summary>
    /// <remarks>
    /// Значение удерживается (<see cref="FillBehavior.HoldEnd"/>), а не
    /// отпускается: отпущенное вернулось бы к обычному сразу по окончании,
    /// и карточка не стояла бы поднятой, пока на неё смотрят.
    /// </remarks>
    private static DoubleAnimation Slide(double to) => new()
    {
        To = to,
        Duration = Time,
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.HoldEnd,
    };
}
