using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Информация об обнаруженной деградации обхода конкретного сервиса.
/// </summary>
public sealed record DegradationReport(
    string ServiceName,
    string SectionKey,
    string FailedHost,
    int ConsecutiveFailures,
    DateTime DetectedAtUtc,
    string ErrorDetail);

/// <summary>
/// Результат процедуры самоисцеления (Self-Healing).
/// </summary>
public sealed record SelfHealingResult(
    bool Success,
    string SectionKey,
    string? HealedPresetPath,
    double OldFitness,
    double NewFitness,
    string Message);

/// <summary>
/// Фоновый страж самоисцеления (Self-Healing Sentinel).
/// Непрерывно следит за доступностью ключевых сервисов, выявляет признаки свежих блокировок ТСПУ
/// и при необходимости запускает точечную микроэволюцию проблемной секции без вмешательства пользователя.
/// </summary>
public sealed class EvolutionSentinel : IDisposable
{
    private readonly FitnessProbe _probe;
    private readonly EvolutionEngine _engine;
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _isHealingActive;

    public event Action<DegradationReport>? DegradationDetected;
    public event Action<SelfHealingResult>? SelfHealingCompleted;

    public bool IsMonitoring => _monitorTask != null && !_monitorTask.IsCompleted;
    public bool IsHealingActive => _isHealingActive;

    public static readonly IReadOnlyDictionary<string, (string SectionKey, string[] Hosts)> MonitoredServices =
        new Dictionary<string, (string SectionKey, string[] Hosts)>(StringComparer.OrdinalIgnoreCase)
        {
            ["YouTube"] = ("youtube", ["www.youtube.com", "googlevideo.com"]),
            ["Discord"] = ("discord", ["discord.com", "gateway.discord.gg"]),
            ["Telegram"] = ("general", ["web.telegram.org"])
        };

    public EvolutionSentinel(FitnessProbe probe, EvolutionEngine engine)
    {
        _probe = probe;
        _engine = engine;
    }

    /// <summary>
    /// Запускает фоновое наблюдение с заданным интервалом проверки (по умолчанию 90 секунд).
    /// </summary>
    public void StartMonitoring(TimeSpan? interval = null)
    {
        lock (_lock)
        {
            if (IsMonitoring) return;

            _cts = new CancellationTokenSource();
            var checkInterval = interval ?? TimeSpan.FromSeconds(90);
            _monitorTask = Task.Run(() => MonitorLoopAsync(checkInterval, _cts.Token));
        }
    }

    /// <summary>
    /// Останавливает фоновое наблюдение.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _monitorTask = null;
        }
    }

    /// <summary>
    /// Выполняет быструю экспресс-проверку текущего состояния всех сервисов прямо сейчас.
    /// </summary>
    public async Task<List<DegradationReport>> CheckHealthNowAsync(CancellationToken ct = default)
    {
        var reports = new List<DegradationReport>();

        foreach (var (service, (sectionKey, hosts)) in MonitoredServices)
        {
            ct.ThrowIfCancellationRequested();

            bool serviceFailed = false;
            string lastError = "";
            string failedHost = "";

            foreach (var host in hosts)
            {
                var outcome = await _probe.ProbeHostAsync(host, 4, ct);
                if (!outcome.Success)
                {
                    serviceFailed = true;
                    failedHost = host;
                    lastError = outcome.Detail;
                    break;
                }
            }

            lock (_lock)
            {
                _consecutiveFailures.TryGetValue(service, out int count);
                if (serviceFailed)
                {
                    count++;
                    _consecutiveFailures[service] = count;

                    // Если 2 и более сбоев подряд — сигнализируем о деградации
                    if (count >= 2)
                    {
                        var report = new DegradationReport(
                            ServiceName: service,
                            SectionKey: sectionKey,
                            FailedHost: failedHost,
                            ConsecutiveFailures: count,
                            DetectedAtUtc: DateTime.UtcNow,
                            ErrorDetail: lastError
                        );
                        reports.Add(report);
                    }
                }
                else
                {
                    _consecutiveFailures[service] = 0;
                }
            }
        }

        return reports;
    }

    /// <summary>
    /// Выполняет процедуру самоисцеления для проблемной секции (микро-эволюция за 6-10 секунд).
    /// </summary>
    public async Task<SelfHealingResult> HealServiceAsync(
        string sectionKey,
        ZapretPreset currentPreset,
        string zapretRoot,
        string outputDirectory,
        IProgress<EvolutionProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_isHealingActive)
            return new SelfHealingResult(false, sectionKey, null, 0, 0, "Самоисцеление уже выполняется.");

        _isHealingActive = true;
        try
        {
            var sprintSettings = new EvolutionSettings
            {
                FastSprintMode = true,
                TargetSectionKey = sectionKey,
                MaxGenerations = 4,
                PopulationSize = 6,
                ProbeTimeoutSeconds = 3,
                ProbeRetries = 1,
                TargetHosts = MonitoredServices.Values
                    .Where(v => v.SectionKey.Equals(sectionKey, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(v => v.Hosts)
                    .Distinct()
                    .ToList()
            };

            var dummyProgress = progress ?? new Progress<EvolutionProgress>();
            var result = await _engine.RunAsync(
                [currentPreset],
                zapretRoot,
                sprintSettings,
                outputDirectory,
                dummyProgress,
                ct);

            double oldFit = currentPreset.Sections.Count > 0 ? 0.5 : 0.0;
            double newFit = result.Winner.FitnessScore;

            var healingRes = new SelfHealingResult(
                Success: newFit > 0.6,
                SectionKey: sectionKey,
                HealedPresetPath: result.ExportPath,
                OldFitness: oldFit,
                NewFitness: newFit,
                Message: newFit > 0.6
                    ? $"Секция «{sectionKey}» успешно самоисцелена (новый фитнес: {newFit:P0})!"
                    : $"Самоисцеление секции «{sectionKey}» завершено, но фитнес остался ниже нормы ({newFit:P0})."
            );

            // Сбрасываем счетчик сбоев для восстановленного сервиса
            lock (_lock)
            {
                foreach (var (svc, (sec, _)) in MonitoredServices)
                {
                    if (sec.Equals(sectionKey, StringComparison.OrdinalIgnoreCase))
                        _consecutiveFailures[svc] = 0;
                }
            }

            SelfHealingCompleted?.Invoke(healingRes);
            return healingRes;
        }
        catch (Exception ex)
        {
            var failRes = new SelfHealingResult(false, sectionKey, null, 0, 0, $"Ошибка самоисцеления: {ex.Message}");
            SelfHealingCompleted?.Invoke(failRes);
            return failRes;
        }
        finally
        {
            _isHealingActive = false;
        }
    }

    private async Task MonitorLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                var reports = await CheckHealthNowAsync(ct);
                foreach (var report in reports)
                {
                    DegradationDetected?.Invoke(report);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Игнорируем сетевые ошибки цикла мониторинга
            }
        }
    }

    public void Dispose()
    {
        StopMonitoring();
    }
}
