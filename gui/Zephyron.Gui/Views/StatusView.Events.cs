using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Zephyron.Gui.Views;

/// <summary>Строка ленты событий: время, кто писал, что случилось и цвет смысла.</summary>
public sealed record EventRow(string Time, string Source, string Message, Brush Tone, string Raw)
{
    /// <summary>Подсказка: строка журнала как есть — для разбора, лента показывает её словами.</summary>
    public string Full => Raw;
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
    private const int EventsShown = 4;

    /// <summary>Сколько байтов с конца файла читать: запуск пишет десяток служебных строк подряд.</summary>
    private const int TailBytes = 48 * 1024;

    // Окно пишет «[чч:мм:сс] кто: что», надзор — «гггг-мм-дд чч:мм:сс кто: что», а свои
    // строки («останавливаю службы») и вовсе без «кто». Без второго вида лента молчала
    // ровно о падениях движков.
    private static readonly Regex Line = new(
        @"^(?:\[(?<time>\d{2}:\d{2}:\d{2})\]|(?:\d{4}-\d{2}-\d{2}\s+)?(?<time>\d{2}:\d{2}:\d{2}))\s*(?:(?<source>[^:\s][^:]{0,39}):\s+)?(?<message>.+)$",
        RegexOptions.Compiled);

    /// <summary>Замер подвисания окна — для разбора в журнале, человеку в ленте это шум.</summary>
    internal static bool IsStallNote(string line) => line.Contains("окно: поток окна был занят", StringComparison.Ordinal);

    /// <summary>Время, источник и текст строки журнала; <c>null</c> — строка не из журнала.</summary>
    internal static (string Time, string Source, string Message)? Split(string line)
    {
        var match = Line.Match(line);

        if (!match.Success)
            return null;

        var source = match.Groups["source"];
        return (match.Groups["time"].Value, source.Success ? source.Value + ":" : "надзор:", match.Groups["message"].Value);
    }

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

    private void OnEvents(object? sender, EventArgs e)
    {
        if (_eventsBusy || !IsVisible)
            return;

        _eventsBusy = true;

        // Чтение держит раздел только слабой ссылкой: закрытый раздел не должен жить,
        // пока в пуле дочитывается журнал (тест утечек QaSignalTests, CI 10.11).
        var view = new WeakReference<StatusView>(this);

        Task.Run(ReadEvents).ContinueWith(
            read =>
            {
                if (view.TryGetTarget(out var self))
                    self.ShowEvents(read.IsCompletedSuccessfully ? read.Result : null);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Последние живые строки ленты; служебные отсеиваются (Plain), показать надо четыре.</summary>
    private static List<string> ReadEvents() =>
        Tail(Path.Combine("runtime", "supervisor.log"), TailBytes, 200)
            .Where(l => !IsStallNote(l) && Split(l) is var (_, src, msg) && Plain(src, msg) is not null)
            .TakeLast(EventsShown)
            .ToList();

    private void ShowEvents(List<string>? lines)
    {
        _eventsBusy = false;

        // Журнала нет или он занят — лента остаётся прежней.
        if (lines is null)
            return;

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
        if (Split(line) is not var (time, source, message) || Plain(source, message) is not var (who, what))
            return null;

        return new EventRow(time, who, what, (Brush)FindResource(Tone(message)), line);
    }

    private static readonly Regex Pid = new(@",?\s*PID\s*\d+", RegexOptions.Compiled);
    private static readonly Regex Hosts = new(@":\s*(?<list>[\w.\-]+(?:,\s*[\w.\-]+)*)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Строка журнала словами человека: кто — по-русски, что — без PID, путей и терминов;
    /// <c>null</c> — служебная строка, которой в ленте не место (рецепты, пресет, исключения перехвата).
    /// </summary>
    /// <remarks>Владелец 10.11: «"События" сделай более понятными для пользователей».</remarks>
    internal static (string Source, string Message)? Plain(string source, string message)
    {
        var who = source.TrimEnd(':');

        static bool Starts(string text, string head) => text.StartsWith(head, StringComparison.OrdinalIgnoreCase);

        // Кухня запуска: что подхвачено и что исключено из перехвата. Полностью — в «Весь журнал».
        // Разбор строки мог принять начало фразы до двоеточия за «кто» — смотрим строку целиком.
        var whole = who == "надзор" ? message : who + ": " + message;

        if (Starts(whole, "Свой рецепт") || Starts(whole, "Пресет:") || Starts(whole, "Десинк ")
            || Starts(whole, "Служб под присмотром") || Starts(whole, "супервизор запущен") || Starts(whole, "останавливаю службы")
            || Starts(whole, "Подделки секций") || Starts(whole, "Перехват:") || Starts(whole, "Секция «"))
            return null;

        // «запуск» каждого движка тут же сменяется «работает» — второе и показываем.
        if (who is "winws2" or "sing-box" or "tg-ws-proxy" or "dnscrypt-proxy" && message == "запуск")
            return null;

        if (who == "пин" && message.Contains("менять нечего", StringComparison.Ordinal))
            return null;

        var name = who switch
        {
            "winws2" => "Десинк",
            "sing-box" => "Туннель (VPN)",
            "tg-ws-proxy" => "Прокси Telegram",
            "dnscrypt-proxy" => "Шифрованный DNS",
            "надзор" or "движки" or "запуск" => "Zephyron",
            "видео YouTube" => "YouTube",
            "подписка" => "Подписка",
            "пин" => "Пины",
            "окно" => "Zephyron",
            _ => char.ToUpper(who[0]) + who[1..],
        };

        string Count(string text) =>
            Hosts.Match(text) is { Success: true } m ? m.Groups["list"].Value.Split(',').Length.ToString() : "";

        var text = message switch
        {
            _ when Starts(message, "работает") => "работает",
            "супервизор остановлен" => "обход выключен",
            _ when who == "движки" && Starts(message, "запуск") => "обход включён" + Reason(message),
            _ when who == "движки" && Starts(message, "остановка") => "обход выключен" + Reason(message),
            _ when who == "движки" && Starts(message, "перезапуск") => "обход перезапущен" + Reason(message),
            _ when who == "видео YouTube" && message.Contains("ожили под малым окном", StringComparison.Ordinal)
                => $"починены узлы видео ({Count(message)}), ролики идут без VPN",
            _ when who == "видео YouTube" && message.Contains("отключены", StringComparison.Ordinal)
                => $"неработающие узлы видео ({Count(message)}) пропущены, плеер берёт другие",
            _ when who == "видео YouTube" && message.Contains("возвращены", StringComparison.Ordinal)
                => $"узлы видео снова в строю ({Count(message)})",
            _ when who == "подписка" && message.Contains("запуск из запаса", StringComparison.Ordinal)
                => message[..message.IndexOf(": запуск", StringComparison.Ordinal)] + " — серверы из сохранённой копии, панель спрошу позже",
            _ when who == "подписка" && message.Contains("перечитана", StringComparison.Ordinal)
                => message[..message.IndexOf(" перечитана", StringComparison.Ordinal)] + " обновлена",
            _ when who == "окно" && Starts(message, "уведомление после обновления до ")
                => "программа обновлена до " + message["уведомление после обновления до ".Length..],
            _ when who == "запуск" && Starts(message, "движки подняты с попытки ")
                => "обход включился с " + message["движки подняты с попытки ".Length..] + "-й попытки",
            _ => Pid.Replace(message, ""),
        };

        return (name + ":", text);

        // «кнопка «Запустить» на «Главной»» → « (кнопкой)»; прочие причины — как есть, коротко.
        static string Reason(string m)
        {
            int dash = m.IndexOf(" — ", StringComparison.Ordinal);

            if (dash < 0)
                return "";

            var why = m[(dash + 3)..];

            return why.StartsWith("кнопка", StringComparison.Ordinal) || why.StartsWith("«", StringComparison.Ordinal)
                ? " кнопкой"
                : $" ({why})";
        }
    }

    /// <summary>Цвет смысла по словам строки: сбой красным, оговорка жёлтым, прочее — третьим планом.</summary>
    internal static string Tone(string message)
    {
        var text = message.ToLowerInvariant();

        // Отрицания раньше «Success»: «не работает» содержит «работает».
        if (text.Contains("ошибк") || text.Contains("упал") || text.Contains("не удал") || text.Contains("сбой")
            || text.Contains("не работает") || text.Contains("не запущен") || text.Contains("не запустил") || text.Contains("не поднят")
            || text.Contains("исчез")
            || text.Contains("error") || text.Contains("failed") || text.Contains("exception"))
            return "Danger";

        if (text.Contains("предупрежд") || text.Contains("не ответ") || text.Contains("warn") || text.Contains("перезапуск"))
            return "Warn";

        if (text.Contains("запущен") || text.Contains("поднят") || text.Contains("работает"))
            return "Success";

        return "Faint";
    }
}
