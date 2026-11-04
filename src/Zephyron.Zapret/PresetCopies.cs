using Zephyron.Core;

namespace Zephyron.Zapret;

/// <summary>Копия и переименование пресета в папке пресетов.</summary>
/// <remarks>
/// Пресет зовётся по имени файла, строка <c># Preset:</c> правится вместе с ним.
/// Пресет поставки (есть <c># BuiltinVersion:</c>) не переименовывается: обновление
/// вернёт его под прежним именем. Копия теряет BuiltinVersion — она уже своя.
/// </remarks>
public static class PresetCopies
{
    private const int MaxName = 80;

    public static bool Shipped(string path) =>
        File.ReadLines(path).TakeWhile(l => l.StartsWith('#') || l.Trim().Length == 0)
            .Any(l => l.StartsWith("# BuiltinVersion:", StringComparison.Ordinal));

    /// <summary>Что не так с именем; <c>null</c> — годится. <paramref name="except"/> — файл, которому имя можно оставить.</summary>
    public static string? Problem(string? name, string directory, string? except = null)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Название не может быть пустым.";

        if (trimmed.Length > MaxName)
            return $"Название длиннее {MaxName} знаков.";

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.EndsWith('.'))
            return "В названии нельзя \\ / : * ? \" < > | и точку в конце.";

        var target = Path.GetFullPath(Path.Combine(directory, trimmed + ".txt"));

        if (File.Exists(target) && !(except is not null
            && string.Equals(target, Path.GetFullPath(Path.Combine(directory, except)), StringComparison.OrdinalIgnoreCase)))
            return $"Пресет «{trimmed}» уже есть.";

        return null;
    }

    /// <summary>Свободное имя копии: «Копия X», «Копия X (2)»…</summary>
    public static string CopyName(string directory, string name)
    {
        var first = $"Копия {name}";

        if (first.Length > MaxName)
            first = first[..MaxName].TrimEnd();

        var candidate = first;

        for (int n = 2; File.Exists(Path.Combine(directory, candidate + ".txt")); n++)
            candidate = $"{first} ({n})";

        return candidate;
    }

    /// <summary>Копирует пресет под новым именем; возвращает имя нового файла.</summary>
    public static string Duplicate(string directory, string file, string name)
    {
        name = name.Trim();

        if (Problem(name, directory) is { } problem)
            throw new InvalidOperationException(problem);

        File.WriteAllLines(Path.Combine(directory, name + ".txt"),
            Retitle(File.ReadAllLines(Path.Combine(directory, file)), name, dropBuiltin: true));

        return name + ".txt";
    }

    /// <summary>Переименовывает свой пресет; возвращает новое имя файла.</summary>
    public static string Rename(string directory, string file, string name)
    {
        name = name.Trim();
        var source = Path.Combine(directory, file);

        if (Shipped(source))
            throw new InvalidOperationException("Пресет программы возвращается с каждым обновлением — переименуйте его копию.");

        if (Problem(name, directory, except: file) is { } problem)
            throw new InvalidOperationException(problem);

        var lines = Retitle(File.ReadAllLines(source), name, dropBuiltin: false);
        var target = Path.Combine(directory, name + ".txt");

        // Сначала новый файл, потом удаление старого. Смена регистра — тот же файл в Windows.
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            var temporary = source + ".rename";
            File.WriteAllLines(temporary, lines);
            File.Delete(source);
            File.Move(temporary, target);
        }
        else
        {
            File.WriteAllLines(target, lines);
            File.Delete(source);
        }

        return name + ".txt";
    }

    /// <summary>Выбранный пресет идёт за переименованием.</summary>
    public static AppSettings Renamed(AppSettings settings, string oldName, string newName) =>
        string.Equals(settings.PresetName, oldName, StringComparison.OrdinalIgnoreCase)
            ? settings with { PresetName = newName }
            : settings;

    internal static IReadOnlyList<string> Retitle(IReadOnlyList<string> lines, string name, bool dropBuiltin)
    {
        var result = new List<string>();
        bool titled = false, header = true;

        foreach (var line in lines)
        {
            if (header && !line.StartsWith('#') && line.Trim().Length > 0)
                header = false;

            if (header && line.StartsWith("# Preset:", StringComparison.Ordinal))
            {
                result.Add($"# Preset: {name}");
                titled = true;
            }
            else if (!(header && dropBuiltin && line.StartsWith("# BuiltinVersion:", StringComparison.Ordinal)))
                result.Add(line);
        }

        if (!titled)
            result.Insert(0, $"# Preset: {name}");

        return result;
    }
}
