using System.Windows;
using Zephyron.Core;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Zephyron.Gui;

/// <summary>
/// Неоновый ореол цвета акцента вокруг элемента (0.18).
/// </summary>
/// <remarks>
/// <para>
/// Своё свойство, а не <c>DropShadowEffect</c> в стиле. Эффект в стиле — один
/// объект на все элементы, и с цветом из темы (DynamicResource) его нельзя
/// заморозить: WPF отказывается делить такой объект между потоками, и окно
/// падало при разборе (тесты, 24.10). Здесь у каждого элемента свой эффект,
/// а цвет приходит ссылкой на ресурс самого элемента — смена темы
/// перекрашивает ореол вживую.
/// </para>
/// <para>
/// Радиус ноль — ореола нет: так его гасит триггер стиля (недоступная кнопка).
/// </para>
/// </remarks>
public static class Glow
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Glow), new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.RegisterAttached(
        "Strength", typeof(double), typeof(Glow), new PropertyMetadata(0.5, OnChanged));

    /// <summary>Цвет ореола — ссылкой на ресурс AccentColor элемента, ставится сам.</summary>
    private static readonly DependencyProperty ColorProperty = DependencyProperty.RegisterAttached(
        "Color", typeof(Color), typeof(Glow), new PropertyMetadata(Colors.Transparent, OnChanged));

    /// <summary>Элемент уже в списке тех, кого будит смена профиля.</summary>
    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "Tracked", typeof(bool), typeof(Glow), new PropertyMetadata(false));

    // Слабые ссылки: ореол не должен держать элемент, окно пересоздаётся из трея.
    private static readonly List<WeakReference<FrameworkElement>> Tracked = [];

    static Glow() => Motion.Changed += (_, _) => Refresh();

    /// <summary>Профиль или выключатель сменились: ореолы гаснут (Lite) или возвращаются.</summary>
    private static void Refresh()
    {
        FrameworkElement[] alive;

        lock (Tracked)
        {
            Tracked.RemoveAll(r => !r.TryGetTarget(out _));
            alive = [.. Tracked.Select(r => r.TryGetTarget(out var e) ? e : null).OfType<FrameworkElement>()];
        }

        foreach (var element in alive)
        {
            if (element.CheckAccess())
                Apply(element);
            else
                element.Dispatcher.BeginInvoke(() => Apply(element));
        }
    }

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    public static double GetStrength(DependencyObject element) => (double)element.GetValue(StrengthProperty);

    public static void SetStrength(DependencyObject element, double value) => element.SetValue(StrengthProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element)
            Apply(element);
    }

    private static void Apply(FrameworkElement element)
    {
        // В Lite ореола нет: размытие тени — самая дорогая часть неона на слабом видео.
        double radius = Motion.Profile == EffectsProfile.Lite ? 0 : GetRadius(element);

        // Учёт по заданному радиусу, а не по обнулённому для Lite: иначе после Lite свечение не вернётся.
        if (GetRadius(element) > 0 && !(bool)element.GetValue(TrackedProperty))
        {
            element.SetValue(TrackedProperty, true);

            lock (Tracked)
                Tracked.Add(new WeakReference<FrameworkElement>(element));
        }

        if (radius <= 0)
        {
            if (element.Effect is DropShadowEffect)
                element.ClearValue(UIElement.EffectProperty);

            return;
        }

        // Цвет — ссылкой на ресурс один раз: дальше он сам следует за темой.
        if (element.ReadLocalValue(ColorProperty) == DependencyProperty.UnsetValue)
            element.SetResourceReference(ColorProperty, "AccentColor");

        if (element.Effect is not DropShadowEffect effect || effect.IsFrozen)
        {
            effect = new DropShadowEffect { ShadowDepth = 0 };
            element.Effect = effect;
        }

        effect.BlurRadius = radius;
        effect.Opacity = GetStrength(element);
        effect.Color = (Color)element.GetValue(ColorProperty);
    }
}
