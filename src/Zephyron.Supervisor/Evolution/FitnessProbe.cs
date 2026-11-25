using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Оценивает приспособленность особи пресета через запуск winws2 и реальные сетевые TLS-пробы.
/// </summary>
public sealed class FitnessProbe
{
    private static readonly SemaphoreSlim WinwsProcessLock = new(1, 1);
    private readonly string _zapretRoot;
    private readonly string _winwsPath;

    public string ZapretRoot => _zapretRoot;
    public string WinwsPath => _winwsPath;

    public FitnessProbe(string zapretRoot, string? winwsPath = null)
    {
        _zapretRoot = zapretRoot;
        _winwsPath = ResolveWinwsPath(zapretRoot, winwsPath);
    }

    /// <summary>
    /// Выполняет одиночную проверку доступности хоста через TLS-рукопожатие.
    /// </summary>
    public async Task<ProbeOutcome> ProbeHostAsync(string host, int timeoutSeconds = 4, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var address = await RecipeTrial.AddressForAsync(host, cts.Token);
            if (address is null)
            {
                return new ProbeOutcome
                {
                    Success = false,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Detail = "DNS не разрешил имя хоста"
                };
            }

            var flow = await RecipeTrial.OpensAsync(host, address, cts.Token);
            sw.Stop();

            return new ProbeOutcome
            {
                Success = flow.Passed && !flow.Stub,
                LatencyMs = sw.ElapsedMilliseconds,
                Detail = flow.Ending
            };
        }
        catch (OperationCanceledException)
        {
            return new ProbeOutcome
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Detail = "Таймаут подключения"
            };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Success = false,
                LatencyMs = sw.ElapsedMilliseconds,
                Detail = RecipeTrial.Short(ex)
            };
        }
    }

    private static string ResolveWinwsPath(string zapretRoot, string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var fullRoot = Path.GetFullPath(zapretRoot);
        string[] directCandidates =
        [
            Path.Combine(fullRoot, "exe", "winws2.exe"),
            Path.Combine(fullRoot, "winws2.exe"),
            Path.Combine(fullRoot, "exe", "winws.exe"),
            Path.Combine(fullRoot, "bin", "winws.exe"),
        ];

        foreach (var c in directCandidates)
        {
            if (File.Exists(c))
                return c;
        }

        // Поиск вверх по каталогам (для тестов, IDE и корня репозитория)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string[] searchCandidates =
            [
                Path.Combine(dir.FullName, "dist", "Zephyron", "engines", "zapret", "exe", "winws2.exe"),
                Path.Combine(dir.FullName, "build", "engines", "zapret", "exe", "winws2.exe"),
                Path.Combine(dir.FullName, "tools", "winws2.exe")
            ];

            foreach (var sc in searchCandidates)
            {
                if (File.Exists(sc))
                    return sc;
            }

            dir = dir.Parent;
        }

        return directCandidates[0];
    }

    /// <summary>
    /// Оценивает особь: формирует пресет из генома, запускает с ним изолированный процесс winws2,
    /// выполняет параллельные сетевые пробы и рассчитывает фитнес-балл.
    /// </summary>
    public async Task<FitnessResult> EvaluateAsync(
        PresetIndividual individual,
        ZapretPreset templatePreset,
        EvolutionSettings settings,
        CancellationToken ct)
    {
        var preset = individual.ToPreset(templatePreset);
        var arguments = new List<string>(WinwsCommandLine.Build(preset));

        // Устанавливаем изолирующий высокий приоритет WinDivert для тестового экземпляра
        if (!arguments.Any(a => a.StartsWith("--wf-priority", StringComparison.OrdinalIgnoreCase)))
        {
            arguments.Insert(0, "--wf-priority=1000");
        }
        if (!arguments.Any(a => a.StartsWith("--wf-dup-check", StringComparison.OrdinalIgnoreCase)))
        {
            arguments.Insert(0, "--wf-dup-check=0");
        }

        // Если бинарник winws не существует или нет прав — эмулируем безопасный провал
        if (!File.Exists(_winwsPath))
        {
            var fallback = new Dictionary<string, ProbeOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in settings.TargetHosts)
            {
                fallback[h] = new ProbeOutcome
                {
                    Success = false,
                    LatencyMs = 0,
                    Detail = "winws.exe не найден"
                };
            }

            return new FitnessResult
            {
                Score = 0.0,
                Passed = 0,
                Total = settings.TargetHosts.Count,
                AvgLatencyMs = 0,
                StreamingSpeedKBps = 0.0,
                DiscordVoicePassed = false,
                HostResults = fallback
            };
        }

        await WinwsProcessLock.WaitAsync(ct);
        try
        {
            var outcomes = new ConcurrentDictionary<string, ProbeOutcome>(StringComparer.OrdinalIgnoreCase);
            var latencies = new ConcurrentBag<long>();
            var throughputScores = new ConcurrentBag<double>();
            var voiceScores = new ConcurrentBag<double>();
            var rawSpeeds = new ConcurrentBag<double>();

            // Запускаем winws2 с конфигурацией данной особи
            string? launchError = await RecipeTrial.WithWinwsAsync(
                _winwsPath,
                _zapretRoot,
                arguments,
                async () =>
                {
                    // Параллельно проверяем все целевые хосты внутри запущенного экземпляра winws2
                    var hostTasks = settings.TargetHosts.Select(async host =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        var sw = Stopwatch.StartNew();
                        try
                        {
                            using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            int effectiveTimeoutSeconds = (host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
                                                           host.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                                                           host.Contains("twitch", StringComparison.OrdinalIgnoreCase))
                                ? Math.Max(settings.ProbeTimeoutSeconds, 5)
                                : Math.Max(settings.ProbeTimeoutSeconds, 3);
                            hostCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

                            var address = await RecipeTrial.AddressForAsync(host, hostCts.Token);
                            if (address is null)
                            {
                                outcomes[host] = new ProbeOutcome
                                {
                                    Success = false,
                                    LatencyMs = sw.ElapsedMilliseconds,
                                    Detail = "DNS не разрешил имя"
                                };
                                return;
                            }

                            var flow = await RecipeTrial.OpensAsync(host, address, hostCts.Token);
                            sw.Stop();

                            bool success = flow.Passed && !flow.Stub;
                            string detail = flow.Ending;

                            if (success)
                            {
                                latencies.Add(sw.ElapsedMilliseconds);

                                // L7 Throughput-проба для видеосервисов (Googlevideo, YouTube, Twitch)
                                if (host.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) ||
                                    host.Contains("youtube", StringComparison.OrdinalIgnoreCase) ||
                                    host.Contains("twitch", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var speedRes = await L7ThroughputProbe.MeasureSpeedAsync(host, address, 4, hostCts.Token);
                                        if (speedRes.Success)
                                        {
                                            throughputScores.Add(Math.Min(1.0, speedRes.SpeedKBps / 1200.0));
                                            rawSpeeds.Add(speedRes.SpeedKBps);
                                            detail += $" · {speedRes.Detail}";
                                        }
                                        else if (speedRes.IsThrottled)
                                        {
                                            throughputScores.Add(0.25);
                                            rawSpeeds.Add(speedRes.SpeedKBps);
                                            detail += $" · ⚠ {speedRes.Detail}";
                                        }
                                    }
                                    catch
                                    {
                                        // Ошибка замера скорости не сбрасывает успешность L4
                                        throughputScores.Add(0.5);
                                    }
                                }
                                // Проверка Discord Voice UDP
                                else if (host.Contains("discord", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var voiceRes = await DiscordVoiceProbe.TestVoiceUdpAsync(800, hostCts.Token);
                                        if (voiceRes.Success)
                                        {
                                            voiceScores.Add(1.0);
                                            detail += " · Voice UDP ✔";
                                        }
                                        else
                                        {
                                            voiceScores.Add(0.0);
                                            detail += " · Voice UDP ✖";
                                        }
                                    }
                                    catch
                                    {
                                        voiceScores.Add(0.5);
                                    }
                                }
                            }

                            outcomes[host] = new ProbeOutcome
                            {
                                Success = success,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Detail = detail
                            };
                        }
                        catch (Exception ex)
                        {
                            sw.Stop();
                            outcomes[host] = new ProbeOutcome
                            {
                                Success = false,
                                LatencyMs = sw.ElapsedMilliseconds,
                                Detail = RecipeTrial.Short(ex)
                            };
                        }
                    });

                    await Task.WhenAll(hostTasks);
                },
                ct);

            if (launchError is not null)
            {
                // Ошибка запуска WinDivert или процесса
                foreach (var h in settings.TargetHosts)
                {
                    outcomes[h] = new ProbeOutcome
                    {
                        Success = false,
                        LatencyMs = 0,
                        Detail = launchError
                    };
                }
            }

            int passed = outcomes.Values.Count(o => o.Success);
            int total = settings.TargetHosts.Count;
            long avgLatency = latencies.Count > 0 ? (long)latencies.Average() : 2000;
            double maxSpeed = rawSpeeds.Count > 0 ? rawSpeeds.Max() : 0.0;
            bool voicePassed = voiceScores.Any(v => v >= 0.9);

            // Формула фитнеса 2.0 (Многокритериальная оптимизация с L7 Throughput и Discord Voice):
            // 0.55 * passRatio (проходимость L4/TLS)
            // 0.20 * speedBonus (пропускная способность видеостриминга)
            // 0.10 * voiceBonus (прохождение UDP Discord Voice)
            // 0.10 * latencyBonus (минимальный пинг)
            // 0.05 * efficiencyBonus (компактность цепочки пакетов)
            double passRatio = total > 0 ? (double)passed / total : 0.0;
            double speedBonus = throughputScores.Count > 0 ? throughputScores.Average() : 0.5;
            double voiceBonus = voiceScores.Count > 0 ? voiceScores.Average() : 0.5;
            
            // Быстрый отклик (<80мс -> ~0.98, 200мс -> 0.90, 500мс -> 0.69, >1500мс -> 0.0)
            double latencyBonus = Math.Clamp(1.0 - (Math.Max(0, avgLatency - 50) / 1450.0), 0.0, 1.0);

            int totalSteps = individual.Genes.Values.Sum(g => g.DesyncSteps.Count);
            double efficiencyBonus = Math.Max(0.0, 1.0 - (totalSteps / 25.0));

            double score = (0.55 * passRatio) 
                         + (0.20 * speedBonus) 
                         + (0.10 * voiceBonus) 
                         + (0.10 * latencyBonus) 
                         + (0.05 * efficiencyBonus);

            individual.StreamingSpeedKBps = maxSpeed;

            return new FitnessResult
            {
                Score = Math.Round(Math.Clamp(score, 0.0, 1.0), 3),
                Passed = passed,
                Total = total,
                AvgLatencyMs = avgLatency,
                StreamingSpeedKBps = maxSpeed,
                DiscordVoicePassed = voicePassed,
                HostResults = outcomes
            };
        }
        catch (Exception ex)
        {
            var fallbackOutcomes = new Dictionary<string, ProbeOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in settings.TargetHosts)
            {
                fallbackOutcomes[h] = new ProbeOutcome
                {
                    Success = false,
                    LatencyMs = 0,
                    Detail = RecipeTrial.Short(ex)
                };
            }

            return new FitnessResult
            {
                Score = 0.0,
                Passed = 0,
                Total = settings.TargetHosts.Count,
                AvgLatencyMs = 0,
                StreamingSpeedKBps = 0.0,
                DiscordVoicePassed = false,
                HostResults = fallbackOutcomes
            };
        }
        finally
        {
            WinwsProcessLock.Release();
        }
    }

    /// <summary>
    /// Комплексная проверка работы стратегий в связке (Cross-Service Synergy):
    /// проверяет отсутствие взаимных коллизий между сервисами при одновременной работе пресета.
    /// </summary>
    public async Task<CrossServiceSynergyResult> VerifySynergyAsync(
        PresetIndividual individual,
        ZapretPreset templatePreset,
        EvolutionSettings settings,
        CancellationToken ct)
    {
        var preset = individual.ToPreset(templatePreset);
        var arguments = new List<string>(WinwsCommandLine.Build(preset));
        if (!arguments.Any(a => a.StartsWith("--wf-priority", StringComparison.OrdinalIgnoreCase)))
            arguments.Insert(0, "--wf-priority=1000");
        if (!arguments.Any(a => a.StartsWith("--wf-dup-check", StringComparison.OrdinalIgnoreCase)))
            arguments.Insert(0, "--wf-dup-check=0");

        var statuses = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int passed = 0;
        var hosts = settings.TargetHosts;

        if (!File.Exists(_winwsPath))
        {
            return new CrossServiceSynergyResult
            {
                IsAllPassed = false,
                PassedCount = 0,
                TotalCount = hosts.Count,
                SynergyScore = 0.0,
                Verdict = "Верификация в связке невозможна: бинарник winws не найден"
            };
        }

        await WinwsProcessLock.WaitAsync(ct);
        try
        {
            string? launchError = await RecipeTrial.WithWinwsAsync(
                _winwsPath,
                _zapretRoot,
                arguments,
                async () =>
                {
                    // Проверяем устойчивость связки параллельно по всем хостам: выполняем 2 раунда сетевых запросов
                    var hostTasks = hosts.Select(async host =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        int successRounds = 0;
                        long totalLat = 0;

                        for (int round = 0; round < 2; round++)
                        {
                            var sw = Stopwatch.StartNew();
                            try
                            {
                                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                cts.CancelAfter(TimeSpan.FromSeconds(settings.ProbeTimeoutSeconds));

                                var addr = await RecipeTrial.AddressForAsync(host, cts.Token);
                                if (addr is null)
                                    continue;

                                var flow = await RecipeTrial.OpensAsync(host, addr, cts.Token);
                                sw.Stop();

                                if (flow.Passed && !flow.Stub)
                                {
                                    successRounds++;
                                    totalLat += sw.ElapsedMilliseconds;
                                }
                            }
                            catch
                            {
                                // Игнорируем единичный сбой раунда
                            }
                        }

                        if (successRounds > 0)
                        {
                            Interlocked.Increment(ref passed);
                            long avg = totalLat / successRounds;
                            statuses[host] = $"Стабильно ({avg} мс, {successRounds}/2)";
                        }
                        else
                        {
                            statuses[host] = "Блокируется DPI / Таймаут";
                        }
                    });

                    await Task.WhenAll(hostTasks);
                },
                ct);

            if (launchError != null)
            {
                return new CrossServiceSynergyResult
                {
                    IsAllPassed = false,
                    PassedCount = 0,
                    TotalCount = hosts.Count,
                    SynergyScore = 0.0,
                    Verdict = $"Ошибка запуска движка: {launchError}"
                };
            }
        }
        catch (Exception ex)
        {
            return new CrossServiceSynergyResult
            {
                IsAllPassed = false,
                PassedCount = 0,
                TotalCount = hosts.Count,
                SynergyScore = 0.0,
                Verdict = $"Сбой верификации: {RecipeTrial.Short(ex)}"
            };
        }
        finally
        {
            WinwsProcessLock.Release();
        }

        double synergyRatio = hosts.Count > 0 ? (double)passed / hosts.Count : 0.0;
        string verdict = synergyRatio >= 0.95
            ? "Отличная синергия связки: все ключевые сервисы работают стабильно и бесконфликтно."
            : synergyRatio >= 0.65
                ? $"Частичная синергия: {passed}/{hosts.Count} сервисов работают стабильно."
                : $"Слабая связка ({passed}/{hosts.Count}): рекомендуется продолжить эволюцию.";

        return new CrossServiceSynergyResult
        {
            IsAllPassed = passed == hosts.Count,
            PassedCount = passed,
            TotalCount = hosts.Count,
            SynergyScore = Math.Round(synergyRatio, 2),
            HostStatuses = statuses,
            Verdict = verdict
        };
    }
}
