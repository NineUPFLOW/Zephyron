namespace Zephyron.Proxy;

/// <summary>Имя, у которого вердикт сменился между двумя проверками.</summary>
public sealed record VerdictChange(string Host, string Was, string Now);

/// <summary>
/// «Было — стало» по сохранённым отчётам проверки (reports\blockcheck-*.txt).
/// </summary>
/// <remarks>
/// Сайт ломается между двумя проверками, а не во время одной, — сравнение
/// и есть половина разбора. Читается тот же текст, что пишет окно: таблица
/// колонками 38 + 8 + 8 + 9 + 9, вердикт с 72-го знака.
/// </remarks>
public static class BlockCheckHistory
{
    /// <summary>Ширина колонки имени в отчёте: имя длиннее подрезано до 37.</summary>
    public const int NameWidth = 38;

    private const int VerdictAt = 72;

    /// <summary>Имя так, как его запишет отчёт: без пометки и подрезанное.</summary>
    public static string Key(string host) => host.Length >= NameWidth ? host[..(NameWidth - 1)] : host;

    /// <summary>Работали ли движки и вердикт по каждому имени; не отчёт — пусто.</summary>
    public static (bool? Engines, IReadOnlyDictionary<string, string> Verdicts) Parse(string text)
    {
        var verdicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool? engines = null;
        bool table = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("Движки:", StringComparison.Ordinal))
                engines = line.Contains("работали", StringComparison.Ordinal);
            else if (line.StartsWith("ИМЯ ", StringComparison.Ordinal))
                table = true;
            else if (table && line.StartsWith("---", StringComparison.Ordinal))
                continue;
            else if (table && line.Length == 0)
                break;
            else if (table && line.Length > VerdictAt)
            {
                var name = line[..NameWidth].Trim();
                int mark = name.IndexOf(" [", StringComparison.Ordinal);

                verdicts[mark > 0 ? name[..mark] : name] = line[VerdictAt..].Trim();
            }
        }

        return (engines, verdicts);
    }

    /// <summary>Что сменилось: только имена, проверенные оба раза.</summary>
    public static IReadOnlyList<VerdictChange> Compare(
        IReadOnlyDictionary<string, string> was,
        IEnumerable<(string Host, string Verdict)> now) =>
        now.Where(n => was.TryGetValue(Key(n.Host), out var before) && before != n.Verdict)
            .Select(n => new VerdictChange(n.Host, was[Key(n.Host)], n.Verdict))
            .ToList();
}
