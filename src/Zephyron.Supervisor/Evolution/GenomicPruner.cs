using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Выполняет геномную редукцию (pruning) — удаление избыточных мутаций,
/// дублирующихся шагов и мусора перед экспортом пресета.
/// </summary>
public static class GenomicPruner
{
    /// <summary>
    /// Очищает и минифицирует геном победителя, гарантируя компактность пресета.
    /// </summary>
    public static PresetIndividual Prune(PresetIndividual individual)
    {
        var cleanedGenes = new Dictionary<string, PresetGene>(individual.Genes.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (sectionName, gene) in individual.Genes)
        {
            var uniqueSteps = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in gene.DesyncSteps)
            {
                var trimmed = step.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                // Удаляем опасные опции tcp_md5
                trimmed = GenePool.SanitizeStep(trimmed);

                if (seen.Add(trimmed))
                {
                    uniqueSteps.Add(trimmed);
                }
            }

            if (uniqueSteps.Count == 0)
            {
                uniqueSteps.Add("pass");
            }

            cleanedGenes[sectionName] = gene with
            {
                DesyncSteps = uniqueSteps
            };
        }

        return new PresetIndividual
        {
            Id = individual.Id,
            GenerationNumber = individual.GenerationNumber,
            Genes = cleanedGenes,
            FitnessScore = individual.FitnessScore,
            ProbesPassed = individual.ProbesPassed,
            ProbesTotal = individual.ProbesTotal,
            AvgLatencyMs = individual.AvgLatencyMs,
            LastHostOutcomes = individual.LastHostOutcomes,
            ParentA = individual.ParentA,
            ParentB = individual.ParentB
        };
    }
}
