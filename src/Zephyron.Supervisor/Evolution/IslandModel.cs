using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Островная модель (Island Model) эволюции с несколькими параллельными субпопуляциями.
/// </summary>
public sealed class IslandModel
{
    /// <summary>
    /// Запускает эволюцию на нескольких изолированных субпопуляциях с периодической миграцией элиты.
    /// </summary>
    public async Task<PresetIndividual> RunAsync(
        IReadOnlyList<ZapretPreset> templatePresets,
        string zapretRoot,
        EvolutionSettings settings,
        GenePool pool,
        FitnessProbe probe,
        IProgress<EvolutionProgress> progress,
        CancellationToken ct)
    {
        var rng = new Random();
        int islandsCount = Math.Max(2, templatePresets.Count);
        int subPopSize = Math.Max(4, settings.PopulationSize / islandsCount);

        // Инициализируем острова
        var islands = new List<List<PresetIndividual>>();
        for (int i = 0; i < islandsCount; i++)
        {
            var basePreset = templatePresets[i % templatePresets.Count];
            var islandPop = new List<PresetIndividual>
            {
                PresetIndividual.FromPreset(basePreset, 0)
            };

            while (islandPop.Count < subPopSize)
            {
                var mutated = GeneticOperators.Mutate(islandPop[0], pool, 0, rng, 0.30);
                islandPop.Add(mutated);
            }

            islands.Add(islandPop);
        }

        PresetIndividual overallBest = islands[0][0];

        for (int gen = 0; gen < settings.MaxGenerations; gen++)
        {
            ct.ThrowIfCancellationRequested();

            // Оценка всех островов
            for (int i = 0; i < islands.Count; i++)
            {
                var template = templatePresets[i % templatePresets.Count];
                foreach (var ind in islands[i])
                {
                    if (!ind.HasValidFitness)
                    {
                        var res = await probe.EvaluateAsync(ind, template, settings, ct);
                        ind.FitnessScore = res.Score;
                        ind.ProbesPassed = res.Passed;
                        ind.ProbesTotal = res.Total;
                        ind.AvgLatencyMs = res.AvgLatencyMs;
                        ind.LastHostOutcomes = res.HostResults.ToDictionary(k => k.Key, v => v.Value.Success);
                    }
                }
            }

            // Находим лучшую особь среди всех островов с приоритетом проходимости и минимального пинга
            var currentBest = islands.SelectMany(x => x).Where(x => x.HasValidFitness)
                .OrderByDescending(x => x.ProbesPassed)
                .ThenBy(x => x.AvgLatencyMs)
                .ThenByDescending(x => x.FitnessScore)
                .FirstOrDefault();

            if (currentBest is not null)
            {
                if (!overallBest.HasValidFitness)
                {
                    overallBest = currentBest;
                }
                else
                {
                    bool isBetter = currentBest.ProbesPassed > overallBest.ProbesPassed
                        || (currentBest.ProbesPassed == overallBest.ProbesPassed && currentBest.AvgLatencyMs < overallBest.AvgLatencyMs)
                        || (currentBest.ProbesPassed == overallBest.ProbesPassed && currentBest.AvgLatencyMs == overallBest.AvgLatencyMs && currentBest.FitnessScore > overallBest.FitnessScore);

                    if (isBetter)
                        overallBest = currentBest;
                }
            }

            progress.Report(new EvolutionProgress
            {
                Generation = gen + 1,
                MaxGenerations = settings.MaxGenerations,
                BestFitness = overallBest.FitnessScore,
                BestPassed = overallBest.ProbesPassed,
                BestTotal = overallBest.ProbesTotal,
                BestLatencyMs = overallBest.AvgLatencyMs,
                BestHostResults = overallBest.LastHostOutcomes,
                BestPresetName = templatePresets[0].Name,
                Phase = $"Островная модель: поколение {gen + 1}/{settings.MaxGenerations}"
            });

            if (overallBest.FitnessScore >= settings.ConvergeThreshold)
                break;

            // Миграция между островами каждые settings.IslandMigrationInterval поколений
            if ((gen + 1) % settings.IslandMigrationInterval == 0)
            {
                for (int i = 0; i < islands.Count; i++)
                {
                    int nextIsland = (i + 1) % islands.Count;
                    var emigrant = GeneticOperators.Elite(islands[i], 1).FirstOrDefault();
                    if (emigrant != null)
                    {
                        // Безопасно находим худшую особь на следующем острове, учитывая NaN
                        int worstIdx = -1;
                        double worstScore = double.PositiveInfinity;
                        for (int w = 0; w < islands[nextIsland].Count; w++)
                        {
                            var cand = islands[nextIsland][w];
                            double sc = cand.HasValidFitness ? cand.FitnessScore : -1.0;
                            if (sc < worstScore)
                            {
                                worstScore = sc;
                                worstIdx = w;
                            }
                        }
                        if (worstIdx >= 0)
                            islands[nextIsland][worstIdx] = emigrant;
                    }
                }
            }

            // Смена поколений на каждом острове
            for (int i = 0; i < islands.Count; i++)
            {
                var nextGen = new List<PresetIndividual>(GeneticOperators.Elite(islands[i], 1));
                while (nextGen.Count < subPopSize)
                {
                    var p1 = GeneticOperators.TournamentSelect(islands[i], rng, settings.TournamentSize);
                    var p2 = GeneticOperators.TournamentSelect(islands[i], rng, settings.TournamentSize);
                    var (c1, c2) = GeneticOperators.Crossover(p1, p2, gen + 1, rng, settings.CrossoverRate);

                    nextGen.Add(GeneticOperators.Mutate(c1, pool, gen + 1, rng, settings.MutationRate));
                    if (nextGen.Count < subPopSize)
                        nextGen.Add(GeneticOperators.Mutate(c2, pool, gen + 1, rng, settings.MutationRate));
                }
                islands[i] = nextGen;
            }
        }

        return overallBest;
    }
}
