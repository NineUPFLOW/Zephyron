using Zephyron.Core;

namespace Zephyron.Zapret;

/// <summary>Чем подделка служит: приветствием TLS, первым пакетом QUIC или прочим UDP.</summary>
public enum FakeKind
{
    Tls,
    Quic,
    Udp,
    Other,
}

/// <summary>Образец подделки: встроенный в winws2 или файл из <c>bin\</c>.</summary>
/// <param name="Id">Как он хранится в настройках: имя файла (<c>stun2.bin</c>) или встроенное имя.</param>
public sealed record FakeBlob(string Id, FakeKind Kind, bool BuiltIn)
{
    /// <summary>Как назвать человеку: без <c>.bin</c>.</summary>
    public string Title => BuiltIn ? Id : Path.GetFileNameWithoutExtension(Id);
}

/// <summary>Место в секции пресета, где стоит подделка.</summary>
/// <param name="Slot">Номер шага с <c>blob=</c> внутри секции, с нуля.</param>
/// <param name="Step">Шаг <c>--lua-desync</c> целиком — показать, что именно меняется.</param>
/// <param name="Blob">Образец по пресету: имя из <c>blob=</c>.</param>
/// <param name="BlobFile">Файл, которым пресет объявил образец; <c>null</c> — встроенный.</param>
/// <param name="Details">Списки и фильтры секции одной строкой — для поиска и подписи.</param>
public sealed record FakeSlot(string Section, int Slot, string Step, string Blob, string? BlobFile, bool Udp, string Details);

/// <summary>
/// Выбор подделки у секции пресета без правки файла пресета.
/// </summary>
/// <remarks>
/// <para>
/// Выбор хранится в настройках по тройке «пресет — секция — место» и подставляется в готовую
/// строку запуска (<see cref="Apply"/>): обновление пресета выбор не стирает, а исчезнувшая
/// секция остаётся как в пресете. Файл образца объявляется своим именем с приставкой
/// <c>zy_fake_</c> — как у game filter, чтобы не спорить с объявлениями пресета.
/// </para>
/// <para>
/// Подставляет <see cref="Apply"/> тот, кто собирает строку winws2 (надзор);
/// без этого вызова выбор сохраняется, но на запуск не действует.
/// </para>
/// </remarks>
public static class FakeChoice
{
    public static IReadOnlyList<FakeBlob> BuiltIns { get; } =
    [
        new("fake_default_tls", FakeKind.Tls, true),
        new("fake_default_quic", FakeKind.Quic, true),
        new("fake_default_udp", FakeKind.Udp, true),
        new("fake_default_http", FakeKind.Other, true),
    ];

    private const string Prefix = "zy_fake_";

    /// <summary>Чем служит образец — по имени файла или встроенному имени.</summary>
    public static FakeKind KindOf(string id)
    {
        var name = Path.GetFileNameWithoutExtension(id).ToLowerInvariant();

        bool Has(string s) => name.Contains(s, StringComparison.Ordinal);

        if (name.StartsWith("tls", StringComparison.Ordinal) || Has("clienthello") || name == "fake_default_tls")
            return FakeKind.Tls;

        if (name.StartsWith("quic", StringComparison.Ordinal) || name == "fake_default_quic")
            return FakeKind.Quic;

        if (name.StartsWith("stun", StringComparison.Ordinal) || name.StartsWith("active_", StringComparison.Ordinal)
            || name.StartsWith("dtls", StringComparison.Ordinal) || Has("discord") || Has("wireguard") || Has("udp"))
            return FakeKind.Udp;

        return FakeKind.Other;
    }

    /// <summary>Образцы, что есть: встроенные и файлы <c>bin\*.bin</c> корня Zapret.</summary>
    public static IReadOnlyList<FakeBlob> Available(string? zapretRoot)
    {
        var bin = zapretRoot is null ? null : Path.Combine(zapretRoot, "bin");

        var files = bin is not null && Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "*.bin").Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase).Select(f => new FakeBlob(f, KindOf(f), false))
            : [];

        return [.. BuiltIns, .. files];
    }

    /// <summary>Что из образцов годится в это место: по протоколу секции.</summary>
    public static IReadOnlyList<FakeBlob> Fitting(FakeSlot slot, IReadOnlyList<FakeBlob> available) =>
        available.Where(b => slot.Udp ? b.Kind is FakeKind.Quic or FakeKind.Udp : b.Kind is FakeKind.Tls or FakeKind.Other).ToList();

    /// <summary>Места подделок в пресете: по секциям с именем, по порядку шагов.</summary>
    public static IReadOnlyList<FakeSlot> Slots(ZapretPreset preset)
    {
        var declared = Declarations(preset.GlobalArguments);
        var result = new List<FakeSlot>();

        foreach (var section in preset.Sections.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            bool udp = IsUdp(section.RawArguments);
            int slot = 0;

            var details = string.Join("; ", section.HostListPaths.Concat(section.IpSetPaths).Select(Path.GetFileName)
                .Concat(section.RawArguments.Where(a => a.StartsWith("--filter-", StringComparison.Ordinal))));

            foreach (var step in section.DesyncRecipes.Where(s => BlobOf(s) is not null))
            {
                var blob = BlobOf(step)!;
                result.Add(new FakeSlot(section.Name, slot++, step, blob, declared.GetValueOrDefault(blob), udp, details));
            }
        }

        return result;
    }

    /// <summary>
    /// Места, подходящие под запрос: каждое слово должно найтись в имени секции, её списках или фильтрах.
    /// </summary>
    public static IReadOnlyList<FakeSlot> Search(IEnumerable<FakeSlot> slots, string? query)
    {
        var words = (query ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        return slots
            .Where(s => words.All(w => s.Section.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                        || s.Details.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
    }

    /// <summary>Подставляет выбранные подделки в готовую строку запуска; возвращает число заменённых мест.</summary>
    public static int Apply(List<string> arguments, string? presetName, IReadOnlyList<FakeSwap>? swaps)
    {
        var mine = (swaps ?? [])
            .Where(s => string.Equals(s.Preset, presetName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Blob))
            .ToList();

        if (mine.Count == 0)
            return 0;

        var declare = new List<string>();
        int replaced = 0, start = 0;

        while (start < arguments.Count)
        {
            int end = arguments.IndexOf("--new", start);

            if (end < 0)
                end = arguments.Count;

            var name = arguments.Skip(start).Take(end - start)
                .FirstOrDefault(a => a.StartsWith("--name=", StringComparison.Ordinal))?["--name=".Length..];

            int slot = 0;

            for (int i = start; i < end && name is not null; i++)
            {
                if (!arguments[i].StartsWith("--lua-desync=", StringComparison.Ordinal)
                    || BlobOf(arguments[i]["--lua-desync=".Length..]) is null)
                    continue;

                int here = slot++;
                var swap = mine.FirstOrDefault(s => s.Slot == here && string.Equals(s.Section, name, StringComparison.Ordinal));

                if (swap is null)
                    continue;

                // Значение из файла настроек: только встроенный образец или файл прямо в bin.
                if (!BuiltIns.Any(b => b.Id == swap.Blob)
                    && (Path.GetFileName(swap.Blob) != swap.Blob || !swap.Blob.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var blob = NameFor(swap.Blob);
                var declaration = $"--blob={blob}:@bin/{swap.Blob}";

                if (blob != swap.Blob && !declare.Contains(declaration))
                    declare.Add(declaration);

                arguments[i] = "--lua-desync=" + WithBlob(arguments[i]["--lua-desync=".Length..], blob);
                replaced++;
            }

            start = end + 1;
        }

        // Глобальные ключи от места в строке не зависят — в начало, как у game filter.
        arguments.InsertRange(0, declare);

        return replaced;
    }

    /// <summary>Имя образца в строке: встроенный — как есть, файл — своим объявлением.</summary>
    internal static string NameFor(string id) =>
        BuiltIns.Any(b => b.Id == id)
            ? id
            : Prefix + new string(Path.GetFileNameWithoutExtension(id).Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray());

    /// <summary>Значение <c>blob=</c> шага; <c>null</c> — подделки в шаге нет.</summary>
    internal static string? BlobOf(string step) =>
        step.Split(':').Skip(1)
            .Where(p => p.StartsWith("blob=", StringComparison.Ordinal))
            .Select(p => p["blob=".Length..])
            .FirstOrDefault(v => v.Length > 0);

    private static string WithBlob(string step, string blob) =>
        string.Join(':', step.Split(':').Select((p, i) => i > 0 && p.StartsWith("blob=", StringComparison.Ordinal) ? "blob=" + blob : p));

    /// <summary>Объявления <c>--blob=имя:@файл</c>: имя → имя файла.</summary>
    private static Dictionary<string, string> Declarations(IEnumerable<string> globals)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var arg in globals.Where(a => a.StartsWith("--blob=", StringComparison.Ordinal)))
        {
            var body = arg["--blob=".Length..];
            int colon = body.IndexOf(':');

            if (colon > 0 && body.Length > colon + 1 && body[colon + 1] == '@')
                result.TryAdd(body[..colon], Path.GetFileName(body[(colon + 2)..]));
        }

        return result;
    }

    private static bool IsUdp(IEnumerable<string> raw) =>
        raw.Any(a => a.StartsWith("--filter-udp", StringComparison.Ordinal)
                     || (a.StartsWith("--filter-l7=", StringComparison.Ordinal)
                         && a.Split('=', 2)[1].Split(',').Any(p => p is "quic" or "stun" or "discord" or "wireguard" or "dht" or "dtls")));
}
