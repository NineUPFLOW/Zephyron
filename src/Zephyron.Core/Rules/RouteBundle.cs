using System.IO.Compression;

namespace Zephyron.Core.Rules;

/// <summary>
/// Маршруты вместе со своими списками одним файлом: zip с <c>rules.user.yaml</c> и <c>own/*.txt</c>.
/// </summary>
/// <remarks>
/// Голый файл правил (<see cref="RulesShare"/>) свои списки не несёт, и правила на них у
/// получателя молчат. Набор читается и там, где ждут обычный yaml: <see cref="Read"/> принимает оба вида.
/// Списки получателя не перезаписываются — в них могут быть имена, дописанные руками.
/// </remarks>
public static class RouteBundle
{
    public const string Extension = ".zyroutes";

    private const string RulesEntry = "rules.user.yaml";
    private const string ListsDir = "own/";

    /// <summary>Файл — набор (zip), а не голый yaml.</summary>
    public static bool IsBundle(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'P' && stream.ReadByte() == 'K';
    }

    /// <summary>Пишет набор; возвращает число упакованных списков.</summary>
    /// <param name="root">Корень установки, от которого считаются пути списков.</param>
    public static int Write(string rulesPath, string target, string? root = null)
    {
        var rules = RulesShare.Read(rulesPath);
        var names = rules
            .Where(r => r.Match == MatchKind.HostList && OwnLists.IsOwn(r.Value))
            .Select(r => OwnLists.NameOf(r.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tmp = target + ".tmp";
        var packed = 0;

        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(rulesPath, RulesEntry);

            foreach (var name in names)
            {
                var file = Path.Combine(root ?? string.Empty, OwnLists.PathFor(name).Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(file))
                    continue;

                zip.CreateEntryFromFile(file, ListsDir + name + ".txt");
                packed++;
            }
        }

        File.Move(tmp, target, overwrite: true);
        return packed;
    }

    /// <summary>Правила из файла — набора или голого yaml.</summary>
    public static IReadOnlyList<UserRuleEntry> Read(string path)
    {
        if (!IsBundle(path))
            return RulesShare.Read(path);

        var tmp = Path.Combine(Path.GetTempPath(), $"zephyron-bundle-{Guid.NewGuid():N}.yaml");

        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                CheckSize(zip);
                var entry = zip.GetEntry(RulesEntry) ?? throw new InvalidDataException("В наборе нет rules.user.yaml.");
                File.WriteAllText(tmp, Text(entry), new System.Text.UTF8Encoding(false));
            }

            return RulesShare.Read(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Названия списков, которые едут в наборе; у голого yaml — пусто.</summary>
    public static IReadOnlyList<string> ListNames(string path)
    {
        if (!IsBundle(path))
            return [];

        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Select(Name).OfType<string>().ToList();
    }

    /// <summary>
    /// Заводит списки из набора, которых здесь ещё нет.
    /// </summary>
    /// <returns>Сколько заведено и сколько оставлено как есть, потому что такой список уже был.</returns>
    public static (int Created, int Kept) ImportLists(string path, string? root = null)
    {
        if (!IsBundle(path))
            return (0, 0);

        int created = 0, kept = 0;

        using var zip = ZipFile.OpenRead(path);
        CheckSize(zip);

        foreach (var entry in zip.Entries)
        {
            if (Name(entry) is not { } name)
                continue;

            if (OwnLists.Exists(name, root))
            {
                kept++;
                continue;
            }

            var text = Text(entry);
            var full = Path.Combine(root ?? string.Empty, OwnLists.PathFor(name).Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(full))!);
            File.WriteAllText(full, text, new System.Text.UTF8Encoding(false));
            created++;
        }

        return (created, kept);
    }

    // Файл чужой, а программа от администратора: сжатая бомба не должна съесть память и диск.
    private const int MaxEntries = 512;
    private const long MaxEntryChars = 16 * 1024 * 1024;
    private const long MaxTotal = 64 * 1024 * 1024;

    private static void CheckSize(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries || zip.Entries.Any(e => e.Length > MaxEntryChars) || zip.Entries.Sum(e => e.Length) > MaxTotal)
            throw new InvalidDataException("Набор слишком большой.");
    }

    /// <summary>Текст записи; заголовку архива не верим — считаем прочитанное.</summary>
    private static string Text(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        var text = new System.Text.StringBuilder();
        var buffer = new char[81920];

        for (int read; (read = reader.Read(buffer, 0, buffer.Length)) > 0;)
        {
            text.Append(buffer, 0, read);

            if (text.Length > MaxEntryChars)
                throw new InvalidDataException("Набор слишком большой.");
        }

        return text.ToString();
    }

    /// <summary>Название списка из записи набора; <c>null</c> — не список или имя не годится (защита от «../»).</summary>
    private static string? Name(ZipArchiveEntry entry)
    {
        if (!entry.FullName.StartsWith(ListsDir, StringComparison.Ordinal) || !entry.FullName.EndsWith(".txt", StringComparison.Ordinal))
            return null;

        var name = entry.FullName[ListsDir.Length..^4];
        return OwnLists.Normalize(name) == name ? name : null;
    }
}
