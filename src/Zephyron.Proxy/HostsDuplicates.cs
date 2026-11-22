namespace Zephyron.Proxy;

/// <summary>Имя, прибитое в hosts не в первый раз.</summary>
public sealed record HostsRepeat
{
    public required string Name { get; init; }

    /// <summary>Строка повтора, считая с нуля.</summary>
    public required int Line { get; init; }

    public required string Address { get; init; }

    /// <summary>Первая строка этого имени в файле.</summary>
    public required int FirstLine { get; init; }

    public required string FirstAddress { get; init; }

    /// <summary>
    /// Другой адрес, чем у первой записи, — и в другом месте файла.
    /// </summary>
    /// <remarks>
    /// Windows отдаёт программам все адреса имени (замер 22.11), так что такая
    /// строка действует наравне с первой: имя ведут сразу два хозяина, и программа
    /// может пойти по любому. Второй адрес в том же блоке — это не повтор, а запасной
    /// адрес пина, и в отчёт он не попадает.
    /// </remarks>
    public bool Conflicts => !string.Equals(Address, FirstAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>Блок, в котором стоит повтор; <c>null</c> — вне блоков.</summary>
    public string? Block { get; init; }
}

/// <summary>Что нашлось в hosts: повторы, спорящие записи и строки, которые можно убрать.</summary>
public sealed record HostsDuplicateReport
{
    public required IReadOnlyList<HostsRepeat> Repeats { get; init; }

    /// <summary>
    /// Строки, целиком состоящие из повторов с тем же адресом.
    /// </summary>
    /// <remarks>
    /// Только такие убираются без последствий: всё, что в них сказано, уже
    /// сказано строкой выше, и разрешение имён от удаления не изменится.
    /// Строку со спорящим адресом или с новым именем среди повторов
    /// не трогаем — там убрать значит изменить смысл, а это решает человек.
    /// </remarks>
    public required IReadOnlyList<int> Removable { get; init; }

    /// <summary>Чужие блоки, откуда убираются строки: их хозяин может вернуть записи.</summary>
    public required IReadOnlyList<string> ForeignBlocks { get; init; }

    public int Same => Repeats.Count(r => !r.Conflicts);

    public int Conflicting => Repeats.Count(r => r.Conflicts);

    public bool Any => Repeats.Count > 0;
}

/// <summary>
/// Ищет в hosts имена, прибитые больше одного раза.
/// </summary>
/// <remarks>
/// <para>
/// Замер 23.09 на живом файле: все имена Claude прибиты дважды к одному
/// адресу — нашим блоком и блоком ZapretGUI ниже. Повтор с тем же адресом
/// ничего не добавляет, а с другим адресом из другого блока — действует наравне
/// с первым: Windows отдаёт все адреса имени (замер 22.11), и снявший наш пин
/// продолжал бы ходить по чужому.
/// </para>
/// <para>
/// Только чтение. Убирает строки <see cref="HostsEditor.Remove"/> — с копией
/// и после вопроса человеку.
/// </para>
/// </remarks>
public static class HostsDuplicates
{
    public static HostsDuplicateReport Find(string? path = null)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(path ?? HostsFile.DefaultPath);
        }
        catch (Exception)
        {
            lines = [];
        }

        return Find(HostsEditor.Parse(path), lines);
    }

    /// <param name="entries">Разобранные записи — <see cref="HostsEditor.Parse"/>.</param>
    /// <param name="lines">Сам файл: по нему узнаётся, в каком блоке строка.</param>
    public static HostsDuplicateReport Find(IReadOnlyList<HostsEntry> entries, IReadOnlyList<string> lines)
    {
        var blocks = BlocksOf(lines);
        var first = new Dictionary<string, (int Line, string Address)>(StringComparer.OrdinalIgnoreCase);
        var repeats = new List<HostsRepeat>();
        var removable = new List<int>();

        // Выключенные записи не считаются: Windows их не читает, и повтором
        // действующей строки закомментированная не является.
        foreach (var entry in entries.Where(e => e.Enabled).OrderBy(e => e.Line))
        {
            int repeated = 0;
            bool allSame = true;

            foreach (var name in entry.Names)
            {
                if (!first.TryGetValue(name, out var earlier))
                {
                    first[name] = (entry.Line, entry.Address);
                    allSame = false;
                    continue;
                }

                // Другой адрес в том же блоке — запасной адрес того же пина, не повтор.
                if (!string.Equals(entry.Address, earlier.Address, StringComparison.OrdinalIgnoreCase)
                    && blocks.GetValueOrDefault(entry.Line) == blocks.GetValueOrDefault(earlier.Line))
                {
                    allSame = false;
                    continue;
                }

                var repeat = new HostsRepeat
                {
                    Name = name,
                    Line = entry.Line,
                    Address = entry.Address,
                    FirstLine = earlier.Line,
                    FirstAddress = earlier.Address,
                    Block = blocks.GetValueOrDefault(entry.Line),
                };

                repeats.Add(repeat);
                repeated++;

                if (repeat.Conflicts)
                    allSame = false;
            }

            // Повтор внутри своего же блока правится перезаписью блока,
            // а не удалением строки: блок ведём мы, и строка в нём —
            // не чужая запись, а наше состояние.
            if (repeated > 0 && allSame && !IsOurs(blocks.GetValueOrDefault(entry.Line)))
                removable.Add(entry.Line);
        }

        var foreign = removable
            .Select(line => blocks.GetValueOrDefault(line))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new HostsDuplicateReport { Repeats = repeats, Removable = removable, ForeignBlocks = foreign };
    }

    /// <summary>
    /// Строка → кто её ведёт: самый внутренний блок или раздел.
    /// </summary>
    /// <remarks>
    /// Тот же разбор, что у таблицы раздела (<see cref="HostsTable.SourcesOf"/>).
    /// До 19.10 здесь был свой, плоский: закрывающая строка вложенного блока
    /// Zapret GUI обнуляла блок, и остаток нашего считался строками вне блоков —
    /// «Убрать повторы» могла удалить нашу же запись как чужую.
    /// </remarks>
    private static Dictionary<int, string> BlocksOf(IReadOnlyList<string> lines) => HostsTable.SourcesOf(lines);

    private static bool IsOurs(string? block) => block == HostsTable.OurSource;
}
