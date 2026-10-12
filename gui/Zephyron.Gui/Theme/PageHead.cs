using System.Windows;

namespace Zephyron.Gui;

/// <summary>
/// Шапка страницы: имя раздела, подпись и место под кнопки справа.
/// </summary>
/// <remarks>
/// <para>
/// Имя и подпись задаются присоединёнными свойствами, а кнопки кладутся
/// содержимым: разметке не нужно повторять полосу, и разделы выходят
/// одной высоты и одного вида. Шаблон — стиль <c>PageHead</c>
/// (Theme/Controls.xaml).
/// </para>
/// <para>
/// Полоса, а не строка заголовка: у каждого раздела своя шапка с акцентной
/// чертой слева, и по ней видно, где вы. Фон — переход от карточки к
/// приподнятому: на обоих текст темы читается по проверке читаемости,
/// а отличимость даёт черта и крупный кегль.
/// </para>
/// </remarks>
public static class PageHead
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(PageHead), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.RegisterAttached(
        "Subtitle", typeof(string), typeof(PageHead), new PropertyMetadata(string.Empty));

    public static string GetTitle(DependencyObject element) => (string)element.GetValue(TitleProperty);

    public static void SetTitle(DependencyObject element, string value) => element.SetValue(TitleProperty, value);

    public static string GetSubtitle(DependencyObject element) => (string)element.GetValue(SubtitleProperty);

    public static void SetSubtitle(DependencyObject element, string value) => element.SetValue(SubtitleProperty, value);
}
