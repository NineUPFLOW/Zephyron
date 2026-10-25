using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Zephyron.Gui;

/// <summary>
/// Заголовок окна Windows — цветами темы (0.18): тёмный или светлый режим,
/// строка заголовка цвета подложки и рамка окна цвета акцента.
/// </summary>
/// <remarks>
/// <para>
/// До 0.18 так делало только главное окно, и то лишь включало тёмный режим:
/// семнадцать диалогов открывались с белой системной строкой заголовка над
/// неоновым окном — будто из другой программы. Теперь свойство ставит общий
/// стиль окна (<c>Dialog</c>), и после смены темы заголовки перекрашиваются
/// у всех открытых окон (<see cref="RefreshAll"/>).
/// </para>
/// <para>
/// Цвет строки и рамки — атрибуты Windows 11 (34, 35); Windows 10 их молча
/// не принимает, и там остаётся тёмный режим. Ошибка Windows окну не мешает:
/// оформление не стоит того, чтобы из-за него не открылось окно.
/// </para>
/// </remarks>
public static class TitleBar
{
    private const int ImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static readonly DependencyProperty ThemedProperty = DependencyProperty.RegisterAttached(
        "Themed", typeof(bool), typeof(TitleBar), new PropertyMetadata(false, OnThemed));

    public static bool GetThemed(DependencyObject element) => (bool)element.GetValue(ThemedProperty);

    public static void SetThemed(DependencyObject element, bool value) => element.SetValue(ThemedProperty, value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private static void OnThemed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
            return;

        if (new WindowInteropHelper(window).Handle != 0)
            Paint(window);
        else
            window.SourceInitialized += (_, _) => Paint(window);
    }

    /// <summary>Перекрашивает заголовки всех открытых окон — после смены темы.</summary>
    public static void RefreshAll()
    {
        // Список окон доступен только из потока приложения; тема, применённая
        // из чужого потока (тесты), своих окон заголовками не перекрасит.
        if (Application.Current is not { } app || !app.Dispatcher.CheckAccess())
            return;

        foreach (Window window in app.Windows)
        {
            if (GetThemed(window))
                Paint(window);
        }
    }

    private static void Paint(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == 0)
                return;

            var backdrop = Color(window, "BackdropColor", Colors.Black);
            var accent = Color(window, "AccentColor", Colors.SlateBlue);
            var text = Color(window, "TextColor", Colors.White);

            int dark = Luminance(backdrop) < 0.5 ? 1 : 0;
            DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref dark, sizeof(int));

            int caption = ColorRef(backdrop);
            int border = ColorRef(accent);
            int title = ColorRef(text);
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
            DwmSetWindowAttribute(handle, TextColor, ref title, sizeof(int));
        }
        catch (Exception)
        {
            // Оформление не стоит того, чтобы из-за него не открылось окно.
        }
    }

    private static Color Color(FrameworkElement element, string key, Color fallback) =>
        element.TryFindResource(key) is Color color ? color : fallback;

    /// <summary>COLORREF Windows: 0x00BBGGRR.</summary>
    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
}
