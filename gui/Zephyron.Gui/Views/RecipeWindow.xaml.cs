using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zephyron.Core;
using Zephyron.Proxy;
using Zephyron.Supervisor;
using Zephyron.Zapret;

namespace Zephyron.Gui.Views;

/// <summary>
/// Строка одного рецепта в окне выбора.
/// </summary>
/// <remarks>
/// Класс с уведомлениями, а не запись. Перебор рецептов меняет вердикт
/// у каждой строки по очереди, и прежде для показа этого пересоздавался весь
/// <c>ItemsSource</c> — присваиванием <c>null</c> и обратно. WPF на этом падал:
/// подмена приходила из продолжения задачи и попадала в середину раскладки,
/// а <c>StackPanel</c> в этот момент уже держал в руках прежний список.
/// Отсюда «ArgumentOutOfRangeException: Parameter 'index'» посреди проверки.
/// </remarks>
public sealed class RecipeRow : INotifyPropertyChanged
{
    /// <summary>Что запишется в правило.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Шаги рецепта — те самые, что уйдут в <c>--lua-desync</c>.
    /// </summary>
    /// <remarks>
    /// Несёт строка, а не ищутся по имени в пресете. Поиск по имени
    /// работал, пока рецепты брались только оттуда, и уже тогда подводил:
    /// имя группы — это имя её первой секции, и перестановка секций
    /// обесценивала сохранённый выбор молча. Теперь источников два,
    /// и второго в пресете нет вовсе.
    /// </remarks>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Что показывается в заголовке: приёмы, а не сервис.</summary>
    public required string Title { get; init; }

    /// <summary>Набор целиком, с настройками.</summary>
    public required string Summary { get; init; }

    /// <summary>Где ещё этот набор применяется — чтобы судить по знакомому.</summary>
    public required string UsedBy { get; init; }

    /// <summary>
    /// За сколько имя открылось; <c>null</c> — не открылось или не мерили.
    /// </summary>
    /// <remarks>
    /// Нужно затем, что рабочих рецептов обычно несколько, а совет был
    /// «берите любой из отмеченных». Любой — значит гадать. Время отвечает
    /// на это замером: рецепт, при котором рукопожатие проходит вдвое
    /// быстрее, и в работе будет вести себя лучше.
    /// </remarks>
    public TimeSpan? Took { get; set; }

    /// <summary>
    /// Этот набор пресет и так применяет к этому имени.
    /// </summary>
    /// <remarks>
    /// Без пометки человек выбирает вслепую то, что уже действует, и потом
    /// не понимает, почему ничего не изменилось. Считается тем же кодом,
    /// что решает судьбу имени в работе, — <see cref="PresetZones"/>.
    /// </remarks>
    public bool Current { get; init; }

    private string _verdict = string.Empty;
    private Brush _verdictColour = Brushes.Transparent;
    private Brush _edge = Brushes.Transparent;

    public string Verdict
    {
        get => _verdict;
        set => Set(ref _verdict, value);
    }

    public Brush VerdictColour
    {
        get => _verdictColour;
        set => Set(ref _verdictColour, value);
    }

    public Brush Edge
    {
        get => _edge;
        set => Set(ref _edge, value);
    }

    /// <summary>
    /// Какой рецепт советовать: быстрейший из тех, что открыли имя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь, а не в обработчике кнопки, потому что это единственная чистая
    /// часть окна и единственная, которую можно проверить тестом. Оставь её
    /// в обработчике — тест повторил бы выборку своими словами и проверял бы
    /// собственную копию, а не то, что увидит человек.
    /// </para>
    /// <para>
    /// Неработавшие не рассматриваются вовсе: у них время означает срок
    /// ожидания, а не скорость. Пустить их в сравнение значило бы советовать
    /// самый быстрый отказ.
    /// </para>
    /// </remarks>
    public static RecipeRow? Best(IEnumerable<RecipeRow> rows) =>
        rows.Where(r => r.Took is not null).OrderBy(r => r.Took!.Value).FirstOrDefault();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Выбор рецепта десинка для одного имени.
/// </summary>
/// <remarks>
/// <para>
/// Заведено потому, что <c>mode: desync</c> в маршрутах означал ровно одно —
/// «мимо туннеля». Чем чинить имя, решал пресет, и если оно не попадало
/// ни в один его список, не делалось ничего: снаружи это неотличимо
/// от неработающего десинка. Хуже того, попасть можно в секцию с рецептом
/// <c>pass</c> — она забирает имя себе и пропускает нетронутым.
/// </para>
/// <para>
/// Рецепты берутся из самого пресета: там они подобраны под живые блокировки
/// и обновляются вместе с ним. Свой перечень мы бы выдумали один раз
/// и оставили стареть.
/// </para>
/// </remarks>
public partial class RecipeWindow : Window
{
    private readonly string _domain;
    private readonly ZapretPreset _preset;
    private readonly List<RecipeRow> _rows = [];

    private CancellationTokenSource? _work;

    /// <summary>Куда стучаться приветствием; узнаётся раз на проверку.</summary>
    private IPAddress? _address;

    /// <summary>Что выбрали; <c>null</c> — окно закрыли отменой.</summary>
    public string? Chosen { get; private set; }

    /// <param name="title">Что показать в заголовке: имя или название сервиса.</param>
    /// <param name="domain">
    /// Имя, на котором проверяются рецепты. Для сервиса — любое из его списка.
    /// </param>
    /// <remarks>
    /// Разделены намеренно, и это исправление. Сервису в заголовок идёт
    /// «discord», а проверять на нём нечего: такого имени не существует,
    /// оно не разрешается, и каждый рецепт отвечал «не помогает» — все
    /// одиннадцать, включая заведомо рабочие.
    /// </remarks>
    /// <param name="presetOnlyIfKnown">
    /// Предлагать «решает пресет», только когда в пресете есть секция под это
    /// имя. Для своих доменов (владелец, 26.09): свой домен почти никогда
    /// не стоит ни в одном списке пресета, и «десинк» без рецепта оставлял
    /// его без обхода вовсе — а в строке стояло «десинк», как будто работает.
    /// </param>
    public RecipeWindow(string title, string domain, ZapretPreset preset, bool presetOnlyIfKnown = false)
    {
        InitializeComponent();

        _domain = domain;
        _preset = preset;

        Head.Text = $"Чем чинить {title}";

        if (!string.Equals(title, domain, StringComparison.OrdinalIgnoreCase))
            Head.Text += $" — проверяю на {domain}";

        // Какую секцию пресет отдаёт этому имени сам. Спрашивается тем же
        // кодом, что решает это в работе: winws2 отдаёт пакет первой
        // совпавшей секции, и ответ здесь обязан совпадать с тем, что
        // покажет отчёт проверки.
        string? already = null;

        try
        {
            already = PresetZones.Build(preset, ZapretPaths.Discover()?.Root)
                .MatchFor(domain)?.Name;
        }
        catch (Exception)
        {
            // Списки пресета могли не прочитаться. Пометка — удобство,
            // и её отсутствие не повод не дать выбрать рецепт.
        }

        // Приёмы, которые умеет движок. Нужны затем, чтобы из каталога
        // предлагать только то, что этот пресет потянет: рецепт с
        // неподключённым модулем winws2 не запустит вовсе, и проверка
        // ответила бы «не помогает» о приёме, которого не пробовали.
        IReadOnlyDictionary<string, string> providers =
            new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (ZapretPaths.Discover() is { } paths)
                providers = LuaModules.Providers(LuaModules.Scan(paths.LuaDirectory));
        }
        catch (Exception)
        {
            // Без приёмов останутся только наборы пресета — это не отказ,
            // а сужение выбора до заведомо рабочего.
        }

        foreach (var choice in RecipeChoices.Build(preset, providers, already))
        {
            _rows.Add(new RecipeRow
            {
                Name = choice.Name,
                Title = choice.Title,

                // Шаги несёт сама строка, а не ищутся потом по имени.
                // Поиск по имени уже стоил голоса Discord: правка пресета
                // переставила секции, имя группы сменилось, и сохранённый
                // выбор перестал находиться — молча.
                Steps = choice.Steps,

                // С местами переноса: шаг не содержит пробелов, и WPF,
                // не найдя где разорвать, рвал его посреди слова — в узком
                // окне строка вставала столбцом в пять букв.
                Summary = StepText.Of(choice.Steps),
                UsedBy = choice.Source,
                Current = choice.Current,
                Edge = (Brush)FindResource("Border"),
            });
        }

        if (_rows.FirstOrDefault(r => r.Current) is { } current)
        {
            current.Verdict = "сейчас применяется";
            current.VerdictColour = (Brush)FindResource("Muted");
        }

        Recipes.ItemsSource = _rows;

        Say(_rows.Count == 0
            ? "Ни одного рецепта не нашлось — ни в пресете, ни в каталоге."
            : $"Рецептов: {_rows.Count} — из пресета «{preset.Name}» и из каталога. "
              + "Проверка подбирает рабочий сама, но требует остановленных движков.");

        TestButton.IsEnabled = _rows.Count > 0;

        if (presetOnlyIfKnown)
        {
            if (already is null)
            {
                PresetButton.Visibility = Visibility.Collapsed;
                PresetNote.Text = $"Пресет «{preset.Name}» не знает {domain}: ни одна его секция это имя "
                    + "не берёт, и без рецепта десинк не сделал бы с ним ничего. Выберите рецепт — "
                    + "или «Отмена», и маршрут останется прежним.";
            }
            else
            {
                PresetNote.Text = $"«Решает пресет» — имя возьмёт его секция «{already}».";
            }
        }

        Closed += (_, _) => _work?.Cancel();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        Chosen = name;
        Close(true);
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        Chosen = DesyncRecipes.FromPreset;
        Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// Прерывает перебор, оставляя уже полученные ответы.
    /// </summary>
    /// <remarks>
    /// Отвечает немедленно, не дожидаясь, пока прерывание дойдёт до проб.
    /// Дойти оно может не сразу: текущий рецепт держит запущенный winws2,
    /// и его завершения ждут до четырёх секунд. Молчащая кнопка эти секунды
    /// выглядит неработающей.
    /// </remarks>
    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Say("Останавливаю: жду, пока закончится текущий рецепт…", "Muted", "⏹");

        _work?.Cancel();
    }

    /// <summary>
    /// Отвечает человеку так, чтобы ответ было видно.
    /// </summary>
    /// <remarks>
    /// Полосой, а не строкой под заголовком. Прежде окно отвечало подписью,
    /// и её не замечали: нажал «Проверить все», ничего видимого не случилось,
    /// и человек приходил с «проверка не начинается». Чаще всего она и не
    /// должна была начаться — имя открывается без десинка либо движки
    /// работают, — но сказано это было так, что не читалось.
    /// </remarks>
    private void Say(string text, string colour = "Muted", string mark = "•")
    {
        Status.Text = text;

        // Знак состояния вместо знака шрифта: «◐» — идёт проверка, остальное — по цвету сообщения.
        StatusMark.Tag = mark == "◐" ? "Starting" : colour switch
        {
            "Success" => "Running",
            "Warn" => "Degraded",
            "Danger" => "Failed",
            _ => "Off",
        };

        // Ошибка — на подложке DangerSoft, как ErrorState; остальное — нейтрально.
        StatusCard.SetResourceReference(Border.BackgroundProperty, colour == "Danger" ? "DangerSoft" : "Raised");
        StatusCard.SetResourceReference(Border.BorderBrushProperty, colour == "Danger" ? "DangerLine" : "Border");
        StatusCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Закрывает окно с ответом.
    /// </summary>
    /// <remarks>
    /// Через try, потому что <c>DialogResult</c> у окна, показанного не через
    /// <c>ShowDialog</c>, бросает исключение. Пока такое окно у нас одно
    /// и модальное, но нажатие «выбрать» не должно ронять программу, если
    /// однажды его откроют иначе: человек в этот момент делает ровно то,
    /// зачем окно и открыл.
    /// </remarks>
    private void Close(bool answer)
    {
        _work?.Cancel();

        try
        {
            DialogResult = answer;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    /// <summary>
    /// Перебирает рецепты и смотрит, с каким имя открывается.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движки обязаны быть остановлены, и это не перестраховка. Работающий
    /// winws2 уже держит свой набор фильтров, а второй экземпляр поверх него
    /// правил бы те же пакеты дважды — вышел бы замер не рецепта, а их суммы.
    /// Туннель тем более: через него имя откроется любым рецептом, включая
    /// пустой, и проверка ответит «работает всё».
    /// </para>
    /// <para>
    /// Останавливать их сами не беремся: это весь трафик машины, и человек
    /// должен решить это сам, а не обнаружить постфактум.
    /// </para>
    /// </remarks>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            Say("Сначала остановите движки на «Главной». Работающий десинк правил бы "
                + "те же пакеты вторым слоем, а туннель открыл бы имя любым рецептом — "
                + "проверять было бы нечего.", "Warn", "⏸");

            return;
        }

        var paths = ZapretPaths.Discover();
        var winws = paths is null ? null : Path.Combine(paths.Root, "exe", "winws2.exe");

        if (winws is null || !File.Exists(winws))
        {
            Say("winws2 не найден рядом с программой — проверять нечем.", "Danger", "✕");
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        TestButton.IsEnabled = false;
        TestButton.Content = "Проверяю…";
        StopButton.IsEnabled = true;

        int worked = 0;
        int tried = 0;

        try
        {
            // Адрес спрашивается один раз на всю проверку. Имя пробы может
            // не разрешаться вовсе, и тогда каждый повтор — это ещё один отказ
            // резолвера в цепочке из десятка рецептов.
            _address = await RecipeTrial.AddressForAsync(_domain, _work.Token);

            if (_address is null)
            {
                Say($"Не удалось узнать адрес ни у {_domain}, ни у его зоны — "
                    + "проверять некуда стучаться.", "Danger", "✕");

                return;
            }

            // Сперва без всякого рецепта: если имя открывается само, дальше
            // мерить нечего и выбирать не из чего.
            //
            // Тем же способом, что и весь перебор, а не полной пробой. Полная
            // ходит через DNS и пять стадий, и на имени пробы, которого в DNS
            // нет, отвечала про резолвер вместо фильтра. Разные мерки в одном
            // окне вдобавок расходятся в ответах — а сравнивать предстоит
            // именно их.
            if (await RecipeTrial.OpensAsync(_domain, _address, _work.Token) is { Passed: true } bare)
            {
                Say($"{_domain} открывается и без десинка — {RecipeTrial.Verdict(bare, TimeSpan.Zero)}. "
                    + "Рецепт ему не нужен, берите «решает пресет».", "Success", "✓");

                return;
            }

            worked = 0;

            foreach (var row in _rows)
            {
                Say($"Проверяю {++tried} из {_rows.Count}: {row.Title}…", "Warn", "◐");

                row.Verdict = "проверяю…";
                row.VerdictColour = (Brush)FindResource("Muted");
                row.Took = null;

                var clock = Stopwatch.StartNew();
                var flow = await TryAsync(winws, paths!.Root, row, _work.Token);
                clock.Stop();

                // Открывшим считается тот, кто довёл до страницы, а не тот,
                // кто прошёл фильтр. Разница вскрылась 21.09: все рецепты
                // получали 301 на apex Instagram и числились рабочими,
                // притом что сайт не грузился ни с одним.
                bool ok = flow.Page;

                // Время — только у дошедших до страницы. У остальных оно
                // означает срок ожидания либо длину цепочки перенаправлений,
                // а не скорость, и сравнивать его не с чем.
                row.Took = ok ? clock.Elapsed : null;

                // Вердикт называет, что пришло, а не только «да/нет».
                // Прежде здесь стояло «открывается за 0,8 с» и на коротком
                // перенаправлении на страницу входа — владелец прочёл это
                // как «сайт работает» и был вправе.
                row.Verdict = RecipeTrial.Verdict(flow, clock.Elapsed);

                // Три цвета, а не два. Перенаправление — это пройденный
                // фильтр, но не пришедшая страница, и валить его в одну
                // кучу с отказом так же неверно, как с успехом.
                row.VerdictColour = (Brush)FindResource(
                    flow.Page ? "Success" : ok ? "Warn" : "Danger");

                row.Edge = (Brush)FindResource(ok ? "AccentLine" : "Border");

                if (ok)
                    worked++;
            }

            if (worked == 0)
            {
                Say($"Ни один рецепт не открыл {_domain}. Дело может быть не в десинке — "
                    + "посмотрите «Проверку блокировок» целиком.", "Danger", "✕");
            }
            else
            {
                // Советуем быстрейший, а не «любой из отмеченных». Прежний
                // совет отправлял выбирать наугад из пяти одинаково зелёных
                // строк; время же — замер, и оно говорит, какой рецепт
                // проводит рукопожатие с меньшим сопротивлением.
                var best = RecipeRow.Best(_rows)!;

                var sovet = $"Открывают {worked} из {_rows.Count}. "
                    + $"Быстрее всех «{best.Title}» — {best.Took!.Value.TotalSeconds:0.0} с.";

                if (best.Current)
                    sovet += " Его пресет и так применяет: менять нечего.";

                Say(sovet, "Success", "✓");
            }
        }
        catch (OperationCanceledException)
        {
            Say($"Остановлено. Проверено {tried} из {_rows.Count}, ответы сохранены.",
                "Muted", "⏹");
        }
        catch (Exception ex)
        {
            Say("Проверка не удалась: " + ex.GetBaseException().Message, "Danger", "✕");
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "Проверить все";
            StopButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Поднимает winws2 с одним профилем и пробует открыть имя.
    /// </summary>
    /// <remarks>
    /// Сама сборка профиля, запуск и проба лежат в <see cref="RecipeTrial"/>:
    /// ими же меряет и автоподбор маршрутов.
    /// </remarks>
    private async Task<RecipeTrial.Flow> TryAsync(
        string winws,
        string zapretRoot,
        RecipeRow row,
        CancellationToken cancellationToken)
    {
        if (row.Steps.Count == 0)
            return RecipeTrial.Flow.Nothing("рецепт без шагов");

        var list = Path.GetFullPath(Path.Combine(
            WinwsCommandLine.OwnListsDirectory, "probe.txt"));

        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        await File.WriteAllTextAsync(list, _domain + Environment.NewLine, cancellationToken);

        var flow = RecipeTrial.Flow.Nothing("winws2 не запустился");

        var failed = await RecipeTrial.WithWinwsAsync(
            winws,
            zapretRoot,
            RecipeTrial.RecipeArguments(_preset, zapretRoot, [_domain], row.Steps, list),
            async () => flow = await RecipeTrial.OpensAsync(_domain, _address!, cancellationToken),
            cancellationToken);

        return failed is null ? flow : RecipeTrial.Flow.Nothing(failed);
    }
}
