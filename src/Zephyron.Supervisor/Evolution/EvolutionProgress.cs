namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Данные о текущем прогрессе поколения эволюции для UI и отчётов.
/// </summary>
public sealed record EvolutionProgress
{
    public int Generation { get; init; }
    public int MaxGenerations { get; init; }
    public double BestFitness { get; init; }
    public int BestPassed { get; init; }
    public int BestTotal { get; init; }
    public long BestLatencyMs { get; init; }
    public string BestPresetName { get; init; } = "";
    public IReadOnlyDictionary<string, bool> BestHostResults { get; init; } = new Dictionary<string, bool>();
    public string Phase { get; init; } = "";
    public bool IsComplete { get; init; }
    public string? WinnerExportPath { get; init; }
    public string? SynergyReport { get; init; }
    public DpiProfile? DetectedDpiProfile { get; init; }
    public IReadOnlyList<PresetDiffEntry>? DiffEntries { get; init; }
    public Zapret.Evolution.PresetIndividual? WinnerIndividual { get; init; }
    public string? LiveOutcomeDetail { get; init; }
}
