using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

public enum DiffChangeKind
{
    Unchanged,
    Optimized,
    Pruned,
    FineTuned
}

/// <summary>
/// Запись сравнения отдельной секции пресета («Было / Стало»).
/// </summary>
public sealed record PresetDiffEntry
{
    public string SectionName { get; init; } = "";
    public string OldSteps { get; init; } = "";
    public string NewSteps { get; init; } = "";
    public DiffChangeKind Kind { get; init; } = DiffChangeKind.Unchanged;
    public string Explanation { get; init; } = "";
    public string StatusBadge { get; init; } = "";
    public string BadgeColorHex { get; init; } = "#888888";
}

/// <summary>
/// Анализирует и строит понятный для человека отчёт об оптимизациях пресета («Было / Стало»).
/// </summary>
public static class PresetDiffInspector
{
    public static IReadOnlyList<PresetDiffEntry> Inspect(ZapretPreset basePreset, PresetIndividual winner)
    {
        var entries = new List<PresetDiffEntry>();

        foreach (var section in basePreset.Sections)
        {
            if (section.IsPassThrough)
                continue;

            var oldSteps = string.Join("; ", section.DesyncRecipes);
            if (string.IsNullOrWhiteSpace(oldSteps))
            {
                oldSteps = string.Join(" ", section.RawArguments.Where(a => a.StartsWith("--dpi-desync")));
            }

            string newSteps = oldSteps;
            if (winner.Genes.TryGetValue(section.Name, out var gene))
            {
                newSteps = string.Join("; ", gene.DesyncSteps);
            }

            if (string.Equals(oldSteps, newSteps, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new PresetDiffEntry
                {
                    SectionName = section.Name,
                    OldSteps = oldSteps,
                    NewSteps = newSteps,
                    Kind = DiffChangeKind.Unchanged,
                    Explanation = "Защищено Golden Seed: рабочая стратегия сохранена без изменений.",
                    StatusBadge = "Сохранено",
                    BadgeColorHex = "#9E9E9E"
                });
            }
            else
            {
                var (kind, explanation, badge, color) = AnalyzeDiff(section.Name, oldSteps, newSteps);
                entries.Add(new PresetDiffEntry
                {
                    SectionName = section.Name,
                    OldSteps = oldSteps,
                    NewSteps = newSteps,
                    Kind = kind,
                    Explanation = explanation,
                    StatusBadge = badge,
                    BadgeColorHex = color
                });
            }
        }

        return entries;
    }

    private static (DiffChangeKind Kind, string Explanation, string Badge, string Color) AnalyzeDiff(
        string sectionName, string oldSteps, string newSteps)
    {
        // Проверяем геномный прунинг
        if (oldSteps.Contains(":tcp_md5", StringComparison.OrdinalIgnoreCase) &&
            !newSteps.Contains(":tcp_md5", StringComparison.OrdinalIgnoreCase))
        {
            return (DiffChangeKind.Pruned, "Genomic Pruning: удалён устаревший флаг :tcp_md5, снижена нагрузка на WinDivert.", "Очищено", "#2196F3");
        }

        // Проверяем смещение SNI
        if (newSteps.Contains("sniext+1", StringComparison.OrdinalIgnoreCase) &&
            !oldSteps.Contains("sniext+1", StringComparison.OrdinalIgnoreCase))
        {
            return (DiffChangeKind.Optimized, "Смещение сплита оптимизировано на pos=sniext+1 под сигнатуру фильтрации ТСПУ.", "Оптимизировано", "#4CAF50");
        }

        // Проверяем seqovl
        if (newSteps.Contains("seqovl=", StringComparison.OrdinalIgnoreCase) &&
            !oldSteps.Contains("seqovl=", StringComparison.OrdinalIgnoreCase))
        {
            return (DiffChangeKind.Optimized, "Внедрено перекрытие пакетов seqovl для обхода глубокой инспекции буфера ТСПУ.", "Оптимизировано", "#4CAF50");
        }

        // Проверяем fake payload
        if (newSteps.Contains("blob=", StringComparison.OrdinalIgnoreCase) &&
            !oldSteps.Contains("blob=", StringComparison.OrdinalIgnoreCase))
        {
            return (DiffChangeKind.Optimized, "Обновлен профиль фальшивого пакета (TLS blob) для целевого сервиса.", "Оптимизировано", "#4CAF50");
        }

        // Проверяем укорочение шагов
        if (newSteps.Length < oldSteps.Length && oldSteps.Split(';').Length > newSteps.Split(';').Length)
        {
            return (DiffChangeKind.Pruned, "Genomic Pruning: удалены избыточные дублирующие шаги без потери проходимости.", "Редукция", "#2196F3");
        }

        // Иначе мететическая тонкая настройка
        return (DiffChangeKind.FineTuned, "Меметическая доводка: калибровка дельта-задержек и смещений сплита.", "Калибровка", "#FF9800");
    }
}
