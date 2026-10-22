using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Zephyron.Gui.Views;

/// <summary>
/// Журналы движков.
/// </summary>
/// <remarks>
/// <para>
/// Не свалка, а источник. Именно журнал sing-box ответил на вопрос, который
/// мы разбирали неделю: имя он разобрал верно — значит рукопожатие дошло
/// целым и десинк его не портил, — а выход, через который шло, оказался
/// не тем, что назван в шапке отчёта.
/// </para>
/// <para>
/// Читается хвост: журнал вырастает до мегабайтов за сутки, а занимает нас
/// то, что было сейчас.
/// </para>
/// </remarks>
public partial class LogView : UserControl
{
    /// <summary>Сколько последних строк показывать.</summary>
    private const int Tail = 600;

    private bool _errorsOnly;

    /// <summary>Как часто открытый журнал перечитывает файл.</summary>
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(2);

    private readonly System.Windows.Threading.DispatcherTimer _follow = new() { Interval = FollowInterval };

    /// <summary>
    /// Откуда считаются пути журналов; пусто — рабочий каталог (корень установки).
    /// </summary>
    /// <remarks>Задаётся тестами: менять рабочий каталог процесса посреди параллельных тестов нельзя.</remarks>
    internal string Root { get; set; } = string.Empty;

    public LogView()
    {
        InitializeComponent();

        // Журнал идёт следом за файлом, пока его видно (0.16): прежде он
        // показывал снимок на момент открытия, и свежие строки появлялись
        // только по «Обновить».
        _follow.Tick += (_, _) =>
        {
            if (IsVisible)
                Reload(follow: true);
        };

        Loaded += (_, _) =>
        {
            LogsSwitch.IsChecked = Core.AppSettings.Load(Core.AppSettings.DefaultPath).LogsEnabled;
            Reload();
            _follow.Start();
        };

        Unloaded += (_, _) => _follow.Stop();
    }

    /// <summary>«Вести журнал» — записать и предложить перезапуск: движки читают её при запуске.</summary>
    private void OnLogsSwitch(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = Core.AppSettings.Load(Core.AppSettings.DefaultPath);
            var next = settings with { LogsEnabled = LogsSwitch.IsChecked == true };

            next.Save(Core.AppSettings.DefaultPath);
            this.Offer(next.LogsEnabled ? "Журнал включён" : "Журнал выключен");
        }
        catch (Exception ex)
        {
            LogsSwitch.IsChecked = !LogsSwitch.IsChecked;
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private string Path(int source) => System.IO.Path.Combine(Root, source switch
    {
        0 => System.IO.Path.Combine("runtime", "sing-box.log"),
        1 => System.IO.Path.Combine("runtime", "winws2.log"),
        2 => System.IO.Path.Combine("runtime", "supervisor.log"),
        _ => System.IO.Path.Combine("logs", "gui.log"),
    });

    /// <summary>Строка с ошибкой или предупреждением — для «Только ошибки».</summary>
    /// <remarks>
    /// Слова движков (ERROR, FATAL, WARN) и свои слова супервизора и окна:
    /// они пишут журнал по-русски, и по одним английским словам «Только ошибки»
    /// у них показывало пустой журнал при живых сбоях.
    /// </remarks>
    internal static bool IsTrouble(string line) =>
        TroubleWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] TroubleWords =
    [
        "ERROR", "FATAL", "WARN", "PANIC",
        // Супервизор (ProcessSupervisor): «не запустился», «процесс исчез»,
        // «трафик не идёт», «проверка не прошла», «сдаюсь», «перезапуск #N».
        "ошибк", "не запустил", "исчез", "не идёт", "не прошла", "сдаюсь", "перезапуск #",
        "не удалось", "не отвечает", "недоступ",
    ];

    /// <param name="follow">Перечитывание по таймеру: без лишней работы, когда ничего не изменилось, и без рывка к концу, если человек читает выше.</param>
    internal void Reload(bool follow = false)
    {
        var path = Path(Source.SelectedIndex);

        if (!File.Exists(path))
        {
            Text.Text = string.Empty;
            Status.Text = $"Журнала нет: {path}. Он появится, когда движок запустится.";

            return;
        }

        try
        {
            var lines = ReadTail(path, Tail).Select(Strip);

            if (_errorsOnly)
                lines = lines.Where(IsTrouble);

            var shown = lines.ToList();
            var text = string.Join('\n', shown);

            if (follow && text == Text.Text)
                return;

            // Внизу ли человек сейчас: туда и вернём. Читающего выше не дёргаем.
            bool atEnd = !follow || Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;

            Text.Text = text;

            var size = new FileInfo(path).Length / 1024.0;
            Status.Text = $"{path} — {size:N0} КБ, показаны последние {shown.Count} строк"
                + (_errorsOnly ? " с ошибками." : ".");

            // Вниз, к свежему: журнал читают ради последнего, а не первого.
            if (atEnd)
                Scroller.ScrollToBottom();
        }
        catch (Exception ex)
        {
            Status.Text = "Журнал не читается: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Последние строки файла, который движок держит открытым.
    /// </summary>
    /// <remarks>
    /// Общий доступ обязателен: без него чтение отказывало бы всегда — то есть
    /// ровно тогда, когда журнал и нужен, при работающем движке.
    /// </remarks>
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

    /// <summary>
    /// Снимает управляющие последовательности цвета.
    /// </summary>
    /// <remarks>
    /// Движок красит вывод даже при записи в файл, и без очистки строка
    /// приходит с ESC[31m посреди слова.
    /// </remarks>
    private static string Strip(string line) =>
        Regex.Replace(line, @"\x1B\[[0-9;]*m", string.Empty);

    /// <summary>Показать крестик «Свернуть» — у панели журнала в главном окне.</summary>
    public bool ShowClose
    {
        get => CloseButton.Visibility == Visibility.Visible;
        set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Нажат крестик «Свернуть».</summary>
    public event EventHandler? CloseRequested;

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Перечитать хвост — панель зовёт при каждом открытии: журнал растёт и пока она свёрнута.</summary>
    public void Refresh()
    {
        if (IsLoaded)
            Reload();
    }

    /// <summary>Выбранный журнал: 0 — sing-box, 1 — winws2, 2 — супервизор, 3 — окно.</summary>
    internal int Journal
    {
        get => Source.SelectedIndex;
        set => Source.SelectedIndex = value;
    }

    /// <summary>Показывать только строки с ошибками — то же, что кнопка «Только ошибки».</summary>
    internal bool ErrorsOnly
    {
        get => _errorsOnly;
        set
        {
            if (value != _errorsOnly)
                OnErrors(this, new RoutedEventArgs());
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnSource(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            Reload();
    }

    private void OnErrors(object sender, RoutedEventArgs e)
    {
        _errorsOnly = !_errorsOnly;
        ErrorsButton.Content = _errorsOnly ? "Все строки" : "Только ошибки";

        Reload();
    }
}
