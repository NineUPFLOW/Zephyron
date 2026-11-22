using System.Windows;

namespace Zephyron.Gui;

/// <summary>
/// Состояние раздела справа от пункта бокового меню: «Вкл», «32 маршрута», «DNSCrypt».
/// </summary>
/// <remarks>
/// По макету 23.11: по меню сразу видно, что включено, не заходя в раздел.
/// <see cref="OnProperty"/> — значение «включено»: оно светится акцентом, прочие приглушены.
/// </remarks>
public static class SideValue
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SideValue), new PropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached(
        "On", typeof(bool), typeof(SideValue), new PropertyMetadata(false));

    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);

    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);
}
