using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zephyron.Gui.Views;

/// <summary>Строка ленты событий: время, кто писал, что случилось и цвет смысла.</summary>
public sealed record EventRow(string Time, string Source, string Message, Brush Tone)
{
    public string Full => $"{Time}  {Source} {Message}";
}

/// <summary>
/// Лента «События» на «Главной»: последние строки общего журнала.
/// </summary>
/// <remarks>
/// <para>
/// Журнал один на надзор и окно (<see cref="Journal"/>, runtime\supervisor.log),
/// и в нём уже есть всё, что стоит показать: запуск, остановка, падения движков,
/// правки. Своей истории событий лента не ведёт — показывает файл как есть.
/// </para>
/// <para>
/// Читается хвост файла, а не файл целиком: журнал растёт мегабайтами, а нужны
/// последние строки. Чтение — в пуле, раз в четыре секунды и только пока раздел
/// на экране; файл открыт с общим доступом, писать надзору он не мешает.
/// </para>
/// </remarks>
public partial class StatusView
{
    private static readonly TimeSpan EventsInterval = TimeSpan.FromSeconds(4);

    /// <summary>Сколько последних событий показывать.</summary>
    private const int EventsShown = 6;

    /// <summary>Сколько байтов с конца файла читать: с запасом на шесть длинных строк.</summary>
    private const int TailBytes = 16 * 1024;

    private static readonly Regex Line = new(@"^\[(?<time>\d{2}:\d{2}:\d{2})\]\s*(?<source>[^:]{1,40}):\s*(?<message>.+)$", RegexOptions.Compiled);

    private readonly DispatcherTimer _events = new() { Interval = EventsInterval };
    private bool _eventsBusy;
    private string? _eventsShown;

    private void StartEvents()
    {
        _events.Tick -= OnEvents;
        _events.Tick += OnEvents;
        _events.Start();
        OnEvents(null, EventArgs.Empty);
    }

    private void StopEvents() => _events.Stop();

    private async void OnEvents(object? sender, EventArgs e)
    {
        if (_eventsBusy || !IsVisible)
            return;

        _eventsBusy = true;

        try
        {
            var lines = await Task.Run(() => Tail(Path.Combine("runtime", "supervisor.log"), TailBytes, EventsShown));
            var key = string.Join("\n", lines);

            // Те же строки — не трогаем список: новые элементы на каждом тике
            // перестраивали бы шаблоны впустую.
            if (key == _eventsShown)
                return;

            _eventsShown = key;
            var rows = lines.Select(Parse).Where(r => r is not null).Reverse().ToList();

            Events.ItemsSource = rows;
            EventsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Журнала нет или он занят — лента остаётся прежней.
        }
        finally
        {
            _eventsBusy = false;
        }
    }

    /// <summary>Последние <paramref name="count"/> непустых строк файла.</summary>
    internal static IReadOnlyList<string> Tail(string path, int bytes, int count)
    {
        if (!File.Exists(path))
            return [];

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, file.Length - bytes);
        file.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(file, Encoding.UTF8);
        var all = reader.ReadToEnd().Split('\n');

        // Первая строка хвоста почти всегда обрезана началом окна чтения.
        var whole = start > 0 ? all.Skip(1) : all;

        return whole.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(count).ToList();
    }

    private EventRow? Parse(string line)
    {
        var match = Line.Match(line);

        if (!match.Success)
            return null;

        string message = match.Groups["message"].Value;
        return new EventRow(match.Groups["time"].Value, match.Groups["source"].Value + ":", message, (Brush)FindResource(Tone(message)));
    }

    /// <summary>Цвет смысла по словам строки: сбой красным, оговорка жёлтым, прочее — третьим планом.</summary>
    internal static string Tone(string message)
    {
        var text = message.ToLowerInvariant();

        // Отрицания раньше «Success»: «не работает» содержит «работает».
        if (text.Contains("ошибк") || text.Contains("упал") || text.Contains("не удал") || text.Contains("сбой")
            || text.Contains("не работает") || text.Contains("не запущен") || text.Contains("не поднят")
            || text.Contains("error") || text.Contains("failed") || text.Contains("exception"))
            return "Danger";

        if (text.Contains("предупрежд") || text.Contains("не ответ") || text.Contains("warn") || text.Contains("перезапуск"))
            return "Warn";

        if (text.Contains("запущен") || text.Contains("поднят") || text.Contains("работает"))
            return "Success";

        return "Faint";
    }
}
