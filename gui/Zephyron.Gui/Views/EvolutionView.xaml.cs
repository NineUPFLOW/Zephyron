using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zephyron.Core;
using Zephyron.Supervisor.Evolution;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Gui.Views;

public sealed record ProbeRowUi(string Host, string Status, Brush StatusColor, string Latency, string Detail);

public sealed record EvolutionLogEntry(string Time, string Message, Brush Color);

public sealed record FitnessHistoryPoint(
    int Generation,
    double Fitness,
    int Passed,
    int Total)
{
    public string GenText => $"G{Generation}";
    public string FitnessText => $"{Fitness:P0}";
    public int FitnessPercent { get; set; } = Math.Clamp((int)(Fitness * 100), 0, 100);
    public string TooltipText => $"Поколение {Generation}: фитнес {Fitness:P0}, открыто {Passed}/{Total} хостов";
}

public sealed record PresetDiffUiModel(
    string SectionName,
    string OldSteps,
    string NewSteps,
    string Explanation,
    string StatusBadge,
    Brush BadgeBrush,
    Brush BadgeBackground);

public sealed record HostProbeChip(string Host, bool Success, long LatencyMs)
{
    public string DisplayText => Success ? $"{Host}: {LatencyMs}мс ✔" : $"{Host}: ✖";
    public Brush BadgeColor => Success ? Brushes.LimeGreen : Brushes.OrangeRed;
}

public class TargetDomainGroup : INotifyPropertyChanged
{
    private bool _isChecked;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public IReadOnlyList<string> Hosts { get; set; } = [];

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class PresetBenchmarkItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _passedHosts;
    private int _totalHosts;
    private long _latencyMs;
    private double _fitnessScore;
    private int _rank;
    private bool _isEvaluating;
    private List<HostProbeChip> _hostChips = [];

    public ZapretPreset Preset { get; init; } = default!;
    public string Name => Preset.Name;

    public bool IsEvaluating
    {
        get => _isEvaluating;
        set
        {
            if (_isEvaluating != value)
            {
                _isEvaluating = value;
                OnPropertyChanged(nameof(IsEvaluating));
                OnPropertyChanged(nameof(ScoreBadge));
                OnPropertyChanged(nameof(ScoreBrush));
                OnPropertyChanged(nameof(ScoreBackground));
                OnPropertyChanged(nameof(RankText));
                OnPropertyChanged(nameof(LatencyBadge));
            }
        }
    }

    public int PassedHosts
    {
        get => _passedHosts;
        set
        {
            if (_passedHosts != value)
            {
                _passedHosts = value;
                OnPropertyChanged(nameof(PassedHosts));
                OnPropertyChanged(nameof(ScoreBadge));
                OnPropertyChanged(nameof(ScoreBrush));
                OnPropertyChanged(nameof(ScoreBackground));
            }
        }
    }

    public int TotalHosts
    {
        get => _totalHosts;
        set
        {
            if (_totalHosts != value)
            {
                _totalHosts = value;
                OnPropertyChanged(nameof(TotalHosts));
                OnPropertyChanged(nameof(ScoreBadge));
                OnPropertyChanged(nameof(ScoreBrush));
                OnPropertyChanged(nameof(ScoreBackground));
                OnPropertyChanged(nameof(RankText));
            }
        }
    }

    public long LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (_latencyMs != value)
            {
                _latencyMs = value;
                OnPropertyChanged(nameof(LatencyMs));
                OnPropertyChanged(nameof(LatencyBadge));
            }
        }
    }

    public double FitnessScore
    {
        get => _fitnessScore;
        set
        {
            if (Math.Abs(_fitnessScore - value) > 0.0001)
            {
                _fitnessScore = value;
                OnPropertyChanged(nameof(FitnessScore));
                OnPropertyChanged(nameof(RankText));
            }
        }
    }

    public int Rank
    {
        get => _rank;
        set
        {
            if (_rank != value)
            {
                _rank = value;
                OnPropertyChanged(nameof(Rank));
                OnPropertyChanged(nameof(RankText));
            }
        }
    }

    public List<HostProbeChip> HostChips
    {
        get => _hostChips;
        set
        {
            _hostChips = value;
            OnPropertyChanged(nameof(HostChips));
        }
    }

    public bool IsEvolved =>
        Name.StartsWith("Evolved ", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("(evolved)", StringComparison.OrdinalIgnoreCase)
        || (Preset?.BuiltinVersion?.StartsWith("evolved", StringComparison.OrdinalIgnoreCase) == true);

    public string EvolvedBadge
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(Name, @"\(Gen\s*(\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return $"🧬 Gen {match.Groups[1].Value}";
            return "🧬 Эволюция";
        }
    }

    public Visibility EvolvedBadgeVisibility => IsEvolved ? Visibility.Visible : Visibility.Collapsed;

    public string RankText => _isEvaluating
        ? "⏳ Выполняется замер..."
        : (_totalHosts == 0 ? "В очереди на проверку" : $"Ранг #{Rank} · фитнес {FitnessScore:P0}");

    public string ScoreBadge => _isEvaluating
        ? "⚡ Проверка..."
        : (_totalHosts == 0 ? "Ожидает" : $"{PassedHosts}/{TotalHosts} хостов");

    public string LatencyBadge => _isEvaluating
        ? "замер..."
        : (LatencyMs > 0 ? $"{LatencyMs} мс" : "—");

    public Brush ScoreBrush => _isEvaluating
        ? Brushes.DeepSkyBlue
        : (_totalHosts == 0
            ? Brushes.Gray
            : (PassedHosts == TotalHosts
                ? Brushes.LimeGreen
                : (PassedHosts > 0 ? Brushes.Orange : Brushes.OrangeRed)));

    public Brush ScoreBackground => _isEvaluating
        ? new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xB0, 0xFF))
        : (_totalHosts == 0
            ? new SolidColorBrush(Color.FromArgb(0x15, 0x88, 0x88, 0x88))
            : (PassedHosts == TotalHosts
                ? new SolidColorBrush(Color.FromArgb(0x22, 0x35, 0xE6, 0xA0))
                : (PassedHosts > 0 ? new SolidColorBrush(Color.FromArgb(0x22, 0xF5, 0xB1, 0x4A)) : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x6B, 0x6B)))));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                SelectedChanged?.Invoke();
            }
        }
    }

    public Action? SelectedChanged { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class EvolutionView : UserControl
{
    private readonly EvolutionSettings _settings;
    private readonly ObservableCollection<TargetDomainGroup> _domainGroups = [];
    private readonly ObservableCollection<PresetBenchmarkItem> _benchmarkItems = [];
    private readonly ObservableCollection<ProbeRowUi> _probeRows = [];
    private readonly ObservableCollection<EvolutionLogEntry> _logEntries = [];
    private readonly ObservableCollection<PresetDiffUiModel> _diffEntries = [];
    private readonly ObservableCollection<FitnessHistoryPoint> _fitnessHistory = [];

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _benchmarkCts;
    private string? _lastExportedFile;
    private PresetIndividual? _lastWinner;
    private ZapretPreset? _lastBasePreset;
    private string? _previousPresetName;
    private double _initialBaseFitness = 0.0;
    private int _lastLoggedGen = -1;

    public EvolutionView()
    {
        InitializeComponent();

        TargetDomainsList.ItemsSource = _domainGroups;
        BenchmarkResultsList.ItemsSource = _benchmarkItems;
        ProbeResultsList.ItemsSource = _probeRows;
        EvolutionLogList.ItemsSource = _logEntries;
        DiffEntriesList.ItemsSource = _diffEntries;
        FitnessHistoryList.ItemsSource = _fitnessHistory;

        _settings = EvolutionSettings.Load();
        InitializeDomainGroups();
        ApplySettingsToUi();
        ReloadPresetsFromDisk();

        Loaded += (_, _) => ReloadPresetsFromDisk();
    }

    private void InitializeDomainGroups()
    {
        _domainGroups.Clear();
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "YouTube",
            Description = "Web + Видео CDN",
            Hosts = ["www.youtube.com", "googlevideo.com"],
            IsChecked = true
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Discord",
            Description = "Клиент + Шлюз",
            Hosts = ["discord.com", "gateway.discord.gg"],
            IsChecked = true
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Telegram",
            Description = "API + Веб",
            Hosts = ["api.telegram.org", "web.telegram.org"],
            IsChecked = true
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Twitch",
            Description = "Стримы и чат",
            Hosts = ["api.twitch.tv", "twitch.tv"],
            IsChecked = true
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Steam",
            Description = "Сообщество + Магазин",
            Hosts = ["store.steampowered.com", "steamcommunity.com"],
            IsChecked = false
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "X / Twitter",
            Description = "Соцсеть",
            Hosts = ["x.com", "twitter.com"],
            IsChecked = false
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Instagram",
            Description = "Медиа",
            Hosts = ["instagram.com"],
            IsChecked = false
        });
        _domainGroups.Add(new TargetDomainGroup
        {
            Name = "Rutracker",
            Description = "Трекер",
            Hosts = ["rutracker.org"],
            IsChecked = false
        });
    }

    public void ReloadPresetsFromDisk(string? selectPresetName = null)
    {
        var files = ZapretPaths.PresetFiles;
        var reader = new PresetReader();
        var presets = reader.Read(files);

        // Сохраняем состояние текущих бенчмарков и выборки
        var existingStates = _benchmarkItems.ToDictionary(
            x => x.Name,
            x => (x.IsSelected, x.PassedHosts, x.TotalHosts, x.LatencyMs, x.FitnessScore, x.HostChips),
            StringComparer.OrdinalIgnoreCase);

        _benchmarkItems.Clear();

        // Упорядочиваем: сначала эволюционировавшие (они в приоритете для дальнейшей эволюции!),
        // затем по числу открытых хостов, фитнесу или алфавиту
        var sortedPresets = presets
            .OrderByDescending(p => p.Name.StartsWith("Evolved ", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => existingStates.TryGetValue(p.Name, out var s) ? s.PassedHosts : -1)
            .ThenByDescending(p => existingStates.TryGetValue(p.Name, out var s) ? s.FitnessScore : -1)
            .ToList();

        int rank = 1;
        foreach (var p in sortedPresets)
        {
            bool isSelected = false;
            int passed = 0;
            int total = 0;
            long latency = 0;
            double fitness = 0.0;
            List<HostProbeChip> chips = [];

            if (existingStates.TryGetValue(p.Name, out var st))
            {
                isSelected = st.IsSelected;
                passed = st.PassedHosts;
                total = st.TotalHosts;
                latency = st.LatencyMs;
                fitness = st.FitnessScore;
                chips = st.HostChips;
            }
            else
            {
                // Если пресет только что эволюционировал или входит в первые позиции
                isSelected = rank <= 3;
            }

            if (!string.IsNullOrEmpty(selectPresetName) &&
                (string.Equals(p.Name, selectPresetName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileNameWithoutExtension(p.FilePath), selectPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                isSelected = true;
            }

            var item = new PresetBenchmarkItem
            {
                Preset = p,
                Rank = rank++,
                PassedHosts = passed,
                TotalHosts = total,
                LatencyMs = latency,
                FitnessScore = fitness,
                HostChips = chips,
                IsSelected = isSelected,
                SelectedChanged = UpdateSelectedCount
            };
            _benchmarkItems.Add(item);
        }

        UpdateSelectedCount();
    }

    private void OnRefreshPresets(object sender, RoutedEventArgs e)
    {
        ReloadPresetsFromDisk();
        AppendLog("🔄 Список пресетов обновлен с диска.", Brushes.DeepSkyBlue);
    }

    private void UpdateSelectedCount()
    {
        int count = _benchmarkItems.Count(p => p.IsSelected);
        SelectedCountText.Text = $"Отобрано для эволюции: {count} из {_benchmarkItems.Count}";
        StartEvolutionButton.Content = $"🧬 Запустить эволюцию из выбранных ({count})";
        StartEvolutionButton.IsEnabled = count > 0;
    }

    private List<string> GetSelectedTargetHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in _domainGroups.Where(g => g.IsChecked))
        {
            foreach (var h in group.Hosts)
                hosts.Add(h);
        }

        if (!string.IsNullOrWhiteSpace(CustomHostsBox.Text))
        {
            var custom = CustomHostsBox.Text
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s));

            foreach (var c in custom)
                hosts.Add(c);
        }

        if (hosts.Count == 0)
        {
            hosts.Add("www.youtube.com");
            hosts.Add("discord.com");
        }

        return hosts.ToList();
    }

    private void ApplySettingsToUi()
    {
        PopSizeSlider.Value = _settings.PopulationSize;
        PopSizeLabel.Text = _settings.PopulationSize.ToString();

        GenerationsSlider.Value = _settings.MaxGenerations;
        GenerationsLabel.Text = _settings.MaxGenerations.ToString();

        ConvergeSlider.Value = (int)(_settings.ConvergeThreshold * 100);
        ConvergeLabel.Text = $"{ConvergeSlider.Value}%";

        MutationSlider.Value = (int)(_settings.MutationRate * 100);
        MutationLabel.Text = $"{MutationSlider.Value}%";

        IslandCheck.IsChecked = _settings.IslandModelEnabled;
        MemeticCheck.IsChecked = _settings.EnableMemeticFineTuning;
        SynergyCheck.IsChecked = _settings.EnableSynergyVerification;
    }

    private void SyncUiToSettings()
    {
        _settings.PopulationSize = (int)PopSizeSlider.Value;
        _settings.MaxGenerations = (int)GenerationsSlider.Value;
        _settings.ConvergeThreshold = ConvergeSlider.Value / 100.0;
        _settings.MutationRate = MutationSlider.Value / 100.0;
        _settings.IslandModelEnabled = IslandCheck.IsChecked == true;
        _settings.EnableMemeticFineTuning = MemeticCheck.IsChecked == true;
        _settings.EnableSynergyVerification = SynergyCheck.IsChecked == true;

        _settings.TargetHosts = GetSelectedTargetHosts();
        _settings.Save();
    }

    private void OnPopSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PopSizeLabel is not null)
            PopSizeLabel.Text = ((int)e.NewValue).ToString();
    }

    private void OnGenerationsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GenerationsLabel is not null)
            GenerationsLabel.Text = ((int)e.NewValue).ToString();
    }

    private void OnConvergeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConvergeLabel is not null)
            ConvergeLabel.Text = $"{(int)e.NewValue}%";
    }

    private void OnMutationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MutationLabel is not null)
            MutationLabel.Text = $"{(int)e.NewValue}%";
    }

    private void OnSelectPopularDomains(object sender, RoutedEventArgs e)
    {
        foreach (var d in _domainGroups)
        {
            d.IsChecked = d.Name is "YouTube" or "Discord" or "Telegram" or "Twitch";
        }
    }

    private void OnSelectAllDomains(object sender, RoutedEventArgs e)
    {
        foreach (var d in _domainGroups)
            d.IsChecked = true;
    }

    private void OnDeselectAllDomains(object sender, RoutedEventArgs e)
    {
        foreach (var d in _domainGroups)
            d.IsChecked = false;
    }

    private void OnSelectTop3(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _benchmarkItems.Count; i++)
            _benchmarkItems[i].IsSelected = i < 3;
        UpdateSelectedCount();
    }

    private void OnSelectTop5(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _benchmarkItems.Count; i++)
            _benchmarkItems[i].IsSelected = i < 5;
        UpdateSelectedCount();
    }

    private void OnSelectAllPresets(object sender, RoutedEventArgs e)
    {
        foreach (var item in _benchmarkItems)
            item.IsSelected = true;
        UpdateSelectedCount();
    }

    private void OnDeselectAllPresets(object sender, RoutedEventArgs e)
    {
        foreach (var item in _benchmarkItems)
            item.IsSelected = false;
        UpdateSelectedCount();
    }

    private void AppendLog(string message, Brush? color = null)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            _logEntries.Add(new EvolutionLogEntry(time, message, color ?? Brushes.LightGray));
            LogScrollViewer.ScrollToEnd();
        });
    }

    private async void OnBenchmarkAllPresets(object sender, RoutedEventArgs e)
    {
        var targetHosts = GetSelectedTargetHosts();
        if (targetHosts.Count == 0)
        {
            MessageBox.Show("Пожалуйста, выберите хотя бы один целевой домен для проверки.", "Бенчмарк", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var zapretPaths = ZapretPaths.Discover();
        var zapretRoot = zapretPaths?.Root;
        if (zapretRoot is null)
        {
            MessageBox.Show("Каталог Zapret не найден.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var files = ZapretPaths.PresetFiles;
        var presets = new PresetReader().Read(files);
        if (presets.Count == 0)
        {
            MessageBox.Show("Не найдены файлы пресетов для тестирования.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BenchmarkAllButton.IsEnabled = false;
        StopBenchmarkButton.Visibility = Visibility.Visible;
        BenchmarkProgressBar.Visibility = Visibility.Visible;
        BenchmarkProgressBar.Value = 0;
        BenchmarkStatusText.Visibility = Visibility.Visible;
        StartEvolutionButton.IsEnabled = false;

        _benchmarkCts = new CancellationTokenSource();
        var ct = _benchmarkCts.Token;

        bool wasRunning = EngineControl.IsRunning;
        var probe = new FitnessProbe(zapretRoot, zapretPaths?.ExecutablePath);

        AppendLog($"⚡ Запуск бенчмарка: проверка {presets.Count} пресетов на {targetHosts.Count} доменах...", Brushes.DeepSkyBlue);

        var conflictReport = EnvironmentConflictChecker.Check();
        if (conflictReport.HasConflicts)
        {
            foreach (var conflict in conflictReport.Conflicts)
            {
                AppendLog($"⚠ Внимание: {conflict}", Brushes.Orange);
            }
        }

        var benchmarkSettings = new EvolutionSettings
        {
            TargetHosts = targetHosts,
            ProbeTimeoutSeconds = 4,
            ProbeRetries = 1
        };

        // Предварительно отображаем все найденные пресеты в таблице со статусом «Ожидает»
        var existingSelected = _benchmarkItems.Where(x => x.IsSelected).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _benchmarkItems.Clear();
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            _benchmarkItems.Add(new PresetBenchmarkItem
            {
                Preset = p,
                Rank = i + 1,
                IsSelected = existingSelected.Contains(p.Name),
                SelectedChanged = UpdateSelectedCount
            });
        }

        try
        {
            if (wasRunning)
            {
                BenchmarkStatusText.Text = "Освобождение порта winws2 для изолированных замеров...";
                await EngineControl.StopAsync("бенчмарк", ct);
                await Task.Delay(1000, ct);
            }

            for (int i = 0; i < presets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var preset = presets[i];
                var uiItem = _benchmarkItems[i];
                uiItem.IsEvaluating = true;

                BenchmarkStatusText.Text = $"Тестирование {i + 1}/{presets.Count}: «{preset.Name}»...";
                BenchmarkProgressBar.Value = (i * 100.0) / presets.Count;

                var individual = PresetIndividual.FromPreset(preset, 0);
                var evalResult = await probe.EvaluateAsync(individual, preset, benchmarkSettings, ct);

                var chips = new List<HostProbeChip>();
                foreach (var (host, outcome) in evalResult.HostResults)
                {
                    chips.Add(new HostProbeChip(host, outcome.Success, outcome.LatencyMs));
                }

                uiItem.PassedHosts = evalResult.Passed;
                uiItem.TotalHosts = evalResult.Total;
                uiItem.LatencyMs = evalResult.AvgLatencyMs;
                uiItem.FitnessScore = evalResult.Score;
                uiItem.HostChips = chips;
                uiItem.IsEvaluating = false;

                BenchmarkProgressBar.Value = (i + 1) * 100.0 / presets.Count;

                // Live-логирование каждого протестированного пресета в живой журнал
                var passedSummary = evalResult.HostResults
                    .Where(kv => kv.Value.Success)
                    .Select(kv => $"{kv.Key} ✔ ({kv.Value.LatencyMs}мс)");
                var failedSummary = evalResult.HostResults
                    .Where(kv => !kv.Value.Success)
                    .Select(kv => $"{kv.Key} ✖ ({kv.Value.Detail})");

                var hostBreakdown = string.Join(", ", passedSummary.Concat(failedSummary));
                var logBrush = evalResult.Passed == evalResult.Total
                    ? Brushes.LimeGreen
                    : (evalResult.Passed > 0 ? Brushes.LightSkyBlue : Brushes.OrangeRed);

                AppendLog($"[{i + 1}/{presets.Count}] «{preset.Name}»: открыто {evalResult.Passed}/{evalResult.Total} хостов, пинг {evalResult.AvgLatencyMs}мс, фитнес {evalResult.Score:P0} [{hostBreakdown}]", logBrush);
            }

            // Сортировка: сначала наибольший пробив хостов, затем максимальный фитнес, затем минимальный пинг
            var sorted = _benchmarkItems
                .OrderByDescending(x => x.PassedHosts)
                .ThenByDescending(x => x.FitnessScore)
                .ThenBy(x => x.LatencyMs)
                .ToList();

            _benchmarkItems.Clear();
            for (int i = 0; i < sorted.Count; i++)
            {
                sorted[i].Rank = i + 1;
                sorted[i].IsSelected = i < 3; // Топ-3 выбраны автоматически
                _benchmarkItems.Add(sorted[i]);
            }

            var leader = sorted.FirstOrDefault();
            if (leader != null)
            {
                AppendLog($"🏆 Бенчмарк завершён! Лидер: «{leader.Name}» ({leader.PassedHosts}/{leader.TotalHosts} хостов, пинг {leader.LatencyMs}мс, фитнес {leader.FitnessScore:P0})", Brushes.LimeGreen);
            }

            BenchmarkStatusText.Text = $"Бенчмарк завершён. Найдено {sorted.Count} пресетов, топ-3 отмечены.";
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusText.Text = "Тестирование пресетов остановлено пользователем.";
            AppendLog("Бенчмарк остановлен пользователем.", Brushes.Orange);
        }
        catch (Exception ex)
        {
            BenchmarkStatusText.Text = $"Ошибка бенчмарка: {ex.Message}";
            AppendLog($"Ошибка бенчмарка: {ex.Message}", Brushes.OrangeRed);
        }
        finally
        {
            foreach (var item in _benchmarkItems)
                item.IsEvaluating = false;

            if (wasRunning)
            {
                _ = EngineControl.StartAsync("бенчмарк", CancellationToken.None);
            }

            BenchmarkAllButton.IsEnabled = true;
            StopBenchmarkButton.Visibility = Visibility.Collapsed;
            BenchmarkProgressBar.Visibility = Visibility.Collapsed;
            _benchmarkCts = null;
            UpdateSelectedCount();
        }
    }

    private void OnStopBenchmark(object sender, RoutedEventArgs e)
    {
        _benchmarkCts?.Cancel();
        StopBenchmarkButton.IsEnabled = false;
    }

    private void OnTargetSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetSectionCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.TargetSectionKey = tag.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : tag;
            if (TargetSectionHint is not null)
            {
                TargetSectionHint.Text = tag switch
                {
                    "youtube" => "Фокус только на YouTube/googlevideo (остальные секции защищены).",
                    "discord" => "Фокус только на Discord голосовых шлюзах и каналах.",
                    "general" => "Фокус только на общих веб-блокировках.",
                    _ => "Будут оптимизированы все секции пресета."
                };
            }
        }
    }

    private async void OnStartMicroEvolution(object sender, RoutedEventArgs e)
    {
        _settings.FastSprintMode = true;
        if (string.IsNullOrWhiteSpace(_settings.TargetSectionKey))
        {
            _settings.TargetSectionKey = "youtube";
            if (TargetSectionCombo is not null)
                TargetSectionCombo.SelectedIndex = 1;
        }
        await ExecuteEvolutionAsync(resume: false);
    }

    private async void OnStartEvolution(object sender, RoutedEventArgs e)
    {
        _settings.FastSprintMode = false;
        await ExecuteEvolutionAsync(resume: false);
    }

    private async void OnResumeEvolution(object sender, RoutedEventArgs e)
    {
        await ExecuteEvolutionAsync(resume: true);
    }

    private void OnStopEvolution(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        PhaseText.Text = "Остановка пользователем...";
        StopEvolutionButton.IsEnabled = false;
        AppendLog("Эволюция останавливается по запросу пользователя...", Brushes.Orange);
    }

    private async Task ExecuteEvolutionAsync(bool resume)
    {
        var selectedItems = _benchmarkItems.Where(p => p.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            MessageBox.Show("Пожалуйста, отметьте галочками хотя бы один пресет для участия в эволюции.", "Эволюция", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedPresets = selectedItems.Select(x => x.Preset).ToList();
        _lastBasePreset = selectedPresets[0];

        SyncUiToSettings();

        var zapretPaths = ZapretPaths.Discover();
        var zapretRoot = zapretPaths?.Root;
        if (zapretRoot is null)
        {
            MessageBox.Show("Каталог Zapret не найден.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        StartEvolutionButton.IsEnabled = false;
        ResumeEvolutionButton.IsEnabled = false;
        StopEvolutionButton.IsEnabled = true;
        BenchmarkAllButton.IsEnabled = false;
        ExportCard.Visibility = Visibility.Collapsed;
        _fitnessHistory.Clear();
        FitnessHistoryCard.Visibility = Visibility.Collapsed;
        _initialBaseFitness = 0.0;
        _lastLoggedGen = -1;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var progress = new Progress<EvolutionProgress>(UpdateProgressUi);

        var probe = new FitnessProbe(zapretRoot, zapretPaths?.ExecutablePath);
        var store = new EvolutionStore();
        var exporter = new PresetExporter();
        var engine = new EvolutionEngine(probe, store, exporter);

        bool wasRunning = EngineControl.IsRunning;

        AppendLog($"🧬 Старт эволюции: засевание генофонда из {selectedPresets.Count} родительских пресетов...", Brushes.Cyan);
        foreach (var p in selectedPresets.Take(3))
        {
            AppendLog($"  • Родительский донор: «{p.Name}»", Brushes.DeepSkyBlue);
        }

        var envConflict = EnvironmentConflictChecker.Check();
        if (envConflict.HasConflicts)
        {
            foreach (var conflict in envConflict.Conflicts)
            {
                AppendLog($"⚠ Внимание: {conflict}", Brushes.Orange);
            }
        }

        try
        {
            if (wasRunning)
            {
                PhaseText.Text = "Освобождение сетевого драйвера winws2 для эволюции...";
                await EngineControl.StopAsync("эволюция", ct);
                await Task.Delay(1000, ct);
            }

            var outputDir = ZapretPaths.PresetDirectory;
            var result = resume
                ? await engine.ResumeAsync(selectedPresets[0], zapretRoot, _settings, outputDir, progress, ct)
                : await engine.RunAsync(selectedPresets, zapretRoot, _settings, outputDir, progress, ct);

            _lastExportedFile = result.ExportPath;
            _lastWinner = result.Winner;
            var evolvedStem = Path.GetFileNameWithoutExtension(result.ExportPath);
            ExportPathText.Text = $"Файл: {Path.GetFileName(result.ExportPath)}";
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "🚀 Применить пресет";
            ABTestResultCard.Visibility = Visibility.Collapsed;
            ExportCard.Visibility = Visibility.Visible;
            PhaseText.Text = "🎉 Эволюция успешно завершена!";

            ReloadPresetsFromDisk(selectPresetName: evolvedStem);

            AppendLog($"🎉 Эволюция триумфально завершена! Победитель: {result.Winner.Genes.Count} секций, фитнес {result.Winner.FitnessScore:P0}", Brushes.LimeGreen);
            AppendLog($"🧬 Пресет «{evolvedStem}» сохранён и добавлен в список для дальнейшей эволюции!", Brushes.LimeGreen);
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Процесс эволюции остановлен.";
            AppendLog("Процесс эволюции прерван пользователем.", Brushes.Orange);
        }
        catch (Exception ex)
        {
            PhaseText.Text = $"Ошибка: {ex.Message}";
            AppendLog($"Сбой эволюции: {ex.Message}", Brushes.OrangeRed);
        }
        finally
        {
            if (wasRunning)
            {
                _ = EngineControl.StartAsync("эволюция", CancellationToken.None);
            }

            StartEvolutionButton.IsEnabled = true;
            ResumeEvolutionButton.IsEnabled = true;
            StopEvolutionButton.IsEnabled = false;
            BenchmarkAllButton.IsEnabled = true;
            _cts = null;
        }
    }

    private void UpdateProgressUi(EvolutionProgress prog)
    {
        Dispatcher.InvokeAsync(() =>
        {
            PhaseText.Text = prog.Phase;
            GenerationProgressText.Text = $"Поколение {prog.Generation} из {prog.MaxGenerations} · {prog.BestPresetName}";
            OverallFitnessText.Text = $"Фитнес: {prog.BestFitness:P0}";

            int pct = prog.MaxGenerations > 0 ? (int)(prog.Generation * 100.0 / prog.MaxGenerations) : 0;
            EvolutionProgressBar.Value = pct;

            BestPassedText.Text = $"{prog.BestPassed} / {prog.BestTotal}";
            BestLatencyText.Text = $"{prog.BestLatencyMs} мс";
            GenerationsDoneText.Text = $"{prog.Generation} / {prog.MaxGenerations}";

            if (_initialBaseFitness <= 0.001 && prog.BestFitness > 0)
            {
                _initialBaseFitness = prog.BestFitness;
            }

            double gain = prog.BestFitness - _initialBaseFitness;
            FitnessGainText.Text = gain >= 0 ? $"+{gain:P0}" : $"{gain:P0}";

            if (!string.IsNullOrEmpty(prog.SynergyReport))
            {
                SynergyReportText.Text = prog.SynergyReport;
                SynergyReportText.Visibility = Visibility.Visible;
            }
            else
            {
                SynergyReportText.Visibility = Visibility.Collapsed;
            }

            if (prog.DetectedDpiProfile is not null)
            {
                DpiProfileText.Text = $"DPI: {prog.DetectedDpiProfile.IspFilterType}";
                DpiProfileBadge.Visibility = Visibility.Visible;
            }

            if (prog.DiffEntries is not null && prog.DiffEntries.Count > 0)
            {
                _diffEntries.Clear();
                foreach (var d in prog.DiffEntries)
                {
                    var brush = d.Kind switch
                    {
                        DiffChangeKind.Optimized => Brushes.LimeGreen,
                        DiffChangeKind.Pruned => Brushes.DodgerBlue,
                        DiffChangeKind.FineTuned => Brushes.Orange,
                        _ => Brushes.Gray
                    };
                    var bg = d.Kind switch
                    {
                        DiffChangeKind.Optimized => new SolidColorBrush(Color.FromArgb(0x22, 0x35, 0xE6, 0xA0)),
                        DiffChangeKind.Pruned => new SolidColorBrush(Color.FromArgb(0x22, 0x21, 0x96, 0xF3)),
                        DiffChangeKind.FineTuned => new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0x98, 0x00)),
                        _ => new SolidColorBrush(Color.FromArgb(0x22, 0x9E, 0x9E, 0x9E))
                    };
                    _diffEntries.Add(new PresetDiffUiModel(
                        d.SectionName,
                        d.OldSteps,
                        d.NewSteps,
                        d.Explanation,
                        d.StatusBadge,
                        brush,
                        bg
                    ));
                }
            }

            if (prog.WinnerIndividual is not null)
            {
                _lastWinner = prog.WinnerIndividual;
            }

            if (!string.IsNullOrEmpty(prog.LiveOutcomeDetail))
            {
                var brush = prog.BestPassed == prog.BestTotal
                    ? Brushes.LimeGreen
                    : (prog.BestPassed > 0 ? Brushes.LightSkyBlue : Brushes.Orange);
                AppendLog(prog.LiveOutcomeDetail, brush);
            }

            // Логируем смену поколений
            if (prog.Generation != _lastLoggedGen && prog.Generation > 0)
            {
                _lastLoggedGen = prog.Generation;
                AppendLog($"Поколение {prog.Generation}: открыто {prog.BestPassed}/{prog.BestTotal} хостов, фитнес {prog.BestFitness:P0}, пинг {prog.BestLatencyMs}мс",
                    prog.BestPassed == prog.BestTotal ? Brushes.LimeGreen : Brushes.LightGray);

                if (!_fitnessHistory.Any(p => p.Generation == prog.Generation))
                {
                    _fitnessHistory.Add(new FitnessHistoryPoint(
                        prog.Generation,
                        prog.BestFitness,
                        prog.BestPassed,
                        prog.BestTotal
                    ));
                    FitnessHistoryCard.Visibility = Visibility.Visible;
                    HistoryPointsSummary.Text = $"Оптимум: {prog.BestFitness:P0} ({prog.BestPassed}/{prog.BestTotal} хостов)";
                }
            }

            // Обновляем детальные строки сетевых проб
            _probeRows.Clear();
            foreach (var (host, success) in prog.BestHostResults)
            {
                _probeRows.Add(new ProbeRowUi(
                    Host: host,
                    Status: success ? "✔ Доступен" : "✖ Заблокирован",
                    StatusColor: success ? Brushes.LimeGreen : Brushes.OrangeRed,
                    Latency: success ? $"{prog.BestLatencyMs} мс" : "—",
                    Detail: success ? "TLS-рукопожатие успешно, фильтр пройден" : "Таймаут или сброс соединения (RST)"
                ));
            }
        });
    }

    private async void OnApplyPreset(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastExportedFile) || !File.Exists(_lastExportedFile))
        {
            MessageBox.Show("Файл адаптированного пресета не найден.", "Применение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            ApplyButton.IsEnabled = false;
            ApplyButton.Content = "Применяю...";

            var presetStem = Path.GetFileNameWithoutExtension(_lastExportedFile);
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            _previousPresetName = settings.PresetName;
            if (!string.IsNullOrEmpty(_previousPresetName))
            {
                RollbackButton.Content = $"↩️ Откатить на «{_previousPresetName}»";
                RollbackButton.Visibility = Visibility.Visible;
            }

            settings = settings with { PresetName = presetStem };
            settings.Save();

            AppendLog($"🚀 Применение пресета «{presetStem}» и перезапуск служб...", Brushes.Gold);

            // Перезапускаем движки с новым пресетом
            var result = await EngineControl.RestartAsync("эволюция: применить", CancellationToken.None);

            ApplyButton.Content = "Применено ✔";
            PhaseText.Text = result.Ok
                ? $"Пресет «{presetStem}» успешно применён и запущен!"
                : $"Пресет выбран, но перезапуск сообщил: {result.Message}";

            AppendLog(result.Ok
                ? $"✔ Пресет «{presetStem}» активирован, службы winws2 и tg-ws-proxy перезапущены!"
                : $"⚠ Пресет выбран, сообщение перезапуска: {result.Message}",
                result.Ok ? Brushes.LimeGreen : Brushes.Orange);
        }
        catch (Exception ex)
        {
            ApplyButton.IsEnabled = true;
            ApplyButton.Content = "🚀 Применить пресет";
            MessageBox.Show($"Ошибка применения пресета: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRollbackPreset(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_previousPresetName))
            return;

        try
        {
            RollbackButton.IsEnabled = false;
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings = settings with { PresetName = _previousPresetName };
            settings.Save();

            AppendLog($"↩️ Выполняется откат к предыдущему стабильному пресету «{_previousPresetName}»...", Brushes.Gold);
            var result = await EngineControl.RestartAsync("эволюция: откат", CancellationToken.None);

            RollbackButton.Visibility = Visibility.Collapsed;
            PhaseText.Text = result.Ok
                ? $"Выполнен откат к пресету «{_previousPresetName}»!"
                : $"Откат выполнен, но перезапуск сообщил: {result.Message}";

            AppendLog(result.Ok
                ? $"✔ Стабильный пресет «{_previousPresetName}» восстановлен!"
                : $"⚠ Сообщение отката: {result.Message}",
                result.Ok ? Brushes.LimeGreen : Brushes.Orange);
        }
        catch (Exception ex)
        {
            RollbackButton.IsEnabled = true;
            MessageBox.Show($"Ошибка отката пресета: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRunABTest(object sender, RoutedEventArgs e)
    {
        if (_lastWinner is null || _lastBasePreset is null)
        {
            MessageBox.Show("Нет данных для A/B тестирования. Запустите эволюцию пресета.", "A/B Тест", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            ABTestButton.IsEnabled = false;
            ABStatusText.Text = "Запуск прямого A/B тестирования в бою...";
            ABTestResultCard.Visibility = Visibility.Visible;

            var zapretPaths = ZapretPaths.Discover();
            if (zapretPaths?.Root is null)
                return;

            var probe = new FitnessProbe(zapretPaths.Root, zapretPaths.ExecutablePath);
            var abProbe = new ABComparisonProbe(probe);

            AppendLog($"⚔️ Запуск прямого A/B тестирования: «{_lastBasePreset.Name}» против «Evolved (Gen {_lastWinner.GenerationNumber})»...", Brushes.Cyan);

            var abResult = await abProbe.CompareAsync(_lastBasePreset, _lastWinner, _settings, CancellationToken.None);

            ABBaseTitleText.Text = $"Базовый: {_lastBasePreset.Name}";
            ABBaseHostsText.Text = $"Доступность: {abResult.BasePassedHosts}/{abResult.TotalHosts} хостов";
            ABBaseLatencyText.Text = $"Пинг: {abResult.BaseAvgLatencyMs} мс";
            ABBaseThroughputText.Text = $"L7 Throughput: {abResult.BaseThroughputKBps:F0} КБ/с";

            ABEvolvedTitleText.Text = $"🧬 {abResult.EvolvedName}";
            ABEvolvedHostsText.Text = $"Доступность: {abResult.EvolvedPassedHosts}/{abResult.TotalHosts} хостов";
            ABEvolvedLatencyText.Text = $"Пинг: {abResult.EvolvedAvgLatencyMs} мс";
            ABEvolvedThroughputText.Text = $"L7 Throughput: {abResult.EvolvedThroughputKBps:F0} КБ/с";

            ABVerdictText.Text = abResult.Verdict;
            ABStatusText.Text = "Тест завершён ✔";

            AppendLog($"⚔️ Вердикт A/B: {abResult.Verdict}", abResult.EvolvedIsSuperior ? Brushes.LimeGreen : Brushes.Orange);
        }
        catch (Exception ex)
        {
            ABStatusText.Text = $"Ошибка: {ex.Message}";
            AppendLog($"Ошибка A/B теста: {ex.Message}", Brushes.OrangeRed);
        }
        finally
        {
            ABTestButton.IsEnabled = true;
        }
    }

    private void OnSearchPresetTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchPresetBox.Text?.Trim() ?? "";
        SearchPresetPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_benchmarkItems);
        if (view is null)
            return;

        if (string.IsNullOrEmpty(query))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = item =>
            {
                if (item is PresetBenchmarkItem p)
                {
                    return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.EvolvedBadge.Contains(query, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            };
        }
    }

    private void OnCopyPresetContent(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastExportedFile) && File.Exists(_lastExportedFile))
        {
            try
            {
                var content = File.ReadAllText(_lastExportedFile);
                Clipboard.SetText(content);
                AppendLog("📋 Содержимое пресета скопировано в буфер обмена!", Brushes.LimeGreen);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось скопировать: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastExportedFile) && File.Exists(_lastExportedFile))
        {
            Process.Start("explorer.exe", $"/select,\"{_lastExportedFile}\"");
        }
        else
        {
            Process.Start("explorer.exe", ZapretPaths.PresetDirectory);
        }
    }
}
