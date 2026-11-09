using System.Runtime.InteropServices;

namespace Zephyron.Core.Diagnostics;

/// <summary>
/// Узлы сервиса, на которые на этой машине недавно ходили, — из кэша DNS Windows.
/// </summary>
/// <remarks>
/// Списки держат зоны (<c>googlevideo.com</c>, <c>cdninstagram.com</c>), а видео и картинки
/// идут с узлов внутри зоны (<c>rr1---sn-….googlevideo.com</c>) — у них свой адрес и своя судьба.
/// Кэш DNS Windows (<c>DnsGetCacheDataTable</c>) читается без прав администратора и знает имена,
/// которые машина разрешала в последние минуты. Браузер со своим DoH туда не пишет.
/// </remarks>
public static class RecentNodes
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CacheEntry
    {
        public IntPtr Next;
        public IntPtr Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCacheDataTable(out IntPtr first);

    /// <summary>Имена в кэше DNS Windows; не прочиталось — пусто.</summary>
    /// <remarks>
    /// Функция не описана в документации, но живёт в dnsapi.dll с Windows 7
    /// и ею пользуются утилиты вроде DNSQuerySniffer. Память записей кэш
    /// отдаёт насовсем: освобождать её нечем — на один прогон проверки это
    /// несколько килобайт.
    /// </remarks>
    public static IReadOnlyList<string> FromDnsCache()
    {
        var names = new List<string>();

        try
        {
            if (!GetCacheDataTable(out var entry))
                return names;

            for (int guard = 0; entry != IntPtr.Zero && guard < 10_000; guard++)
            {
                var cached = Marshal.PtrToStructure<CacheEntry>(entry);

                if (Marshal.PtrToStringUni(cached.Name) is { Length: > 0 } name)
                    names.Add(name);

                entry = cached.Next;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
        }

        return names;
    }

    /// <summary>
    /// Недавние узлы внутри зон — к проверке, под подписью зоны.
    /// </summary>
    /// <param name="recent">Недавние имена (<see cref="FromDnsCache"/>).</param>
    /// <param name="zones">Зона → подпись строки проверки (часть сервиса).</param>
    /// <param name="taken">Уже взятые в проверку имена — их не повторять; дополняется.</param>
    /// <param name="perLabel">Сколько узлов на подпись.</param>
    /// <param name="total">Сколько узлов всего.</param>
    /// <remarks>
    /// Только поддомены зоны, не сама зона: её проверка и так меряет. Зона —
    /// ближайшая к имени (<c>a.b.example</c> ищется как <c>b.example</c>, затем
    /// <c>example</c>), чтобы узел попал под свою часть, а не под широкую.
    /// </remarks>
    public static IReadOnlyList<(string Host, string Label)> Pick(
        IEnumerable<string> recent,
        IReadOnlyDictionary<string, string> zones,
        ISet<string> taken,
        int perLabel = 2,
        int total = 24)
    {
        var picked = new List<(string, string)>();
        var perLabelCount = new Dictionary<string, int>(StringComparer.Ordinal);

        // Узел с цифрами в первом слове — первым: scontent-hel3-1, rr1---sn-…,
        // p2p-sto1 — это и есть точка раздачи, а scontent или static — общее имя,
        // которое DNS разводит по узлам. При двух местах на часть общие имена
        // занимали оба (замер 08.11 у владельца: Instagram взял scontent
        // и static, а scontent-hel3-1 остался за бортом).
        foreach (var raw in recent.OrderBy(n => n.Split('.')[0].Any(char.IsDigit) ? 0 : 1))
        {
            if (picked.Count >= total)
                break;

            var name = raw.Trim().TrimEnd('.').ToLowerInvariant();

            if (name.Length == 0 || !name.Contains('.') || System.Net.IPAddress.TryParse(name, out _) || taken.Contains(name))
                continue;

            if (ZoneOf(name, zones) is not { } label)
                continue;

            int count = perLabelCount.GetValueOrDefault(label);

            if (count >= perLabel)
                continue;

            perLabelCount[label] = count + 1;
            taken.Add(name);
            picked.Add((name, label));
        }

        return picked;
    }

    /// <summary>
    /// Настоящие узлы видео YouTube (<c>rr*---sn-*.googlevideo.com</c>) из недавних имён —
    /// вместо redirector, который показывает только фронтенд (<c>ServicePart.Probe</c> остаётся запасным).
    /// </summary>
    public static IReadOnlyList<string> VideoNodes(IEnumerable<string> recent, int count = 3) =>
        recent.Select(n => n.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(n => VideoNode.IsMatch(n))
            .Distinct(StringComparer.Ordinal)
            .Take(count)
            .ToList();

    private static readonly System.Text.RegularExpressions.Regex VideoNode =
        new(@"^rr\d+---sn-[a-z0-9-]+\.googlevideo\.com$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Подпись ближайшей зоны, в которую имя входит поддоменом.</summary>
    private static string? ZoneOf(string name, IReadOnlyDictionary<string, string> zones)
    {
        for (int dot = name.IndexOf('.'); dot > 0 && dot < name.Length - 1; dot = name.IndexOf('.', dot + 1))
        {
            if (zones.TryGetValue(name[(dot + 1)..], out var label))
                return label;
        }

        return null;
    }
}
