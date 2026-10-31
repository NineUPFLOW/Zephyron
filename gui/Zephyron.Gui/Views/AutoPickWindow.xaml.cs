using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Zephyron.Core;
using Zephyron.Core.Rules;
using Zephyron.Proxy;
using Zephyron.Supervisor;
using Zephyron.Zapret;

namespace Zephyron.Gui.Views;

/// <summary>
/// Строка списка автоподбора: одно имя, живой вердикт и итог.
/// </summary>
/// <remarks>
/// Класс с уведомлениями, и строки меняются на месте — по той же причине, что у
/// <see cref="RecipeRow"/>: подмена <c>ItemsSource</c> посреди раскладки роняет WPF.
/// Всё показываемое считается из состояния при каждом <see cref="Refresh"/>,
/// а не хранится: проб приходит много, и держать производные поля в лад
/// с ними вручную значило бы однажды разойтись.
/// </remarks>
public sealed class AutoPickItem : INotifyPropertyChanged
{
    public enum Kind { Waiting, Changed, Failed, Skipped, Same }

    private readonly Func<string, Brush> _brush;
    private bool _apply;
    private bool _restored;
    private string? _blocked;

    public AutoPickItem(AutoPickTarget target, Func<string, Brush> brush)
    {
        Label = target.Label;
        Host = AutoPickRow.Normalize(target.Host);
        _brush = brush;
    }

    public string Label { get; }

    public string Host { get; }

    /// <summary>Подбор дошёл до этого имени; <c>null</c> — ждёт (или строка из журнала).</summary>
    public AutoPickRow? Row { get; set; }

    /// <summary>Прогон закончен, решения расставлены.</summary>
    public bool Finished { get; set; }

    /// <summary>Журнал: прогон и запись, по которым возвращается.</summary>
    public AutoPickJournalRun? Run { get; set; }

    public AutoPickJournalEntry? Entry { get; set; }

    /// <summary>Решение было, но галочку сняли — не записано.</summary>
    public bool Declined { get; set; }

    /// <summary>Проверка после записи: <c>true</c> открывается, <c>false</c> нет, <c>null</c> не делалась.</summary>
    public bool? Post { get; set; }

    /// <summary>Вызывается при смене галочки — окно пересчитывает кнопки «Записать».</summary>
    public Action? ApplyChanged { get; set; }

    public bool Apply
    {
        get => _apply;
        set
        {
            if (_apply == value)
                return;

            _apply = value;
            Raise(nameof(Apply));
            ApplyChanged?.Invoke();
        }
    }

    public bool Restored => _restored || Entry?.Restored == true;

    public string? Blocked => _blocked;

    public void MarkRestored(string? skipped)
    {
        if (skipped is null)
            _restored = true;
        else
            _blocked = skipped;
    }

    /// <summary>Есть решение, которое ещё можно записать.</summary>
    public bool Selectable => Finished && Row is { Skip: null, Choice: not null, Written: null } && !Declined;

    public Kind Category
    {
        get
        {
            if (Entry is not null && Row is null)
                return Kind.Changed;

            if (Row is null)
                return Kind.Waiting;

            if (Row.Skip is not null)
                return Kind.Skipped;

            if (!Finished)
                return Kind.Waiting;

            if (Row.Choice is not null)
                return Kind.Changed;

            // Служебное имя, а не страница: чинить нечего, это не провал подбора.
            if (AutoPick.NotASite(Row) is not null)
                return Kind.Skipped;

            var tries = Row.Tries;

            // Нынешний путь сломан, а устойчивой замены нет — это провал, а не «как есть»
            // (26.10 netflix.com: молчащий пресет числился рабочим).
            return tries.Count > 0 && AutoPick.CurrentWorks(Row) == false ? Kind.Failed : Kind.Same;
        }
    }

    private bool Written => Entry is not null && Row is null || Row?.Written == true;

    public string CurrentText => Row is null
        ? (Entry is null ? "…" : "")
        : Row.CurrentMode switch
        {
            RoutingMode.Direct => "напрямую",
            RoutingMode.Proxy => "VPN",
            _ => string.IsNullOrEmpty(Row.CurrentRecipe) || Row.CurrentRecipe == DesyncRecipes.FromPreset
                ? "десинк: пресет"
                : "десинк: " + (Row.RuleRecipe ?? Row.CurrentRecipe),
        };

    public string CurrentName => "Сейчас: " + CurrentText;

    public string Verdict
    {
        get
        {
            if (Row is null)
                return Entry is null ? "ждёт" : "";

            if (Row.Skip is not null)
                return "—";

            var tries = Row.Tries;

            if (tries.Count == 0)
                return Finished ? "—" : "ждёт";

            return tries.Where(t => t.Works).OrderBy(t => t.Way).FirstOrDefault() is { } ok
                ? $"открывается {ok.Took.TotalSeconds:0.0} с"
                : "не открывается";
        }
    }

    public Brush VerdictColour => _brush(Verdict switch
    {
        "не открывается" => "Danger",
        var v when v.StartsWith("открывается", StringComparison.Ordinal) => "Success",
        _ => "Muted",
    });

    public string VerdictTip => Row is null
        ? ""
        : string.Join("\n", Row.Tries.Select(t => $"{WayName(t)}: {t.Detail}"));

    private static string WayName(AutoPickTry t) => t.Way switch
    {
        AutoPickWay.Direct => "Напрямую",
        AutoPickWay.Preset => "Пресет",
        AutoPickWay.Recipe => "Рецепт «" + t.Title + "»",
        AutoPickWay.Pin => "Пин " + t.Address + " (" + t.Title + ")",
        _ => "VPN (" + t.Server + ")",
    };

    private string ChoiceText(AutoPickChoice choice) => (choice.Optional ? "можно без VPN: " : "") + choice.Way switch
    {
        AutoPickWay.Direct => "напрямую",
        AutoPickWay.Preset => "десинк: пресет",
        AutoPickWay.Recipe => "десинк: " + choice.Title,
        AutoPickWay.Pin => "пин в hosts: " + choice.Title,
        _ => "через VPN",
    };

    /// <summary>Итог: цвет и текст идут парой, смысл не только в цвете.</summary>
    private (string Text, string Colour) Outcome()
    {
        if (Entry is not null && Row is null)
        {
            return Restored ? ("возвращено", "Muted")
                : _blocked is not null ? (_blocked, "Warn")
                : ("записано: " + Entry.Why, "Success");
        }

        if (Row is null)
            return ("", "Muted");

        if (Row.Skip is not null)
            return ("пропущено: " + Row.Skip, "Muted");

        if (Row.Written == true)
        {
            return Restored ? ("возвращено", "Muted")
                : _blocked is not null ? (_blocked, "Warn")
                : ("записано: " + ChoiceText(Row.Choice!), "Success");
        }

        if (Row.Written == false)
            return ("не записано: " + Row.Note, "Warn");

        if (Declined)
            return ("не записано: галочку сняли", "Muted");

        if (Finished && AutoPick.NotASite(Row) is { } notSite)
            return ("не сайт: " + notSite, "Muted");

        return Category switch
        {
            Kind.Changed => (ChoiceText(Row.Choice!), "Success"),
            Kind.Failed => (Row.Tries.Any(t => t.Works)
                ? "ничего устойчивого — оставлено как есть"
                : "ничего не помогло", "Danger"),
            Kind.Same => (Row.Tries.Count > 0 && Row.Tries.Max(t => t.Level) == 1
                ? "оставить как есть: везде отказ по стране" : "оставить как есть", "Muted"),
            _ => ("", "Muted"),
        };
    }

    public string Result => Outcome().Text;

    public Brush ResultColour => _brush(Outcome().Colour);

    public string ResultTip => Blocked is not null
        ? "Вы поменяли этот маршрут уже после записи, поэтому автоподбор его не трогает."
        : Row?.Choice?.Why ?? Entry?.Why ?? "";

    public Brush Edge => _brush(Category switch
    {
        Kind.Changed when !Declined && !Restored => "AccentLine",
        Kind.Failed => "DangerLine",
        _ => "Border",
    });

    public double RowOpacity => Category == Kind.Skipped ? 0.7 : 1;

    public Visibility ApplyVisibility => Selectable ? Visibility.Visible : Visibility.Hidden;

    public string AutoName => "Записать изменение: " + Label;

    public Visibility RestoreVisibility => Written && !Restored && _blocked is null && Entry is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string RestoreName => "Вернуть: " + Label;

    public Visibility MarkVisibility => Post is null ? Visibility.Collapsed : Visibility.Visible;

    public string MarkTag => Post == true ? "Running" : "Failed";

    public string MarkName => Post == true ? "Проверка после: открывается" : "Проверка после: не открывается";

    public string RowName => $"{Label}, сейчас {CurrentText}, итог {Result}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Перечитать всё показываемое (пустое имя = все свойства).</summary>
    public void Refresh() => Raise(string.Empty);
}

/// <summary>
/// «Автоподбор маршрутов»: проверяет каждое имя и записывает, как его открывать.
/// </summary>
/// <remarks>
/// <para>
/// Меряет голую сеть, поэтому движки на время останавливаются; это спрашивается
/// заранее текстом кнопки и полосой ответа, а не отдельным окном подтверждения.
/// Подписка читается до остановки: ей может понадобиться сам туннель.
/// </para>
/// <para>
/// Записывает только отмеченные строки: <see cref="AutoPick.Apply"/> пишет все
/// строки с решением, поэтому ему отдаётся отчёт из одних отмеченных.
/// </para>
/// </remarks>
public partial class AutoPickWindow : Window
{
    private enum Phase { Idle, Running, Done, Writing, Written, Checking }

    private static readonly string[] StageNames = ["Напрямую", "Пресет", "Рецепты", "Пин", "VPN"];

    private readonly ObservableCollection<AutoPickItem> _items = [];
    private readonly Dictionary<string, AutoPickItem> _byHost = new(StringComparer.Ordinal);
    private readonly ICollectionView _view;
    private readonly Control[] _stageMarks = new Control[StageNames.Length];
    private readonly TextBlock[] _stageText = new TextBlock[StageNames.Length];
    private readonly string?[] _stageSkip = new string?[StageNames.Length];
    private readonly List<string> _warnings = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _clock = new();

    private Phase _phase = Phase.Idle;
    private CancellationTokenSource? _work;
    private AutoPickReport? _report;
    private AutoPickJournalRun? _run;
    private AutoPickJournalRun? _last;
    private Progress<AutoPickRow>? _progress;
    private bool _engineCame;
    private bool _stoppedEngines;
    private bool _checked;
    private bool _leaving;
    private int _stage;
    private int _ticks;
    private string _filter = "all";

    public AutoPickWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is AutoPickItem item && Matches(item);
        List.ItemsSource = _view;

        BuildStages();

        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            LoadLast();
            ApplyPhase();
            StartButton.Focus();
        };

        SizeChanged += OnSizeChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _work?.Cancel();
        };
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private static bool EnginesAlive() =>
        EngineControl.IsRunning || SupervisorState.Load(SupervisorState.DefaultPath)?.IsSupervisorAlive() == true;

    /// <summary>Что с движками на самом деле — по живой проверке, а не по тому, что окно успело сделать.</summary>
    private string EnginesNow() => EnginesAlive() ? "Движки работают."
        : _stoppedEngines ? "Движки остановлены — запустить их можно кнопкой «Запустить движки»."
        : "Движки не запущены.";

    // ---------------------------------------------------------------- полоса ответа

    /// <summary>Отвечает человеку; та же полоса, что в RecipeWindow.</summary>
    private void Say(string text, string colour = "Muted", bool busy = false)
    {
        Status.Text = text;

        StatusMark.Tag = busy ? "Starting" : colour switch
        {
            "Success" => "Running",
            "Warn" => "Degraded",
            "Danger" => "Failed",
            _ => "Off",
        };

        StatusCard.SetResourceReference(Border.BackgroundProperty, colour == "Danger" ? "DangerSoft" : "Raised");
        StatusCard.SetResourceReference(Border.BorderBrushProperty, colour == "Danger" ? "DangerLine" : "Border");
        StatusCard.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- этапы

    private void BuildStages()
    {
        for (int i = 0; i < StageNames.Length; i++)
        {
            if (i > 0)
            {
                Stages.Children.Add(new TextBlock
                {
                    Text = "→",
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Res("Muted"),
                });

            }

            var mark = new Control
            {
                Style = (Style)FindResource("StateMark"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var text = new TextBlock
            {
                Style = (Style)FindResource("Caption"),
                Text = StageNames[i],
                VerticalAlignment = VerticalAlignment.Center,
            };

            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            cell.Children.Add(mark);
            cell.Children.Add(text);
            Stages.Children.Add(cell);

            _stageMarks[i] = mark;
            _stageText[i] = text;
        }
    }

    private int WayCount(AutoPickWay way) =>
        _items.Count(i => i.Row is not null && i.Row.Tries.Any(t => t.Way == way));

    private int LiveCount() => _items.Count(i => i.Row?.Skip is null);

    /// <summary>Перерисовывает этапы и полосу; текущий этап — самый поздний, с которого пришла проба.</summary>
    private void UpdateStages()
    {
        int live = Math.Max(1, LiveCount());
        int active = _stageSkip.Count(s => s is null);
        int value = 0;
        int stage = 0;

        for (int i = 0; i < StageNames.Length; i++)
        {
            int count = WayCount((AutoPickWay)i);

            if (count > 0 && _stageSkip[i] is null)
                stage = i;

            value += Math.Min(count, live);

            _stageText[i].Text = _stageSkip[i] is not null ? $"{StageNames[i]}: пропущен"
                : i == 0 ? $"{StageNames[i]} {count}/{live}"
                : count > 0 ? $"{StageNames[i]} {count}"
                : StageNames[i];

            _stageText[i].ToolTip = _stageSkip[i];
        }

        bool done = _phase is not (Phase.Idle or Phase.Running);

        for (int i = 0; i < StageNames.Length; i++)
        {
            bool skipped = _stageSkip[i] is not null;
            bool current = !done && !skipped && i == stage;
            bool past = !skipped && (done || i < stage);

            _stageMarks[i].Tag = current ? "Starting" : past ? "Running" : "Off";
            _stageText[i].FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            _stageText[i].SetResourceReference(TextBlock.ForegroundProperty, current ? "Text" : "Muted");
        }

        Bar.Maximum = live * Math.Max(1, active);
        Bar.Value = done ? Bar.Maximum : Math.Min(value, Bar.Maximum);

        if (stage != _stage)
        {
            _stage = stage;
            SayProgress();
        }
    }

    private void SayProgress()
    {
        if (_phase != Phase.Running)
            return;

        int live = Math.Max(1, LiveCount());
        string count = _stage == 0 ? $"Готово {WayCount(AutoPickWay.Direct)} из {live}" : $"Проб: {WayCount((AutoPickWay)_stage)}";
        string text = $"Проверяю: {StageNames[_stage].ToLowerInvariant()}. {count}, прошло {_clock.Elapsed:mm\\:ss}.";

        if (_warnings.Count > 0)
            text += " " + string.Join(" ", _warnings);

        Say(text, "Muted", busy: true);
    }

    // ---------------------------------------------------------------- таймер: сторож и счётчик

    /// <summary>
    /// Каждые три секунды проверяет, что движки не вернулись, — их мог поднять трей.
    /// Счёт в полосе обновляется реже (раз в пять секунд): диктор читает её смену.
    /// </summary>
    private void OnTick(object? sender, EventArgs e)
    {
        _ticks++;

        if (_phase == Phase.Running)
        {
            if (_ticks % 3 == 0 && !_engineCame && EnginesAlive())
            {
                _engineCame = true;
                _work?.Cancel();
                return;
            }

            if (_ticks % 5 == 0)
                SayProgress();
        }
    }

    // ---------------------------------------------------------------- последний запуск

    private void LoadLast()
    {
        try
        {
            _last = AutoPickJournal.Load().LastOrDefault(r => r.Entries.Count > 0);
        }
        catch (Exception ex)
        {
            _last = null;
            Say("Журнал автоподбора не читается: " + ex.GetBaseException().Message, "Warn");
        }

        ShowLast();
    }

    private void ShowLast()
    {
        if (_last is null)
        {
            LastRunText.Text = "Запусков ещё не было.";
            LastRunButtons.Visibility = Visibility.Collapsed;
            return;
        }

        int left = _last.Entries.Count(e => !e.Restored);

        LastRunText.Text = $"Последний запуск: {_last.At.LocalDateTime:dd.MM.yyyy HH:mm}. "
            + $"Записано {_last.Entries.Count} изменений"
            + (left < _last.Entries.Count ? $", ещё не возвращено {left}." : ".");

        LastRunButtons.Visibility = Visibility.Visible;
        LastUndoButton.IsEnabled = left > 0;
    }

    private void OnShowLast(object sender, RoutedEventArgs e)
    {
        if (_last is null)
            return;

        _run = _last;
        _items.Clear();
        _byHost.Clear();

        foreach (var entry in _last.Entries)
        {
            var item = NewItem(new AutoPickTarget(entry.Host, entry.Host));
            item.Entry = entry;
            item.Run = _last;
            item.Finished = true;
        }

        ShowList();
        _checked = true;
        SetPhase(Phase.Written);
        Say($"Результаты запуска от {_last.At.LocalDateTime:dd.MM.yyyy HH:mm}. Откат — кнопкой «Вернуть» у строки или «Вернуть всё».", "Muted");
    }

    private void OnUndoLast(object sender, RoutedEventArgs e)
    {
        if (_last is null || !ConfirmUndo(_last.Entries.Count(x => !x.Restored)))
            return;

        try
        {
            AutoPickJournal.RestoreAll(_last);
            _last = AutoPickJournal.Load().FirstOrDefault(r => r.At == _last.At) ?? _last;
            ShowLast();
            Say("Возвращено. Изменённые после записи маршруты не тронуты.", "Success");
        }
        catch (Exception ex)
        {
            Say("Не удалось вернуть: " + ex.GetBaseException().Message, "Danger");
        }
    }

    private bool ConfirmUndo(int count) =>
        MessageBox.Show(this,
            $"Вернуть {count} маршрутов, как они были до автоподбора? Изменённые после этого не тронем.",
            "Zephyron", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    // ---------------------------------------------------------------- список и отбор

    private AutoPickItem NewItem(AutoPickTarget target)
    {
        var item = new AutoPickItem(target, Res) { ApplyChanged = UpdateButtons };

        if (_byHost.TryAdd(item.Host, item))
            _items.Add(item);

        return _byHost[item.Host];
    }

    private void ShowList()
    {
        LastRunCard.Visibility = Visibility.Collapsed;
        ListCard.Visibility = Visibility.Visible;
        StagesRow.Visibility = Visibility.Visible;
        RefreshAll();
    }

    private bool Matches(AutoPickItem item) => _filter switch
    {
        "changed" => item.Category == AutoPickItem.Kind.Changed,
        "failed" => item.Category == AutoPickItem.Kind.Failed,
        "skipped" => item.Category == AutoPickItem.Kind.Skipped,
        _ => true,
    };

    /// <summary>Перечитывает строки, счётчики чипов и пустое состояние.</summary>
    private void RefreshAll()
    {
        foreach (var item in _items)
            item.Refresh();

        int Count(AutoPickItem.Kind k) => _items.Count(i => i.Category == k);

        FilterAll.Content = $"Все {_items.Count}";
        FilterChanged.Content = $"Будет изменено {Count(AutoPickItem.Kind.Changed)}";
        FilterFailed.Content = $"Не помогло {Count(AutoPickItem.Kind.Failed)}";
        FilterSkipped.Content = $"Пропущено {Count(AutoPickItem.Kind.Skipped)}";

        _view.Refresh();
        EmptyFilter.Visibility = _items.Count > 0 && _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        _filter = (string)((RadioButton)sender).Tag;
        RefreshAll();
    }

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        FilterAll.IsChecked = true;
        _filter = "all";
        RefreshAll();
    }

    /// <summary>Чипы в узком окне уходят под этапы.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = ActualWidth < 820;

        Grid.SetRow(Chips, narrow ? 1 : 0);
        Grid.SetColumn(Chips, narrow ? 0 : 1);
        Grid.SetColumnSpan(Chips, narrow ? 2 : 1);
        Chips.Margin = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    // ---------------------------------------------------------------- кнопки по фазам

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        ApplyPhase();
    }

    private int Ticked => _items.Count(i => i.Apply && i.Selectable);

    private void ApplyPhase()
    {
        bool busy = _phase is Phase.Running or Phase.Checking;

        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        bool engines = EnginesAlive();

        // После записи можно начать заново, не переоткрывая окно.
        StartButton.Visibility = V(_phase is Phase.Idle or Phase.Written);
        StartButton.Content = engines ? "Остановить движки и начать" : "Начать";
        StopButton.Visibility = V(busy);
        WriteButton.Visibility = V(_phase == Phase.Done);
        EnginesButton.Visibility = V((_phase is Phase.Done or Phase.Written || _phase == Phase.Idle && _stoppedEngines) && !engines);
        CheckButton.Visibility = V(_phase == Phase.Written && !_checked && engines && _items.Any(i => i.Row?.Written == true));
        UndoButton.Visibility = V(_phase == Phase.Written && _items.Any(i => i.RestoreVisibility == Visibility.Visible));

        // Esc: во время подбора — «Стоп», иначе — «Закрыть».
        StopButton.IsCancel = busy;
        CloseButton.IsCancel = !busy;
        CloseButton.Visibility = V(!busy);
        CloseButton.IsEnabled = _phase != Phase.Writing;

        // «Закрыть» после записи — главная кнопка.
        CloseButton.Style = (Style)FindResource(_phase == Phase.Written ? "Primary" : "Ghost");

        StartButton.IsEnabled = _phase is Phase.Idle or Phase.Written;
        StopButton.IsEnabled = busy;

        // Свой сайт — когда можно начать новый подбор; что важно знать — до первого запуска.
        SiteRow.Visibility = V(_phase is Phase.Idle or Phase.Written);
        SiteButton.IsEnabled = _phase is Phase.Idle or Phase.Written;
        NotesCard.Visibility = V(_phase == Phase.Idle);

        Hint.Text = _phase switch
        {
            Phase.Idle => "Меняет только маршруты. Пресет и подписка не трогаются. "
                + "Пробы сверяют сертификат сайта: если неверны системные часы или нет корневых сертификатов, "
                + "«не открывается» будет почти везде — сначала поправьте их.",
            Phase.Running => "Остановить подбор можно в любой момент: найденное сохранится.",
            Phase.Done => "Снимите галочки у строк, которые менять не нужно.",
            Phase.Writing => "Идёт запись — окно закрыть нельзя.",
            Phase.Checking => "Проверяю записанные имена уже с работающими движками.",
            _ => "",
        };

        UpdateButtons();
        UpdateStages();
    }

    /// <summary>Включает «Записать» только при отмеченных строках.</summary>
    private void UpdateButtons()
    {
        if (WriteButton is null)
            return;

        bool any = _phase == Phase.Done && Ticked > 0;
        string? tip = any ? null : "Нечего записывать: нет ни одного изменения";

        WriteButton.IsEnabled = any;
        WriteButton.ToolTip = tip ?? "Записать отмеченное и перезапустить движки с новыми маршрутами, затем проверить записанное";

        // Одна кнопка по умолчанию на фазу: скрытая тоже ответила бы на Enter.
        StartButton.IsDefault = _phase == Phase.Idle;
        WriteButton.IsDefault = any;
        CloseButton.IsDefault = _phase == Phase.Written || _phase == Phase.Done && !any;
    }

    // ---------------------------------------------------------------- подбор

    /// <summary>Имя, для которого подбор идёт вместо всего списка; ставит «Подобрать для сайта».</summary>
    private string? _onlyHost;

    private void OnSiteKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            OnPickSite(sender, e);
        }
    }

    /// <summary>
    /// Подбор для одного сайта — по ссылке или домену, как в «Сайт не открывается».
    /// </summary>
    /// <remarks>
    /// Владелец 26.10: «добавь возможность автоподбора своего домена». Тот же
    /// подбор, что для всего списка, только на одном имени: те же этапы, запись,
    /// журнал и откат.
    /// </remarks>
    private void OnPickSite(object sender, RoutedEventArgs e)
    {
        if (_phase is not (Phase.Idle or Phase.Written))
            return;

        if (SiteFixer.HostOf(SiteBox.Text) is not { } host)
        {
            Say("Не понял адрес. Вставьте ссылку или домен, например https://example.com/page или example.com.", "Warn");
            SiteBox.Focus();
            return;
        }

        _onlyHost = host;
        OnStart(sender, e);
    }

    /// <summary>Списки и имена папки «Маршрутов», для которых идёт подбор; <c>null</c> — весь список.</summary>
    private IReadOnlySet<string>? _onlyLists;

    /// <summary>
    /// Подбор для одной папки «Маршрутов» сразу при открытии: все её части — списки
    /// сервиса (Discord: голос, медиа, обновления…) и свои имена. Проверять их по одной
    /// руками долго (просьба пользователя 28.10).
    /// </summary>
    public void PickListsOnOpen(IReadOnlySet<string> lists) =>
        Loaded += (_, _) =>
        {
            _onlyLists = lists;
            OnStart(StartButton, new RoutedEventArgs());
        };

    /// <summary>Подбор одного имени сразу при открытии — «Починить» из проверки блокировок.</summary>
    public void PickOnOpen(string host) =>
        Loaded += (_, _) =>
        {
            SiteBox.Text = host;
            OnPickSite(SiteButton, new RoutedEventArgs());
        };

    /// <summary>
    /// Чей это сайт: часть известного сервиса — маршрут пишется ей, иначе своё имя.
    /// </summary>
    /// <remarks>
    /// Иначе своё правило *.имя встало бы рядом с маршрутом сервиса и перекрыло его —
    /// тот самый дубль, что был у Netflix (26.10).
    /// </remarks>
    private static AutoPickTarget SiteTarget(string host, string? zapretRoot)
    {
        var name = AutoPickRow.Normalize(host);

        foreach (var service in Zephyron.Core.Services.ServiceCatalog.All)
        {
            foreach (var part in service.Parts.Where(p => !p.ByAddress && !p.Bulk))
            {
                var zones = HostListReader.Read(part.List, zapretRoot, out _);

                if (zones.Any(z => name == z.TrimStart('*', '.') || name.EndsWith("." + z.TrimStart('*', '.'), StringComparison.Ordinal)))
                    return new AutoPickTarget(name, $"{service.Name} · {part.Name}", part.List, part.Within, part.DefaultOff);
            }
        }

        return new AutoPickTarget(name, OwnLists.NameLabel);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_phase is not (Phase.Idle or Phase.Written))
            return;

        // Подбор одного сайта ставит имя прямо перед вызовом; обычный «Начать» — нет.
        var only = sender == SiteButton || sender == SiteBox ? _onlyHost : null;
        _onlyHost = null;
        var onlyLists = _onlyLists;
        _onlyLists = null;

        _work = new CancellationTokenSource();
        var token = _work.Token;

        _engineCame = false;
        _checked = false;
        _stage = 0;
        _report = null;
        _warnings.Clear();
        Array.Fill(_stageSkip, null);

        LoadError.Visibility = Visibility.Collapsed;
        _items.Clear();
        _byHost.Clear();
        ShowList();
        SetPhase(Phase.Running);

        IReadOnlyList<AutoPickTarget> targets = [];
        IReadOnlyList<Zephyron.Subscriptions.ProxyServer> servers = [];

        try
        {
            Say("Читаю список имён и подписку…", "Muted", busy: true);

            var root = ZapretPaths.Discover()?.Root;

            try
            {
                targets = await Task.Run(() =>
                {
                    if (only is not null)
                        return (List<AutoPickTarget>)[SiteTarget(only, root)];

                    var routes = RoutesOf();

                    // Папка «Маршрутов»: свои имена — как отдельные сайты, части сервиса — их списки.
                    var folderOwn = onlyLists?.Where(v => v.Contains('.') && !v.Contains(Path.DirectorySeparatorChar)
                            && !v.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                            && !System.Net.IPAddress.TryParse(v.Split('/')[0], out _))
                        .Select(v => SiteTarget(v, root)).ToList() ?? [];

                    return CheckView.Targets(root, null, false)
                        .Where(t => onlyLists is null || routes.Map.TryGetValue(t.Service, out var mine) && onlyLists.Contains(mine.List))
                        .Where(t => !routes.Skipped.Contains(t.Service))
                        .Select(t => routes.Map.TryGetValue(t.Service, out var part)
                            ? new AutoPickTarget(t.Host, t.Service, part.List, part.Within, part.DefaultOff)
                            : new AutoPickTarget(t.Host, t.Service))
                        .Concat(folderOwn)
                        // Как в AutoPick.RunAsync: www.x и x — одно правило *.x, строка одна.
                        .DistinctBy(t => SiteFixer.RuleValue(AutoPickRow.Normalize(t.Host)))
                        .ToList();
                }, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoadError.Visibility = Visibility.Visible;
                ListCard.Visibility = Visibility.Collapsed;
                SetPhase(Phase.Idle);
                Say("Не удалось прочитать список имён: " + ex.GetBaseException().Message, "Danger");
                return;
            }

            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // До остановки движков: подписка может читаться через сам туннель.
            if (settings.NeedsProxy)
            {
                try
                {
                    servers = await CheckView.VpnServersAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _warnings.Add("Подписка не прочиталась (" + ex.GetBaseException().Message + ") — этап «VPN» пропущен.");
                }
            }

            _stageSkip[4] = !settings.NeedsProxy ? "VPN выключен в настройках"
                : servers.Count == 0 ? "нет выбранного живого сервера"
                : null;

            if (_stageSkip[4] is not null && servers.Count == 0 && settings.NeedsProxy && _warnings.Count == 0)
                _warnings.Add("Нет подписки либо выбранный сервер не отвечает — этап «VPN» пропущен.");

            var paths = ZapretPaths.Discover();
            var winws = paths is null ? null : Path.Combine(paths.Root, "exe", "winws2.exe");

            if (winws is null || !File.Exists(winws))
            {
                _stageSkip[1] = _stageSkip[2] = "нет winws2";
                _warnings.Insert(0, "Не найден winws2 — десинк проверить нечем. Этапы «Пресет» и «Рецепты» пропущены.");
            }
            else if (settings.PresetName is not { } presetName || ZapretPaths.FindPreset(presetName) is null)
            {
                _stageSkip[1] = _stageSkip[2] = "нет пресета";
                _warnings.Insert(0, "Нет пресета — этапы «Пресет» и «Рецепты» пропущены.");
            }

            foreach (var target in targets)
                NewItem(target);

            RefreshAll();

            if (EnginesAlive())
            {
                Say("Останавливаю движки…", "Muted", busy: true);

                // Отметка до ожидания: «Стоп» или сбой посреди остановки оставили бы
                // движки лежать, а окно — не знающим, что их надо поднять.
                _stoppedEngines = true;
                await EngineControl.StopAsync("автоподбор", token);
            }

            _clock.Restart();
            _ticks = 0;
            _timer.Start();
            UpdateStages();
            SayProgress();

            // Progress создаётся здесь, в потоке окна: обратные вызовы возвращаются в него.
            _progress = new Progress<AutoPickRow>(OnRow);

            var report = await AutoPick.RunAsync(targets, servers, _progress, token);

            Finish(report);
        }
        catch (OperationCanceledException)
        {
            Stopped();
        }
        catch (Exception ex)
        {
            SetPhase(Phase.Idle);
            Say("Не получилось: " + ex.GetBaseException().Message + ". Маршруты не менялись. " + EnginesNow(), "Danger");
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    /// <summary>Проба пришла: привязывает строку и обновляет показанное.</summary>
    private void OnRow(AutoPickRow row)
    {
        if (!_byHost.TryGetValue(row.Host, out var item))
            return;

        item.Row ??= row;
        item.Refresh();

        if (_phase == Phase.Running)
            UpdateStages();
    }

    private void Finish(AutoPickReport report)
    {
        _report = report;

        foreach (var row in report.Rows)
        {
            if (_byHost.TryGetValue(row.Host, out var item))
                item.Row = row;
        }

        foreach (var item in _items)
        {
            item.Finished = true;
            item.Apply = item.Selectable && item.Row?.Choice?.Optional != true;
        }

        SetPhase(Phase.Done);
        RefreshAll();

        int changed = _items.Count(i => i.Category == AutoPickItem.Kind.Changed);
        int failed = _items.Count(i => i.Category == AutoPickItem.Kind.Failed);
        int skipped = _items.Count(i => i.Category == AutoPickItem.Kind.Skipped);

        var text = $"Готово за {_clock.Elapsed:mm\\:ss}. Изменится {changed}, не помогло {failed}, пропущено {skipped}.";

        if (_warnings.Count > 0)
            text += " " + string.Join(" ", _warnings);

        if (report.Notes.Count > 0)
            text += " " + string.Join("; ", report.Notes) + ".";

        text += SaveLog(report);

        Say(text,_warnings.Count > 0 || report.Notes.Count > 0 ? "Warn" : "Success");
        FocusFirst(WriteButton);
    }

    /// <summary>
    /// Подбор прерван: из уже полученных проб решения считаются здесь же.
    /// </summary>
    /// <remarks>
    /// <see cref="AutoPick.RunAsync"/> при отмене отчёта не возвращает, а ответы
    /// жалко. Решение по неполным пробам может предложить рецепт там, где
    /// пресет ещё не проверялся, — поэтому в сообщении так и сказано.
    /// </remarks>
    private void Stopped()
    {
        foreach (var item in _items.Where(i => i.Row is not null))
            item.Row!.Choice = AutoPick.Choose(item.Row);

        if (!_items.Any(i => i.Row is not null))
        {
            SetPhase(Phase.Idle);
            Say((_engineCame ? "Движки запустили снаружи — подбор остановлен. " : "Остановлено до первых результатов. ")
                + EnginesNow(), "Warn");
            return;
        }

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        _report = new AutoPickReport(
            settings.PresetName ?? "",
            _items.Where(i => i.Row is not null).Select(i => i.Row!).ToList(),
            new Dictionary<string, TimeSpan>(),
            []);

        foreach (var item in _items)
        {
            item.Finished = item.Row is not null;

            // Прервали до перепроверки: решение по одному замеру — без галочки.
            item.Apply = item.Selectable && item.Row?.Choice?.Optional != true
                && AutoPick.NeedsRepeat(item.Row!).Count == 0;
        }

        SetPhase(Phase.Done);
        RefreshAll();

        Say((_engineCame
                ? "Движки запустили снаружи — подбор остановлен, ведь он меряет голую сеть. "
                : "Остановлено. ")
            + $"Результаты сохранены: изменится {_items.Count(i => i.Category == AutoPickItem.Kind.Changed)} имён. "
            + "Решения, не прошедшие перепроверку, оставлены без галочки."
            + SaveLog(_report),
            "Warn");
    }

    /// <summary>
    /// Подпись проверки → маршрут, которым имя живёт в «Маршрутах»: список части
    /// сервиса либо свой список.
    /// </summary>
    /// <remarks>
    /// Владелец 26.10: подбор писал своё правило *.netflix.com рядом с маршрутом
    /// «Netflix», и в «Маршрутах» вышло две строки на одно. Подписи — те же, что
    /// строит CheckView.Targets и OwnLists.CheckTargets; «свой домен» сюда не
    /// входит — его правило и есть маршрут.
    /// <para>
    /// Собирается на каждый прогон, а не раз за сеанс: свой список, созданный или
    /// переименованный после первого открытия окна, иначе уходил бы правилом
    /// *.имя рядом со списком, а удалённый — правилом на несуществующий файл.
    /// </para>
    /// <para>
    /// Списки программ (ProgramRoutes) не подбираются: в них выученные имена —
    /// телеметрия и игровые серверы, не сайты, и пробой страницы их не судить.
    /// </para>
    /// </remarks>
    private static (Dictionary<string, (string List, string? Within, bool DefaultOff)> Map, HashSet<string> Skipped) RoutesOf()
    {
        var map = new Dictionary<string, (string, string?, bool)>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var service in Zephyron.Core.Services.ServiceCatalog.All)
        {
            foreach (var part in service.Parts.Where(p => !p.ByAddress && !p.Bulk))
                map.TryAdd($"{service.Name} · {part.Name}", (part.List, part.Within, part.DefaultOff));
        }

        foreach (var entry in UserRulesFile.Load().Entries.Where(e => e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value)))
        {
            var label = OwnLists.ListLabelPrefix + OwnLists.NameOf(entry.Value);

            if (ProgramRoutes.IsProgramList(entry.Value))
                skipped.Add(label);
            else
                map.TryAdd(label, (entry.Value, null, false));
        }

        return (map, skipped);
    }

    /// <summary>Сохраняет журнал прогона; отвечает хвостом для строки состояния.</summary>
    /// <remarks>Не сохранился — подбор от этого не хуже: говорим и идём дальше.</remarks>
    private static string SaveLog(AutoPickReport report)
    {
        try
        {
            return " Журнал прогона: " + AutoPick.SaveLog(report);
        }
        catch (Exception ex)
        {
            return " Журнал прогона не сохранился: " + ex.GetBaseException().Message;
        }
    }

    private void FocusFirst(Button button) => Dispatcher.BeginInvoke(() => button.Focus(), DispatcherPriority.Input);

    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Say("Останавливаю: жду, пока закончится текущая проба…", "Muted", busy: true);
        _work?.Cancel();
    }

    // ---------------------------------------------------------------- запись

    private async void OnWrite(object sender, RoutedEventArgs e) =>
        await Guarded(WriteAsync, "Не записалось: ");

    /// <summary>
    /// Обработчик кнопки не роняет окно: исключение из async void ушло бы
    /// в диспетчер и закрыло программу, а из отброшенной задачи — потерялось бы.
    /// </summary>
    private async Task Guarded(Func<Task> work, string failed)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Say(failed + ex.GetBaseException().Message, "Danger");
        }
    }

    private async Task WriteAsync()
    {
        if (_phase != Phase.Done || _report is null)
            return;

        var ticked = _items.Where(i => i.Apply && i.Selectable).ToList();

        if (ticked.Count == 0)
            return;

        SetPhase(Phase.Writing);
        Say("Записываю…", "Muted", busy: true);

        // Apply пишет все строки с решением, поэтому отдаём ему одни отмеченные.
        var only = _report with { Rows = ticked.Select(i => i.Row!).ToList() };
        var started = DateTimeOffset.Now;

        try
        {
            await Task.Run(() => AutoPick.Apply(only));
        }
        catch (Exception ex)
        {
            // Apply отметок не оставляет (файл откачен), строки снова можно отметить.
            SetPhase(Phase.Done);
            RefreshAll();
            Say("Не записалось: " + ex.GetBaseException().Message + ". Отметки на месте — можно повторить.", "Danger");
            return;
        }

        foreach (var item in _items.Where(i => i.Row?.Choice is not null && i.Row.Written is null))
            item.Declined = true;

        bool journaled = AttachJournal(started);

        int written = _items.Count(i => i.Row?.Written == true);
        int refused = _items.Count(i => i.Row?.Written == false);

        SetPhase(Phase.Written);
        RefreshAll();

        if (written > 0 && !journaled)
        {
            Say($"Записано {written} изменений, но в журнале автоподбора эта запись не нашлась — "
                + "кнопкой «Вернуть» её не откатить. Файл маршрутов до записи: config/rules.user.before-autopick.yaml.",
                "Warn");
        }
        else
        {
            Say($"Записано {written} изменений"
                + (refused > 0 ? $", не записано {refused} (перекрыты другими правилами)" : "")
                + ". Откат — кнопкой «Вернуть» у строки или «Вернуть всё».",
                refused > 0 ? "Warn" : "Success");
        }

        if (written > 0)
            await ApplyEnginesAsync(check: true);
    }

    /// <summary>Находит в журнале только что записанный прогон и вяжет записи со строками.</summary>
    /// <remarks>
    /// По времени, а не «последний»: нечитаемый журнал или запись без прогона
    /// подсунули бы прежний прогон, и «Вернуть» откатывал бы чужие записи.
    /// </remarks>
    /// <returns><c>false</c> — прогона с этой записью в журнале нет, откат недоступен.</returns>
    private bool AttachJournal(DateTimeOffset since)
    {
        try
        {
            _run = AutoPickJournal.Load().LastOrDefault(r => r.At >= since);
        }
        catch (Exception)
        {
            _run = null;
        }

        if (_run is null)
            return false;

        foreach (var item in _items.Where(i => i.Row?.Written == true))
        {
            item.Run = _run;
            item.Entry = _run.Entries.FirstOrDefault(x => x.Host == item.Row!.Host && !x.Restored);
        }

        return true;
    }

    private async void OnEngines(object sender, RoutedEventArgs e) =>
        await Guarded(() => StartEnginesAsync(), "Движки не запустились: ");

    /// <summary>
    /// Применяет записанные маршруты: работающие движки перезапускает, погашенные
    /// этим окном — поднимает; затем, если просили, проверяет записанное.
    /// </summary>
    /// <remarks>
    /// Владелец 26.10: «если после применения нужно перезапускать движки — перезапускай».
    /// Маршруты читаются движками при запуске, и запись без перезапуска молча не
    /// действовала до следующего. Движки, которых не было и до подбора, не трогаем:
    /// человек их не запускал — не нам решать.
    /// </remarks>
    private async Task ApplyEnginesAsync(bool check)
    {
        bool ok;

        if (EnginesAlive())
            ok = await StartEnginesAsync(restart: true);
        else if (_stoppedEngines)
            ok = await StartEnginesAsync();
        else
        {
            Say(Status.Text + " Движки не запущены — маршруты применятся при их запуске.", "Muted");
            return;
        }

        if (ok && check)
            await CheckAsync();
    }

    /// <summary>Поднимает (или перезапускает) движки и ждёт, пока супервизор станет виден, плюс прогрев.</summary>
    private async Task<bool> StartEnginesAsync(bool restart = false)
    {
        var before = _phase;
        SetPhase(Phase.Writing);
        Say(restart ? "Перезапускаю движки с новыми маршрутами…" : "Запускаю движки…", "Muted", busy: true);

        try
        {
            var outcome = restart
                ? await EngineControl.RestartAsync("автоподбор", CancellationToken.None)
                : await EngineControl.StartAsync("автоподбор", CancellationToken.None);

            if (!outcome.Ok)
            {
                Say("Движки не запустились: " + outcome.Message, "Danger");
                return false;
            }

            for (int i = 0; i < 60; i++)
            {
                if (SupervisorState.Load(SupervisorState.DefaultPath)?.IsSupervisorAlive() == true)
                    break;

                await Task.Delay(1000);
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
            _stoppedEngines = false;
            Say(restart ? "Движки перезапущены — новые маршруты действуют." : "Движки запущены — новые маршруты действуют.",
                "Success");
            return true;
        }
        finally
        {
            SetPhase(before);
        }
    }

    // ---------------------------------------------------------------- проверка после

    private async void OnCheck(object sender, RoutedEventArgs e) => await CheckAsync();

    private async Task CheckAsync()
    {
        var rows = _items.Where(i => i.Row?.Written == true && !i.Restored).ToList();

        if (rows.Count == 0)
            return;

        _work = new CancellationTokenSource();
        var token = _work.Token;

        SetPhase(Phase.Checking);

        int done = 0;
        int open = 0;
        int shut = 0;

        try
        {
            await Parallel.ForEachAsync(
                rows,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (item, ct) =>
                {
                    var report = await BlockCheck.CheckAsync(
                        item.Host, null, ct, throughTunnel: item.Row!.Choice!.Mode == RoutingMode.Proxy);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.Post = report.Kind == BlockKind.None;
                        item.Refresh();

                        if (item.Post == true) open++; else shut++;

                        Say($"Проверяю после запуска движков: {++done} из {rows.Count}.", "Muted", busy: true);
                    });
                });

            _checked = true;
            Say($"Проверка после: ✓ {open}, ✕ {shut}.", shut > 0 ? "Warn" : "Success");
        }
        catch (OperationCanceledException)
        {
            Say($"Проверка остановлена: проверено {done} из {rows.Count}.", "Warn");
        }
        catch (Exception ex)
        {
            Say("Проверка не удалась: " + ex.GetBaseException().Message, "Danger");
        }
        finally
        {
            SetPhase(Phase.Written);
        }
    }

    // ---------------------------------------------------------------- откат

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string host } || !_byHost.TryGetValue(host, out var item)
            || item.Run is null || item.Entry is null)
        {
            return;
        }

        string? skipped;

        try
        {
            skipped = AutoPickJournal.Restore(item.Run, item.Entry);
            item.MarkRestored(skipped);
        }
        catch (Exception ex)
        {
            Say("Не удалось вернуть: " + ex.GetBaseException().Message, "Danger");
            return;
        }

        RefreshAll();
        ApplyPhase();

        // Возвращённый маршрут, как и записанный, действует только после перезапуска.
        if (skipped is null)
        {
            Say($"Возвращено: {host}.", "Success");
            await Guarded(() => ApplyEnginesAsync(check: false), "Движки не перезапустились: ");
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        var rows = _items.Where(i => i.RestoreVisibility == Visibility.Visible).ToList();

        if (_run is null || rows.Count == 0 || !ConfirmUndo(rows.Count))
            return;

        try
        {
            var results = AutoPickJournal.RestoreAll(_run);

            for (int i = 0; i < _run.Entries.Count; i++)
            {
                var entry = _run.Entries[i];

                // Уже возвращённые вернулись в прошлый раз — их ответ «уже возвращено» не отказ.
                if (rows.FirstOrDefault(x => ReferenceEquals(x.Entry, entry)) is { } item)
                    item.MarkRestored(results[i]);
            }
        }
        catch (Exception ex)
        {
            Say("Не удалось вернуть: " + ex.GetBaseException().Message, "Danger");
            return;
        }

        RefreshAll();
        ApplyPhase();

        Say("Возвращено. Изменённые после записи маршруты не тронуты.", "Success");
        await Guarded(() => ApplyEnginesAsync(check: false), "Движки не перезапустились: ");
    }

    // ---------------------------------------------------------------- закрытие

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        switch (_phase)
        {
            // Крестик во время подбора — то же, что «Стоп»: окно остаётся с результатами.
            case Phase.Running or Phase.Checking:
                e.Cancel = true;
                OnStop(this, new RoutedEventArgs());
                return;

            // Закрытие посреди записи разошлось бы с журналом.
            case Phase.Writing:
                e.Cancel = true;
                return;

        }

        if (!_leaving)
        {
            if (_phase == Phase.Done && Ticked > 0
                && MessageBox.Show(this,
                    "Закрыть без записи? Найденное пропадёт, маршруты останутся как были.",
                    "Zephyron", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // Движки погасило это окно — молча уйти, оставив человека без них, нельзя.
            if (_stoppedEngines && !EnginesAlive())
            {
                var answer = MessageBox.Show(this,
                    "Движки остановлены на время автоподбора. Запустить их перед закрытием?\n\n"
                    + "Да — запустить и закрыть. Нет — закрыть без них. Отмена — остаться.",
                    "Zephyron", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (answer != MessageBoxResult.No)
                {
                    e.Cancel = true;

                    if (answer == MessageBoxResult.Yes)
                    {
                        _leaving = true;
                        _ = StartAndCloseAsync();
                    }

                    return;
                }
            }
        }

        base.OnClosing(e);
    }

    /// <summary>«Да» при закрытии: поднять движки и уйти; не поднялись — окно остаётся с ошибкой.</summary>
    private async Task StartAndCloseAsync()
    {
        try
        {
            if (await StartEnginesAsync())
            {
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            Say("Движки не запустились: " + ex.GetBaseException().Message, "Danger");
        }

        _leaving = false;
    }
}
