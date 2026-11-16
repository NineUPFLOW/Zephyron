namespace Zephyron.Zapret.Evolution;

/// <summary>
/// Генетические операторы: селекция, кроссовер, мутация и адаптивные коэффициенты.
/// </summary>
public static class GeneticOperators
{
    public const double DefaultMutationRate = 0.15;
    public const double DefaultCrossoverRate = 0.80;

    /// <summary>
    /// Турнирная селекция: выбирает лучшую особь из случайных участников турнира.
    /// </summary>
    public static PresetIndividual TournamentSelect(
        IReadOnlyList<PresetIndividual> population,
        Random rng,
        int k = 3)
    {
        var valid = population.Where(i => i.HasValidFitness).ToList();
        if (valid.Count == 0)
            return population[rng.Next(population.Count)];

        var tournamentSize = Math.Min(k, valid.Count);
        PresetIndividual best = valid[rng.Next(valid.Count)];

        for (int i = 1; i < tournamentSize; i++)
        {
            var competitor = valid[rng.Next(valid.Count)];
            if (competitor.FitnessScore > best.FitnessScore)
                best = competitor;
        }

        return best;
    }

    /// <summary>
    /// Элитизм: выбор N особей с наивысшим баллом приспособленности.
    /// </summary>
    public static IReadOnlyList<PresetIndividual> Elite(
        IReadOnlyList<PresetIndividual> population,
        int n = 2)
    {
        return population
            .Where(i => i.HasValidFitness)
            .OrderByDescending(i => i.FitnessScore)
            .Take(Math.Max(1, n))
            .ToList();
    }

    /// <summary>
    /// Кроссовер (скрещивание) двух особей с получением двух потомков.
    /// При указании targetSection скрещиваются только гены выбранной секции.
    /// </summary>
    public static (PresetIndividual Child1, PresetIndividual Child2) Crossover(
        PresetIndividual parent1,
        PresetIndividual parent2,
        int generation,
        Random rng,
        double rate = DefaultCrossoverRate,
        string? targetSection = null)
    {
        if (rng.NextDouble() > rate)
        {
            // Без кроссовера — клоны с новым поколением
            return (
                new PresetIndividual
                {
                    GenerationNumber = generation,
                    Genes = new Dictionary<string, PresetGene>(parent1.Genes),
                    ParentA = parent1.Id
                },
                new PresetIndividual
                {
                    GenerationNumber = generation,
                    Genes = new Dictionary<string, PresetGene>(parent2.Genes),
                    ParentA = parent2.Id
                }
            );
        }

        bool isTargeted = !string.IsNullOrWhiteSpace(targetSection) && !targetSection.Equals("all", StringComparison.OrdinalIgnoreCase);

        var keys = parent1.Genes.Keys.Union(parent2.Genes.Keys).ToList();
        var child1Genes = new Dictionary<string, PresetGene>(StringComparer.OrdinalIgnoreCase);
        var child2Genes = new Dictionary<string, PresetGene>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            parent1.Genes.TryGetValue(key, out var g1);
            parent2.Genes.TryGetValue(key, out var g2);

            // Если включена целевая микро-эволюция и секция не относится к целевой — замораживаем
            if (isTargeted && !key.Contains(targetSection!, StringComparison.OrdinalIgnoreCase))
            {
                if (g1 is not null)
                {
                    child1Genes[key] = g1;
                    child2Genes[key] = g1;
                }
                else if (g2 is not null)
                {
                    child1Genes[key] = g2;
                    child2Genes[key] = g2;
                }
                continue;
            }

            if (g1 is not null && g2 is not null)
            {
                child1Genes[key] = PresetGene.Crossover(g1, g2, rng);
                child2Genes[key] = PresetGene.Crossover(g2, g1, rng);
            }
            else if (g1 is not null)
            {
                child1Genes[key] = g1;
                child2Genes[key] = g1;
            }
            else if (g2 is not null)
            {
                child1Genes[key] = g2;
                child2Genes[key] = g2;
            }
        }

        var child1 = new PresetIndividual
        {
            GenerationNumber = generation,
            Genes = child1Genes,
            ParentA = parent1.Id,
            ParentB = parent2.Id,
        };

        var child2 = new PresetIndividual
        {
            GenerationNumber = generation,
            Genes = child2Genes,
            ParentA = parent2.Id,
            ParentB = parent1.Id,
        };

        return (child1, child2);
    }

    /// <summary>
    /// Мутация особи с защитой работающих секций: работающие рецепты сохраняются и лишь калибруются,
    /// а неработающие подвергаются направленной мутации.
    /// При указании targetSection мутирует только указанная секция, остальные замораживаются.
    /// </summary>
    public static PresetIndividual Mutate(
        PresetIndividual individual,
        GenePool pool,
        int generation,
        Random rng,
        double rate = DefaultMutationRate,
        string? targetSection = null)
    {
        var mutatedGenes = new Dictionary<string, PresetGene>(StringComparer.OrdinalIgnoreCase);
        bool isTargeted = !string.IsNullOrWhiteSpace(targetSection) && !targetSection.Equals("all", StringComparison.OrdinalIgnoreCase);

        foreach (var (name, gene) in individual.Genes)
        {
            // Если включена микро-эволюция конкретной секции, все остальные секции полностью замораживаются
            if (isTargeted && !name.Contains(targetSection!, StringComparison.OrdinalIgnoreCase))
            {
                mutatedGenes[name] = gene;
                continue;
            }

            bool working = IsSectionWorking(name, individual.LastHostOutcomes);

            if (working && !isTargeted)
            {
                // Защита от деградации: работающую стратегию не ломаем, лишь слегка калибруем пинг
                mutatedGenes[name] = rng.NextDouble() < 0.15
                    ? gene.FineTune(pool, rng)
                    : gene;
            }
            else
            {
                // Неработающая или целевая секция: активный эволюционный поиск решения
                double effectiveRate = isTargeted ? Math.Max(rate, 0.35) : rate;
                mutatedGenes[name] = rng.NextDouble() < effectiveRate
                    ? gene.Mutate(pool, rng, name)
                    : gene;
            }
        }

        return new PresetIndividual
        {
            GenerationNumber = generation,
            Genes = mutatedGenes,
            ParentA = individual.Id,
        };
    }

    private static bool IsSectionWorking(string sectionName, IReadOnlyDictionary<string, bool> outcomes)
    {
        if (outcomes.Count == 0)
            return false;

        var lower = sectionName.ToLowerInvariant();
        foreach (var (host, success) in outcomes)
        {
            if (!success)
                continue;

            var h = host.ToLowerInvariant();
            if (lower.Contains("youtube") && h.Contains("youtube"))
                return true;
            if (lower.Contains("googlevideo") && h.Contains("googlevideo"))
                return true;
            if (lower.Contains("discord") && h.Contains("discord"))
                return true;
            if (lower.Contains("twitch") && h.Contains("twitch"))
                return true;
            if (lower.Contains("steam") && h.Contains("steam"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Адаптивная мутация: повышает интенсивность мутации при застревании популяции в локальном оптимуме.
    /// </summary>
    public static double AdaptiveMutationRate(
        IReadOnlyList<PresetIndividual> population,
        double baseRate = DefaultMutationRate)
    {
        var scores = population
            .Where(i => i.HasValidFitness)
            .Select(i => i.FitnessScore)
            .ToList();

        if (scores.Count < 2)
            return baseRate;

        double max = scores.Max();
        double min = scores.Min();
        double spread = max - min;

        // Если разброс фитнеса меньше 5%, популяция сошлась/застряла -> увеличиваем мутацию до 35%
        if (spread < 0.05)
            return Math.Min(0.35, baseRate * 2.0);

        return baseRate;
    }

    /// <summary>
    /// Мететическая тонкая настройка: производит микро-калибровку параметров особи (позиции сплита, задержки, out-range).
    /// </summary>
    public static PresetIndividual FineTune(
        PresetIndividual individual,
        GenePool pool,
        Random rng)
    {
        var tunedGenes = new Dictionary<string, PresetGene>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, gene) in individual.Genes)
        {
            // С вероятностью 70% калибруем параметры секции
            tunedGenes[name] = rng.NextDouble() < 0.70
                ? gene.FineTune(pool, rng)
                : gene;
        }

        return new PresetIndividual
        {
            GenerationNumber = individual.GenerationNumber,
            Genes = tunedGenes,
            ParentA = individual.Id
        };
    }

    /// <summary>
    /// Генерирует окрестность локальных вариаций особи для алгоритма локального спуска (Hill-Climbing).
    /// </summary>
    public static IReadOnlyList<PresetIndividual> GenerateLocalVariations(
        PresetIndividual individual,
        GenePool pool,
        Random rng,
        int count = 4)
    {
        var variations = new List<PresetIndividual>();
        for (int i = 0; i < count; i++)
        {
            variations.Add(FineTune(individual, pool, rng));
        }
        return variations;
    }
}
