using System.Text;
using System.Text.RegularExpressions;
using Zephyron.Zapret;
using Zephyron.Zapret.Evolution;

namespace Zephyron.Supervisor.Evolution;

/// <summary>
/// Экспортирует победившую особь в файл пресета (.txt), готовый к использованию в Zapret.
/// </summary>
public sealed class PresetExporter
{
    /// <summary>
    /// Экспортирует особь в текстовый пресет формата Zapret 2.
    /// Поддерживает преемственность поколений при повторной эволюции уже эволюционировавшего пресета.
    /// </summary>
    /// <returns>Абсолютный путь к сохраненному файлу пресета.</returns>
    public string Export(
        PresetIndividual winner,
        ZapretPreset template,
        string outputDirectory,
        PresetQualityCertificate? certificate = null,
        IspProfile? isp = null,
        DpiProfile? dpi = null)
    {
        if (!Directory.Exists(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        // Если шаблон уже был эволюционировавшим, аккуратно суммируем поколения
        string baseName = template.Name;
        int totalGen = winner.GenerationNumber;

        var match = Regex.Match(baseName, @"^Evolved\s+(.+?)(?:\s*\(Gen\s*(\d+)\))?$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            baseName = match.Groups[1].Value.Trim();
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int prevGen))
            {
                totalGen += prevGen;
            }
        }
        else
        {
            // Очищаем от возможного хвоста " (evolved)"
            baseName = Regex.Replace(baseName, @"\s*\(evolved\)", "", RegexOptions.IgnoreCase).Trim();
        }

        var safeBase = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
        var presetDisplayName = $"Evolved {baseName} (Gen {totalGen})";
        var fileName = $"Evolved {safeBase} (Gen {totalGen}).txt";
        var fullPath = Path.Combine(outputDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"# Preset: {presetDisplayName}");
        sb.AppendLine($"# BuiltinVersion: evolved-gen-{totalGen}");
        sb.AppendLine($"# EvolvedAt: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"# FitnessScore: {winner.FitnessScore:P0}");
        if (certificate is not null)
        {
            sb.AppendLine($"# QualityScore: {certificate.QualityScore}%");
            sb.AppendLine($"# QualityVerdict: {certificate.Verdict}");
            if (certificate.StreamingSpeedKBps > 0)
                sb.AppendLine($"# StreamingSpeed: {certificate.StreamingSpeedKBps:F0} KB/s");
            if (certificate.QualityBadges.Count > 0)
                sb.AppendLine($"# QualityBadges: {string.Join(", ", certificate.QualityBadges)}");
        }
        if (isp is not null && !string.IsNullOrEmpty(isp.IspName))
            sb.AppendLine($"# TargetISP: {isp.DisplaySummary}");
        if (dpi is not null && !string.IsNullOrEmpty(dpi.IspFilterType))
            sb.AppendLine($"# DpiFingerprint: {dpi.IspFilterType}");
        sb.AppendLine($"# IconColor: #4CAF50");
        sb.AppendLine($"# Description: Адаптированный пресет на основе {baseName}. Поколение {totalGen}. Успешность проб: {winner.ProbesPassed}/{winner.ProbesTotal}, средняя задержка: {winner.AvgLatencyMs} мс.");
        sb.AppendLine();

        // 1. Глобальные аргументы
        foreach (var arg in template.GlobalArguments)
            sb.AppendLine(arg);

        sb.AppendLine();

        // 2. Секции
        bool firstSection = true;
        foreach (var origSection in template.Sections)
        {
            if (!firstSection)
            {
                sb.AppendLine();
                sb.AppendLine("--new");
                sb.AppendLine();
            }
            firstSection = false;

            if (origSection.IsPassThrough || !winner.Genes.TryGetValue(origSection.Name, out var gene))
            {
                // Сохраняем исходную секцию, если она не подвергалась мутации (например passthrough)
                foreach (var arg in origSection.RawArguments)
                    sb.AppendLine(arg);
                continue;
            }

            var sectionArgs = PresetIndividual.RebuildSectionArguments(origSection, gene);
            foreach (var arg in sectionArgs)
                sb.AppendLine(arg);
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
        return fullPath;
    }
}
