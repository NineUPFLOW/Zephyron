using System.Text.Json;
using System.Text.Json.Serialization;
using Zephyron.Core.Rules;
using Zephyron.Proxy;

namespace Zephyron.Supervisor;

/// <summary>Одна запись прогона: что было, что записали и можно ли вернуть.</summary>
public sealed class AutoPickJournalEntry
{
    public string Host { get; set; } = "";

    /// <summary>Значение правила (<c>*.зона</c>) — по нему запись ищется при откате.</summary>
    public string Value { get; set; } = "";

    /// <summary>Что стояло на этом месте; <c>null</c> — записи не было, откат её удалит.</summary>
    public UserRuleEntry? Before { get; set; }

    /// <summary>
    /// Что записали; <c>null</c> бывает у испорченного журнала и у записи, где был
    /// только пин в hosts (<see cref="Pins"/>): правило имени не менялось.
    /// </summary>
    public UserRuleEntry? Wrote { get; set; }

    /// <summary>
    /// Что прибили в hosts: имя → адрес, как записано. Откат снимает пин, только
    /// если все эти строки на месте: иначе их правили после нас.
    /// </summary>
    public Dictionary<string, string>? Pins { get; set; }

    public string Why { get; set; } = "";

    public bool Restored { get; set; }
}

/// <summary>Один прогон автоподбора.</summary>
public sealed class AutoPickJournalRun
{
    public DateTimeOffset At { get; set; }

    public string Preset { get; set; } = "";

    public Dictionary<string, TimeSpan> Timings { get; set; } = [];

    public List<AutoPickJournalEntry> Entries { get; set; } = [];
}

/// <summary>
/// Журнал автоподбора: что записано и как вернуть каждую запись по отдельности.
/// </summary>
/// <remarks>
/// Откат точечный, а не «вернуть файл»: после прогона человек мог править
/// маршруты, и целый файл затёр бы его правки. Запись, изменённая после нас,
/// не трогается вовсе.
/// </remarks>
public static class AutoPickJournal
{
    public const string ChangedLater = "изменено после — не трогаю";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine("config", "autopick-journal.json");

    /// <summary>Читает журнал; нечитаемый (занят, нет доступа) считается пустым.</summary>
    public static List<AutoPickJournalRun> Load(string? path = null)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<AutoPickJournalRun> Read(string? path)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<AutoPickJournalRun>>(File.ReadAllText(target), Json) ?? [];
        }
        catch (JsonException)
        {
            // Испорченный журнал отодвигаем, а не затираем следующей записью:
            // по нему ещё можно вернуть правки руками.
            File.Move(target, target + ".broken", overwrite: true);
            return [];
        }
    }

    /// <param name="retire">
    /// Записи прошлых прогонов, чьи правила этот прогон убрал (дубли маршрута сервиса):
    /// помечаются снятыми в той же записи файла, чтобы журнал не разошёлся с маршрутами.
    /// </param>
    public static void Append(
        AutoPickJournalRun run,
        string? path = null,
        IReadOnlyCollection<AutoPickJournalEntry>? retire = null)
    {
        // Читаем без проглатывания ошибок: не прочитав журнал, дописать в него
        // значило бы затереть историю.
        var all = Read(path);

        if (retire is { Count: > 0 })
        {
            foreach (var entry in all.SelectMany(r => r.Entries).Where(e => !e.Restored))
            {
                if (retire.Any(s => s.Value == entry.Value && s.Wrote == entry.Wrote))
                    entry.Restored = true;
            }
        }

        all.Add(run);
        Save(all, path);
    }

    /// <summary>Возвращает одну запись; <c>null</c> — вернули, иначе причина отказа.</summary>
    public static string? Restore(
        AutoPickJournalRun run,
        AutoPickJournalEntry entry,
        string? rulesPath = null,
        string? journalPath = null,
        string? hostsPath = null) =>
        RestoreMany(run, [entry], rulesPath, journalPath, hostsPath)[0];

    /// <summary>Возвращает все записи прогона; по одному ответу на запись, как у <see cref="Restore"/>.</summary>
    public static IReadOnlyList<string?> RestoreAll(
        AutoPickJournalRun run,
        string? rulesPath = null,
        string? journalPath = null,
        string? hostsPath = null) =>
        RestoreMany(run, run.Entries, rulesPath, journalPath, hostsPath);

    private static IReadOnlyList<string?> RestoreMany(
        AutoPickJournalRun run,
        IReadOnlyList<AutoPickJournalEntry> entries,
        string? rulesPath,
        string? journalPath,
        string? hostsPath = null)
    {
        var file = UserRulesFile.Load(rulesPath);
        var results = new List<string?>();
        var unpin = new List<string>();
        var pinned = entries.Any(e => e.Pins is not null) ? HostsEditor.Pins(hostsPath) : null;

        foreach (var entry in entries)
        {
            // Пин, который правили после нас, не снимаем и правило при нём не трогаем.
            // Снятый руками целиком (ни одного нашего имени в hosts) — уже возвращён:
            // иначе запись навсегда осталась бы «изменено после».
            bool gone = entry.Pins is { } all && all.Keys.All(name => !pinned!.ContainsKey(name));

            var skipped = entry.Restored ? "уже возвращено"
                : entry.Pins is { } pins && !gone
                    && !pins.All(p => pinned!.TryGetValue(p.Key, out var now) && now == p.Value)
                    ? ChangedLater
                    : TryUndo(file, entry);

            if (skipped is null)
            {
                entry.Restored = true;
                if (entry.Pins is not null && !gone)
                    unpin.AddRange(entry.Pins.Keys);
            }

            results.Add(skipped);
        }

        if (results.Any(r => r is null))
        {
            if (unpin.Count > 0)
            {
                HostsEditor.Unpin(unpin, hostsPath);

                // Тесты правят свой файл: сброс кэша DNS системы им ни к чему.
                if (hostsPath is null)
                    HostsEditor.FlushDns();
            }

            file.Save();

            // Отметка уходит в журнал с диска, а не из переданного прогона:
            // вызывающий мог держать устаревшую копию.
            var all = Load(journalPath);

            if (all.FirstOrDefault(r => r.At == run.At) is { } stored)
            {
                foreach (var done in entries.Where(e => e.Restored))
                {
                    foreach (var mine in stored.Entries.Where(e => e.Host == done.Host && e.Value == done.Value))
                        mine.Restored = true;
                }

                Save(all, journalPath);
            }
        }

        return results;
    }

    /// <summary>
    /// Откатывает запись в памяти; <c>null</c> — откатили, иначе причина.
    /// </summary>
    internal static string? TryUndo(UserRulesFile file, AutoPickJournalEntry entry)
    {
        int index = -1;

        // С 26.10 автоподбор пишет и в маршрут сервиса (правило на список), а не только *.зона.
        var match = entry.Wrote?.Match ?? MatchKind.Domain;

        for (int i = 0; i < file.Entries.Count; i++)
        {
            if (file.Entries[i].Match == match && file.Entries[i].Matches(entry.Value))
            {
                index = i;
                break;
            }
        }

        // Только пин в hosts: правила тут нет, его снимает вызывающий.
        if (entry.Wrote is null && entry.Pins is not null)
            return null;

        if (entry.Wrote is null)
            return "запись журнала повреждена";

        if (index < 0 || file.Entries[index] != entry.Wrote)
            return ChangedLater;

        if (entry.Before is null)
            file.RemoveAt(index);
        else
            file.ReplaceAt(index, entry.Before);

        return null;
    }

    private static void Save(List<AutoPickJournalRun> all, string? path)
    {
        var target = path ?? DefaultPath;

        if (Path.GetDirectoryName(target) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        // Атомарно, как файл маршрутов: оборванная запись сделала бы журнал нечитаемым,
        // он ушёл бы в .broken — и с ним путь назад для уже записанных маршрутов.
        UserRulesFile.WriteAtomic(target, new System.Text.UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(all, Json)));
    }
}
