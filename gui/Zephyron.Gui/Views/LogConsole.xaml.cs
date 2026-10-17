using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zephyron.Gui.Views;

/// <summary>
/// Журнал событий поверх окна — консоль внизу и кнопка-таблетка справа.
/// </summary>
/// <remarks>
/// <para>
/// Из макета владельца (<c>.log-con</c> и <c>.log-fab</c>): кнопка внизу
/// справа, по нажатию выезжает консоль с последними строками. Источник —
/// тот же общий журнал, что у супервизора и окна «Журнал»
/// (<see cref="Journal"/>): второго журнала ради консоли не заводим, иначе
/// история одного запуска разложилась бы по двум файлам.
/// </para>
/// <para>
/// Пока консоль открыта, хвост файла перечитывается раз в две секунды —
/// как и прочие показания окна. Это чтение, а не запись: строки кладёт туда
/// тот, кто их и клал.
/// </para>
/// </remarks>
public partial class LogConsole : UserControl
{
    /// <summary>Сколько строк показывать: столько же держит окно «Журнал».</summary>
    private const int Tail = 150;

    /// <summary>Период перечитывания открытой консоли.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _timer = new() { Interval = Interval };

    private bool _open;

    public LogConsole()
    {
        InitializeComponent();

        _timer.Tick += (_, _) => Reload();
    }

    private static string Path => System.IO.Path.Combine("runtime", "supervisor.log");

    /// <summary>Открывает или закрывает консоль.</summary>
    /// <remarks>
    /// Высота сдвига берётся у самой консоли, а не числом: разметка задаёт
    /// её одним местом, и менять её придётся тоже в одном.
    /// </remarks>
    private void OnToggle(object sender, RoutedEventArgs e)
    {
        _open = !_open;

        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.Y = _open ? 0 : Console.Height;

        Fab.Margin = _open ? new Thickness(0, 0, 20, Console.Height + 16) : new Thickness(0, 0, 20, 20);
        FabGlyph.Text = _open ? "\uE70E" : "\uE70D";
        FabText.Text = _open ? "Скрыть" : "Журнал";

        if (_open)
        {
            Reload();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>Очищает показанное — файл не трогает, его ведёт супервизор.</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        Lines.Items.Clear();
        FabCountText.Text = "0";
        FabCount.Visibility = Visibility.Collapsed;
    }

    /// <summary>Перечитывает хвост журнала и раскладывает строки по уровню.</summary>
    private void Reload()
    {
        if (!File.Exists(Path))
        {
            Lines.Items.Clear();
            FabCountText.Text = "0";
            FabCount.Visibility = Visibility.Collapsed;

            return;
        }

        try
        {
            var shown = ReadTail(Path, Tail).Select(Strip).ToList();

            Lines.Items.Clear();

            foreach (var line in shown)
                Lines.Items.Add(Line(line));

            Scroller.ScrollToBottom();

            FabCountText.Text = shown.Count.ToString();
            FabCount.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Журнал занят записью — покажем то, что было, и повторим на следующем тике.
        }
    }

    /// <summary>Строка консоли: время, метка уровня и сам текст.</summary>
    private FrameworkElement Line(string text)
    {
        var level = Level(text);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stamp = Time(text);

        var ts = new TextBlock
        {
            Text = stamp,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            Foreground = (Brush)FindResource("Faint"),
        };

        var badge = new TextBlock
        {
            Text = level,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 9.5,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)FindResource(Colour(level)),
        };

        var body = new TextBlock
        {
            Text = Body(text),
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = (Brush)FindResource("Muted"),
        };

        Grid.SetColumn(badge, 1);
        Grid.SetColumn(body, 2);

        grid.Children.Add(ts);
        grid.Children.Add(badge);
        grid.Children.Add(body);

        return grid;
    }

    private static string Colour(string level) => level switch
    {
        "OK" => "Ok",
        "WRN" => "Warn",
        "ERR" => "Danger",
        "EVT" => "Purple",
        _ => "Accent",
    };

    /// <summary>Уровень строки по её словам — словами же, что в макете.</summary>
    private static string Level(string text)
    {
        if (Contains(text, "ERROR") || Contains(text, "FATAL") || Contains(text, "не удалось"))
            return "ERR";

        if (Contains(text, "WARN") || Contains(text, "предупреж"))
            return "WRN";

        if (Contains(text, "запущен") || Contains(text, "готов") || Contains(text, "работает"))
            return "OK";

        return "INF";
    }

    private static bool Contains(string text, string word) =>
        text.Contains(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Время строки: своё, если оно есть, иначе пусто.</summary>
    private static string Time(string text)
    {
        // Два вида строк: «2026-10-17 00:00:00 …» от запуска и «[00:00:00] …»
        // от окна (Journal). Берём то, что нашли, — выдумывать нечего.
        var full = Regex.Match(text, @"\d{4}-\d{2}-\d{2} (\d{2}:\d{2}:\d{2})");
        if (full.Success)
            return full.Groups[1].Value;

        var shortStamp = Regex.Match(text, @"\[(\d{2}:\d{2}:\d{2})\]");
        return shortStamp.Success ? shortStamp.Groups[1].Value : string.Empty;
    }

    /// <summary>Текст без служебной даты-времени в начале.</summary>
    private static string Body(string text) =>
        Regex.Replace(text, @"^\[?\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]?\s*", string.Empty);

    /// <summary>Снимает управляющие последовательности цвета (как в «Журнале»).</summary>
    private static string Strip(string line) =>
        Regex.Replace(line, @"\x1B\[[0-9;]*m", string.Empty);

    /// <summary>Последние строки файла, который движок держит открытым.</summary>
    private static string[] ReadTail(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var ring = new string[count];
        int written = 0;

        while (reader.ReadLine() is { } line)
            ring[written++ % count] = line;

        if (written <= count)
            return ring.Take(written).ToArray();

        return Enumerable.Range(0, count).Select(i => ring[(written + i) % count]).ToArray();
    }
}
