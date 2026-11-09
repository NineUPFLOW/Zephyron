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

        if (Zephyron.Core.Rules.OwnLists.IsDeviceName(trimmed))
            return "Такое имя занято Windows.";

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

    /// <summary>
    /// Свой пресет: копия выбранного, а впереди — рецепты, подобранные под эту сеть.
    /// </summary>
    /// <remarks>
    /// Подобранное и так работает поверх любого пресета, но живёт в маршрутах. Файлом
    /// его можно передать, сохранить на будущее и включить на другой машине. Списки имён
    /// копируются в <paramref name="listsDirectory"/> под своими именами: пресет ссылается
    /// на них путём от корня Zapret, как и встроенные.
    /// </remarks>
    public static string WithOwn(string directory, string listsDirectory, string file, string name, IReadOnlyList<OwnDesyncProfile> own)
    {
        name = name.Trim();

        if (Problem(name, directory) is { } problem)
            throw new InvalidOperationException(problem);

        var lines = Retitle(File.ReadAllLines(Path.Combine(directory, file)), name, dropBuiltin: true).ToList();
        var block = new List<string>();
        // Хвост-хеш: пустой слаг и разные имена с одинаковыми буквами не должны слиться в один файл.
        var slug = WinwsCommandLine.Sanitize(name).ToLowerInvariant();

        Directory.CreateDirectory(listsDirectory);

        for (int i = 0; i < own.Count; i++)
        {
            var profile = own[i];

            if (profile.Steps.Count == 0 || !File.Exists(profile.HostListPath))
                continue;

            var list = $"zy-own-{slug}-{i + 1}.txt";
            File.Copy(profile.HostListPath, Path.Combine(listsDirectory, list), overwrite: true);

            block.Add($"--name=Подбор: {profile.Name}");

            if (RecipeCatalog.IsPass(profile.Steps))
            {
                block.Add("--filter-tcp=*");
                block.Add("--filter-udp=443");
            }
            else
            {
                block.Add("--filter-tcp=" + (string.IsNullOrWhiteSpace(profile.Ports) ? PresetPorts.Default : profile.Ports));
            }

            block.Add($"--hostlist=lists/{list}");
            block.Add(WinwsCommandLine.ProbeOutRange);
            block.AddRange(profile.Steps.Select(s => "--lua-desync=" + s));
            block.Add("--new");
            block.Add(string.Empty);
        }

        if (block.Count == 0)
            throw new InvalidOperationException("Подбор не записал рецептов десинка — сохранять в пресет нечего.");

        // Перед первым профилем пресета: до него идут только общие ключи.
        int at = lines.FindIndex(l => l.StartsWith("--name=", StringComparison.Ordinal)
            || l.StartsWith("--filter-", StringComparison.Ordinal)
            || l.StartsWith("--new", StringComparison.Ordinal));

        lines.InsertRange(at < 0 ? lines.Count : at, block);
        File.WriteAllLines(Path.Combine(directory, name + ".txt"), lines);

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

    /// <summary>Выбранный пресет и подделки его секций идут за переименованием.</summary>
    public static AppSettings Renamed(AppSettings settings, string oldName, string newName) => settings with
    {
        PresetName = string.Equals(settings.PresetName, oldName, StringComparison.OrdinalIgnoreCase) ? newName : settings.PresetName,
        FakeSwaps = settings.FakeSwaps
            .Select(s => string.Equals(s.Preset, oldName, StringComparison.OrdinalIgnoreCase) ? s with { Preset = newName } : s)
            .ToList(),
    };

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
