namespace Zephyron.Core.Updates;

/// <summary>
/// Какие файлы прежней версии обновление должно убрать: были в поставке, а в новой нет.
/// </summary>
/// <remarks>
/// <para>
/// Подмена только добавляет и перезаписывает. Файл, которого в новом выпуске нет, лежал бы
/// вечно: убранный пресет остался бы в списке (<c>PresetFiles</c> читает всю папку), старый
/// lua или blob — под тем же именем, что берёт движок. Пакет пишет опись поставки
/// (<see cref="ManifestName"/>); убирается разность «опись прежней минус опись новой».
/// Тронуть можно только то, что значится в описи: всё, чего в поставке не было (настройки,
/// свои списки и темы, отчёты, runtime), под удаление не попадает по построению.
/// </para>
/// <para>
/// Выпуски до описи (≤1.8.3) её не имеют — для них <see cref="Legacy"/>: только то, что
/// заведомо не наше рабочее (остатки сборки не в один файл рядом с exe, файлы с префиксом
/// <c>zy-</c> в движке). Полная чистка по описи начнётся со следующего обновления.
/// </para>
/// </remarks>
public static class StaleFiles
{
    /// <summary>Имя описи в корне поставки (пути относительно корня, по одному в строке).</summary>
    public const string ManifestName = "shipped-files.txt";

    /// <summary>Нетронутое при любых условиях — даже если опись по ошибке назвала бы файл.</summary>
    private static readonly string[] ProtectedNames =
    [
        "zephyron.json", "subscriptions.json", "window.json", "autopick-journal.json",
    ];

    /// <summary>Остатки сборки не в один файл: после перехода на single-file рядом с exe им делать нечего.</summary>
    private static readonly string[] LegacyRootSuffixes = [".dll", ".pdb", ".deps.json", ".runtimeconfig.json"];

    private static readonly string[] LegacyRootFiles = ["zy.exe", "Zephyron.Gui.exe"];

    private static readonly string[] LegacyRootFolders = ["runtimes", "amd64"];

    /// <summary>Читает опись; нет файла или он нечитаем — <c>null</c> (а не пустая: «пусто» значило бы «в поставке ничего не было»).</summary>
    public static IReadOnlyList<string>? ReadManifest(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(Normalize).Where(l => l.Length > 0).ToList() is { Count: > 0 } lines ? lines : null // пустая опись — сбой сборки, а не «ничего не поставлено»
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Файлы прежней поставки, которых нет в новой, — под удаление.</summary>
    public static IReadOnlyList<string> Decide(IEnumerable<string> oldShipped, IEnumerable<string> newShipped)
    {
        var keep = newShipped.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return oldShipped.Select(Normalize)
            .Where(f => f.Length > 0 && !keep.Contains(f) && IsRemovable(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Для установки без описи: из лежащих файлов (пути относительно корня) — только заведомо чужие новой сборке.
    /// </summary>
    public static IReadOnlyList<string> Legacy(IEnumerable<string> existing, IEnumerable<string> newShipped)
    {
        var keep = newShipped.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        char sep = Path.DirectorySeparatorChar;

        bool Stale(string f)
        {
            var name = Path.GetFileName(f);
            var folder = Path.GetDirectoryName(f) ?? "";

            if (folder.Length == 0)
            {
                // msquic.dll кладёт рядом сам pack — он и есть «нужный DLL рядом».
                return LegacyRootFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || (!name.Equals("msquic.dll", StringComparison.OrdinalIgnoreCase)
                        && LegacyRootSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
            }

            if (LegacyRootFolders.Contains(f.Split(sep)[0], StringComparer.OrdinalIgnoreCase))
                return true;

            // Свои файлы в движке носят zy-: чужого пользователь туда не кладёт.
            return folder.StartsWith("engines" + sep + "zapret", StringComparison.OrdinalIgnoreCase)
                && name.StartsWith("zy-", StringComparison.OrdinalIgnoreCase)
                // zy-own-* — копии списков, которые программа пишет для пресетов пользователя.
                && !name.StartsWith("zy-own-", StringComparison.OrdinalIgnoreCase);
        }

        return existing.Select(Normalize)
            .Where(f => f.Length > 0 && !keep.Contains(f) && Stale(f) && IsRemovable(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Относительный путь внутри папки, не данные человека и не рабочее состояние.</summary>
    internal static bool IsRemovable(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
            return false;

        var parts = relative.Split(Path.DirectorySeparatorChar);
        var name = parts[^1];

        // runtime\ и reports\ — состояние и сохранённые человеком отчёты; свои правила — rules.user*.yaml.
        return !parts[0].Equals("runtime", StringComparison.OrdinalIgnoreCase)
            && !parts[0].Equals("reports", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
            && !ProtectedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            && !(name.StartsWith("rules.user", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            && !UpdateInstaller.Preserved.Contains(relative, StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        path.Trim().TrimStart('﻿').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    /// <summary>Файлы корня и папок остатков сборки — всё, что <see cref="Legacy"/> вообще рассматривает.</summary>
    internal static IEnumerable<string> LegacyCandidates(string root)
    {
        foreach (var f in Directory.EnumerateFiles(root))
            yield return Path.GetRelativePath(root, f);

        foreach (var folder in LegacyRootFolders.Append(Path.Combine("engines", "zapret")))
        {
            var dir = Path.Combine(root, folder);

            if (!Directory.Exists(dir))
                continue;

            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                yield return Path.GetRelativePath(root, f);
        }
    }
}
