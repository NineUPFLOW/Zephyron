using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Главный координатор и исполнительный движок генетической эволюции пресетов.
/// </summary>
public sealed class EvolutionEngine
{
    private readonly FitnessProbe _probe;
    private readonly EvolutionStore _store;
    private readonly PresetExporter _exporter;
    private readonly IslandModel _islandModel;

    public EvolutionEngine(FitnessProbe probe, EvolutionStore store, PresetExporter exporter)
    {
        _probe = probe;
        _store = store;
        _exporter = exporter;
        _islandModel = new IslandModel();
    }

    /// <summary>
    /// Запускает эволюцию из одного базового пресета.
    /// </summary>
    public Task<(PresetIndividual Winner, string ExportPath)> RunAsync(
        ZapretPreset basePreset,
        string zapretRoot,
        EvolutionSettings settings,
        string outputDirectory,
        IProgress<EvolutionProgress> progress,
        CancellationToken ct) =>
        RunAsync([basePreset], zapretRoot, settings, outputDirectory, progress, ct);

    /// <summary>
    /// Запускает генетическую эволюцию с пулом родительских пресетов, отобранных пользователем.
    /// </summary>
    public async Task<(PresetIndividual Winner, string ExportPath)> RunAsync(
        IReadOnlyList<ZapretPreset> seedPresets,
        string zapretRoot,
        EvolutionSettings settings,
        string outputDirectory,
        IProgress<EvolutionProgress> progress,
        CancellationToken ct)
    {
        if (seedPresets.Count == 0)
            throw new ArgumentException("Для эволюции должен быть выбран хотя бы один родительский пресет.", nameof(seedPresets));

        var primaryPreset = seedPresets[0];
        var pool = GenePool.FromPresets(seedPresets);
        var rng = new Random();

        if (settings.IslandModelEnabled)
        {
            var winnerInd = await _islandModel.RunAsync(seedPresets, zapretRoot, settings, pool, _probe, progress, ct);
            return await FinalizeWinnerAsync(winnerInd, primaryPreset, settings, pool, outputDirectory, progress, rng, ct);
        }

        // Экспресс-определение сигнатуры DPI ТСПУ (DPI Fingerprinting)
        var sampleResults = new Dictionary<string, bool>();
        if (primaryPreset.Sections.Any(s => s.Name.Contains("youtube", StringComparison.OrdinalIgnoreCase)))
            sampleResults["www.youtube.com"] = false;
        if (primaryPreset.Sections.Any(s => s.Name.Contains("discord", StringComparison.OrdinalIgnoreCase)))
            sampleResults["discord.com"] = false;

        var dpiProfile = DpiFingerprinter.DetectProfile(sampleResults);
        int targetMaxGen = settings.FastSprintMode ? Math.Min(settings.MaxGenerations, 4) : settings.MaxGenerations;

        progress.Report(new EvolutionProgress
        {
            Generation = 0,
            MaxGenerations = targetMaxGen,
            Phase = $"DPI Fingerprint: {dpiProfile.IspFilterType}. Засевание генофонда...",
            DetectedDpiProfile = dpiProfile
        });

        // Поколение 0: Засевание из ВСЕХ отобранных пресетов-родителей
        var population = new List<PresetIndividual>();
        foreach (var p in seedPresets)
        {
            population.Add(PresetIndividual.FromPreset(p, 0));
        }

        // Засевание популяции (Transfer learning из журнала успешных рецептов)
        SeedFromJournal(population, primaryPreset);

        // Дополняем популяцию до PopulationSize через кроссовер и мутации отобранных родителей
        int parentIdx = 0;
        int targetPopSize = settings.FastSprintMode ? Math.Min(settings.PopulationSize, 10) : settings.PopulationSize;
        while (population.Count < targetPopSize)
        {
            var parent = population[parentIdx % seedPresets.Count];
            var mutated = GeneticOperators.Mutate(parent, pool, 0, rng, 0.25, settings.TargetSectionKey);
            population.Add(mutated);
            parentIdx++;
        }

        return await RunGenerationsLoopAsync(population, 0, primaryPreset, zapretRoot, settings, pool, outputDirectory, progress, rng, ct);
    }

    /// <summary>
    /// Возобновляет эволюцию из сохраненного состояния популяции.
    /// </summary>
    public async Task<(PresetIndividual Winner, string ExportPath)> ResumeAsync(
        ZapretPreset basePreset,
        string zapretRoot,
        EvolutionSettings settings,
        string outputDirectory,
        IProgress<EvolutionProgress> progress,
        CancellationToken ct)
    {
        var saved = await _store.LoadAsync(ct: ct);
        if (saved is null || saved.Population.Count == 0)
        {
            return await RunAsync(basePreset, zapretRoot, settings, outputDirectory, progress, ct);
        }

        var pool = GenePool.FromPresets([basePreset]);
        var rng = new Random();

        return await RunGenerationsLoopAsync(
            saved.Population,
            saved.Generation,
            basePreset,
            zapretRoot,
            settings,
            pool,
            outputDirectory,
            progress,
            rng,
            ct);
    }

    private async Task<(PresetIndividual Winner, string ExportPath)> RunGenerationsLoopAsync(
        List<PresetIndividual> population,
        int startGeneration,
        ZapretPreset basePreset,
        string zapretRoot,
        EvolutionSettings settings,
        GenePool pool,
        string outputDirectory,
        IProgress<EvolutionProgress> progress,
        Random rng,
        CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentProbes));
        PresetIndividual bestOverall = population[0];

        int maxGen = settings.MaxGenerations;
        for (int gen = startGeneration; gen < maxGen; gen++)
        {
            ct.ThrowIfCancellationRequested();

            progress.Report(new EvolutionProgress
            {
                Generation = gen + 1,
                MaxGenerations = maxGen,
                BestFitness = bestOverall.HasValidFitness ? bestOverall.FitnessScore : 0.0,
                Phase = $"Поколение {gen + 1}/{maxGen}: Оценка фитнеса особей...",
            });

            int evaluatedCount = 0;
            // Параллельная оценка особей текущего поколения через семафор
            var tasks = population.Select(async ind =>
            {
                if (ind.HasValidFitness)
                    return;

                await semaphore.WaitAsync(ct);
                try
                {
                    var result = await _probe.EvaluateAsync(ind, basePreset, settings, ct);
                    ind.FitnessScore = result.Score;
                    ind.ProbesPassed = result.Passed;
                    ind.ProbesTotal = result.Total;
                    ind.AvgLatencyMs = result.AvgLatencyMs;
                    ind.LastHostOutcomes = result.HostResults.ToDictionary(k => k.Key, v => v.Value.Success);

                    int count = Interlocked.Increment(ref evaluatedCount);
                    var passSummary = string.Join(", ", result.HostResults.Select(h => $"{h.Key}: {(h.Value.Success ? "✔" : "✖")}"));

                    progress.Report(new EvolutionProgress
                    {
                        Generation = gen + 1,
                        MaxGenerations = maxGen,
                        BestFitness = bestOverall.HasValidFitness ? Math.Max(bestOverall.FitnessScore, ind.FitnessScore) : ind.FitnessScore,
                        BestPassed = bestOverall.HasValidFitness ? Math.Max(bestOverall.ProbesPassed, ind.ProbesPassed) : ind.ProbesPassed,
                        BestTotal = result.Total,
                        BestLatencyMs = bestOverall.HasValidFitness ? bestOverall.AvgLatencyMs : ind.AvgLatencyMs,
                        BestHostResults = ind.LastHostOutcomes,
                        Phase = $"Поколение {gen + 1}/{maxGen}: особь {count}/{population.Count} (пробито {result.Passed}/{result.Total} хостов, {ind.AvgLatencyMs}мс)",
                        LiveOutcomeDetail = $"[Пок. {gen + 1} | Особь {count}/{population.Count}] Пробито {result.Passed}/{result.Total} хостов ({passSummary}), пинг {result.AvgLatencyMs}мс, фитнес {result.Score:P0}"
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            var genCandidates = population.Where(i => i.HasValidFitness).ToList();
            var genBest = genCandidates
                .OrderByDescending(i => i.ProbesPassed)
                .ThenBy(i => i.AvgLatencyMs)
                .ThenByDescending(i => i.FitnessScore)
                .FirstOrDefault();

            if (genBest is not null)
            {
                if (!bestOverall.HasValidFitness)
                {
                    bestOverall = genBest;
                }
                else
                {
                    // Гарантия против деградации: особь принимается, только если она лучше по проходимости или пингу
                    bool isBetter = genBest.ProbesPassed > bestOverall.ProbesPassed
                        || (genBest.ProbesPassed == bestOverall.ProbesPassed && genBest.AvgLatencyMs < bestOverall.AvgLatencyMs)
                        || (genBest.ProbesPassed == bestOverall.ProbesPassed && genBest.AvgLatencyMs == bestOverall.AvgLatencyMs && genBest.FitnessScore > bestOverall.FitnessScore);

                    if (isBetter)
                    {
                        bestOverall = genBest;
                    }
                }
            }

            // Промежуточный отчёт
            progress.Report(new EvolutionProgress
            {
                Generation = gen + 1,
                MaxGenerations = maxGen,
                BestFitness = bestOverall.FitnessScore,
                BestPassed = bestOverall.ProbesPassed,
                BestTotal = bestOverall.ProbesTotal,
                BestLatencyMs = bestOverall.AvgLatencyMs,
                BestHostResults = bestOverall.LastHostOutcomes,
                BestPresetName = basePreset.Name,
                Phase = $"Поколение {gen + 1}/{maxGen}: Селекция и эволюционный отбор",
            });

            // Сохраняем состояние на диск
            await _store.SaveAsync(population, gen + 1, basePreset.Name, ct: ct);

            // Критерий останова: достижение целевой приспособленности
            if (bestOverall.FitnessScore >= settings.ConvergeThreshold)
                break;

            // Формирование следующего поколения:
            // bestOverall ВСЕГДА гарантированно сохраняется в элите нового поколения (Golden Seed)
            var nextGeneration = new List<PresetIndividual> { bestOverall };
            var remainingElite = GeneticOperators.Elite(population, Math.Max(1, settings.EliteCount - 1));
            foreach (var el in remainingElite)
            {
                if (nextGeneration.Count < settings.EliteCount && el.Id != bestOverall.Id)
                    nextGeneration.Add(el);
            }

            // Адаптивная вероятность мутации в случае стагнации
            double currentMutationRate = GeneticOperators.AdaptiveMutationRate(population, settings.MutationRate);

            while (nextGeneration.Count < settings.PopulationSize)
            {
                var p1 = GeneticOperators.TournamentSelect(population, rng, settings.TournamentSize);
                var p2 = GeneticOperators.TournamentSelect(population, rng, settings.TournamentSize);

                var (child1, child2) = GeneticOperators.Crossover(p1, p2, gen + 1, rng, settings.CrossoverRate, settings.TargetSectionKey);

                nextGeneration.Add(GeneticOperators.Mutate(child1, pool, gen + 1, rng, currentMutationRate, settings.TargetSectionKey));

                if (nextGeneration.Count < settings.PopulationSize)
                {
                    nextGeneration.Add(GeneticOperators.Mutate(child2, pool, gen + 1, rng, currentMutationRate, settings.TargetSectionKey));
                }
            }

            population = nextGeneration;
        }

        return await FinalizeWinnerAsync(bestOverall, basePreset, settings, pool, outputDirectory, progress, rng, ct);
    }

    private async Task<(PresetIndividual Winner, string ExportPath)> FinalizeWinnerAsync(
        PresetIndividual bestOverall,
        ZapretPreset basePreset,
        EvolutionSettings settings,
        GenePool pool,
        string outputDirectory,
        IProgress<EvolutionProgress> progress,
        Random rng,
        CancellationToken ct)
    {
        var winner = bestOverall;

        // 1. Мететический локальный поиск (Hill-Climbing fine-tuning)
        if (settings.EnableMemeticFineTuning)
        {
            progress.Report(new EvolutionProgress
            {
                Generation = settings.MaxGenerations,
                MaxGenerations = settings.MaxGenerations,
                BestFitness = winner.FitnessScore,
                BestPassed = winner.ProbesPassed,
                BestTotal = winner.ProbesTotal,
                BestLatencyMs = winner.AvgLatencyMs,
                BestHostResults = winner.LastHostOutcomes,
                BestPresetName = basePreset.Name,
                Phase = "Мететическая доводка: локальный поиск оптимальных параметров..."
            });

            var variations = GeneticOperators.GenerateLocalVariations(winner, pool, rng, count: 3);
            foreach (var candidate in variations)
            {
                ct.ThrowIfCancellationRequested();
                var res = await _probe.EvaluateAsync(candidate, basePreset, settings, ct);
                candidate.FitnessScore = res.Score;
                candidate.ProbesPassed = res.Passed;
                candidate.ProbesTotal = res.Total;
                candidate.AvgLatencyMs = res.AvgLatencyMs;
                candidate.LastHostOutcomes = res.HostResults.ToDictionary(k => k.Key, v => v.Value.Success);

                // Максимизируем проходимость и минимизируем задержку (пинг)
                bool isBetter = candidate.ProbesPassed > winner.ProbesPassed
                    || (candidate.ProbesPassed == winner.ProbesPassed && candidate.AvgLatencyMs < winner.AvgLatencyMs && candidate.ProbesPassed > 0)
                    || (candidate.ProbesPassed == winner.ProbesPassed && candidate.AvgLatencyMs == winner.AvgLatencyMs && candidate.FitnessScore > winner.FitnessScore);

                if (isBetter)
                {
                    winner = candidate;
                }
            }
        }

        // 2. Комплексная проверка связки сервисов (Cross-Service Synergy Verification)
        string? synergyReport = null;
        if (settings.EnableSynergyVerification)
        {
            progress.Report(new EvolutionProgress
            {
                Generation = settings.MaxGenerations,
                MaxGenerations = settings.MaxGenerations,
                BestFitness = winner.FitnessScore,
                BestPassed = winner.ProbesPassed,
                BestTotal = winner.ProbesTotal,
                BestLatencyMs = winner.AvgLatencyMs,
                BestHostResults = winner.LastHostOutcomes,
                BestPresetName = basePreset.Name,
                Phase = "Проверка стратегии в связке сервисов (YouTube + Discord + Twitch)..."
            });

            var syn = await _probe.VerifySynergyAsync(winner, basePreset, settings, ct);
            synergyReport = $"{syn.Verdict} (Связка: {syn.PassedCount}/{syn.TotalCount}, надёжность: {syn.SynergyScore:P0})";
        }

        // 3. Геномная редукция (Pruning) — удаление дублирующихся шагов и избыточных мутаций
        winner = GenomicPruner.Prune(winner);

        // 4. Финализирующий аудит и сертификация качества пресета
        progress.Report(new EvolutionProgress
        {
            Generation = settings.MaxGenerations,
            MaxGenerations = settings.MaxGenerations,
            BestFitness = winner.FitnessScore,
            BestPassed = winner.ProbesPassed,
            BestTotal = winner.ProbesTotal,
            BestLatencyMs = winner.AvgLatencyMs,
            BestHostResults = winner.LastHostOutcomes,
            BestPresetName = basePreset.Name,
            Phase = "Сертификация качества: стресс-тест в 3 раундах и проверка winws2 --dry-run..."
        });

        var certifier = new PresetQualityCertifier(_probe, _probe.ZapretRoot, _probe.WinwsPath);
        var certificate = await certifier.CertifyAsync(winner, basePreset, settings, ct);

        // 5. Построение отчёта об изменениях («Было / Стало»)
        var diffEntries = PresetDiffInspector.Inspect(basePreset, winner);

        // 6. Экспортируем финального победителя с сертификатом качества
        var exportPath = _exporter.Export(winner, basePreset, outputDirectory, certificate);

        progress.Report(new EvolutionProgress
        {
            Generation = settings.MaxGenerations,
            MaxGenerations = settings.MaxGenerations,
            BestFitness = winner.FitnessScore,
            BestPassed = winner.ProbesPassed,
            BestTotal = winner.ProbesTotal,
            BestLatencyMs = winner.AvgLatencyMs,
            BestHostResults = winner.LastHostOutcomes,
            BestPresetName = basePreset.Name,
            Phase = "Эволюция завершена!",
            IsComplete = true,
            WinnerExportPath = exportPath,
            SynergyReport = synergyReport,
            DiffEntries = diffEntries,
            WinnerIndividual = winner,
            Certificate = certificate
        });

        return (winner, exportPath);
    }

    private static void SeedFromJournal(List<PresetIndividual> population, ZapretPreset basePreset)
    {
        try
        {
            var journal = AutoPickJournal.Load();
            var successfulRuns = journal.Where(j => j.Entries.Any(e => e.Wrote?.Recipe is not null)).ToList();
            if (successfulRuns.Count == 0)
                return;

            var baseInd = population[0];
            var seededGenes = new Dictionary<string, PresetGene>(baseInd.Genes);

            foreach (var run in successfulRuns.Take(3))
            {
                foreach (var entry in run.Entries)
                {
                    if (entry.Wrote?.Recipe is { Length: > 0 } recipeName)
                    {
                        var recipe = DesyncRecipes.Find(basePreset, recipeName);
                        if (recipe != null)
                        {
                            foreach (var usedBy in recipe.UsedBy)
                            {
                                if (seededGenes.TryGetValue(usedBy, out var currentGene))
                                {
                                    seededGenes[usedBy] = currentGene with
                                    {
                                        DesyncSteps = recipe.Steps
                                    };
                                }
                            }
                        }
                    }
                }
            }

            population.Add(new PresetIndividual
            {
                GenerationNumber = 0,
                Genes = seededGenes
            });
        }
        catch
        {
            // Журнал опционален — сбой чтения не мешает запуску
        }
    }
}
