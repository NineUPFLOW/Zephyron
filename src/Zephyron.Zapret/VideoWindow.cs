namespace Zephyron.Zapret;

/// <summary>
/// Узлы видео YouTube под фильтром: малое окно приёма плюс десинк googlevideo, без туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Замер 10.11.2026 у владельца, узел 74.125.154.103 (rr2---sn-4g5ednkd): TCP проходит, но
/// любой ответ сервера, начатый заголовком записи TLS, не доходит, а ClientHello с именем
/// googlevideo гасится по пути. Десинк googlevideo в одиночку — 0 из 3, окно в один байт
/// в одиночку — тоже 0. Вместе — 204 за 1,1 с: окно дробит ответ сервера, первый пакет несёт
/// один байт, фильтр его не узнаёт (модуль <c>config/lua/video-window.lua</c>).
/// </para>
/// <para>
/// Адреса пишет сторож узлов (<c>VideoNodeGuard</c>) в <see cref="DefaultListPath"/>, winws2
/// перечитывает файл сам. Пустой список winws2 считает «подходит всем» (проверено тогда же),
/// поэтому в файле всегда стоит адрес-заглушка из TEST-NET-3.
/// </para>
/// <para>
/// Подтверждение рукопожатия идёт без данных, а такие пакеты winws2 по умолчанию не видит;
/// без окна на нём сервер получает полное окно, и приём не работает. Поэтому пустые пакеты
/// берутся в перехват, но только к сетям фронтов Google (<see cref="GoogleFrontEnds"/>):
/// иначе через winws2 шли бы подтверждения всех загрузок машины.
/// </para>
/// </remarks>
public static class VideoWindow
{
    public static string DefaultListPath => Path.Combine("runtime", "video-window.txt");

    /// <summary>Модуль Lua в папке движка (<c>build.cmd</c> кладёт его с приставкой <c>zy-</c>).</summary>
    public const string Module = "zy-video-window.lua";

    /// <summary>Заглушка: пустой список совпал бы со всеми адресами.</summary>
    public const string Placeholder = "203.0.113.255";

    /// <summary>
    /// Сети фронтов Google, где живут узлы googlevideo. Их же нельзя отдавать секциям,
    /// подделывающим SYN: секция «discord.com (IP fallback)» с <c>syndata</c> ловила их
    /// по <c>ipset-discord</c> (там 74.125.0.0/16 и 173.194.0.0/16), и к узлам под фильтром
    /// не проходил даже TCP — замер 10.11: без winws2 3 из 3, с этой секцией 0 из 3.
    /// </summary>
    public static readonly IReadOnlyList<string> GoogleFrontEnds =
    [
        "64.233.160.0/19",
        "74.125.0.0/16",
        "108.177.0.0/17",
        "142.250.0.0/15",
        "172.217.0.0/16",
        "172.253.0.0/16",
        "173.194.0.0/16",
        "209.85.128.0/17",
        "216.58.192.0/19",
    ];

    private const string RawFilterKey = "--wf-raw-filter=";

    /// <summary>Создаёт список с заглушкой, если его нет или в нём нет ни одного адреса.</summary>
    public static void EnsureList(string? path = null)
    {
        var target = path ?? DefaultListPath;
        var lines = File.Exists(target) ? File.ReadAllLines(target) : [];

        if (!lines.Any(l => l.Trim() == Placeholder))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            Core.AtomicFile.WriteAllText(target, string.Join("\n", lines.Prepend(Placeholder)) + "\n");
        }
    }

    /// <summary>Адреса в списке, без заглушки.</summary>
    public static IReadOnlyList<string> Read(string? path = null)
    {
        var target = path ?? DefaultListPath;

        return File.Exists(target)
            ? File.ReadAllLines(target).Select(l => l.Trim()).Where(l => l.Length > 0 && l != Placeholder && !l.StartsWith('#')).ToList()
            : [];
    }

    /// <summary>Переписывает список: заглушка и эти адреса.</summary>
    public static void Write(IEnumerable<string> addresses, string? path = null)
    {
        var target = path ?? DefaultListPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        Core.AtomicFile.WriteAllText(target,
            string.Join("\n", addresses.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Prepend(Placeholder)) + "\n");
    }

    /// <summary>Профиль: окно, затем шаги десинка googlevideo из пресета на ClientHello.</summary>
    public static IReadOnlyList<string> Profile(string listPath, IEnumerable<string> videoSteps) =>
    [
        "--new",
        "--name=Zephyron: видео под фильтром",
        "--filter-tcp=443",
        $"--ipset={listPath.Replace('\\', '/')}",
        "--out-range=-n10",
        "--payload=all",
        "--lua-desync=zy_video_window",
        "--payload=tls_client_hello",
        .. videoSteps,
    ];

    /// <summary>
    /// Секции по адресам с подделкой SYN не трогают фронты Google (см. <see cref="GoogleFrontEnds"/>).
    /// </summary>
    public static IReadOnlyList<string> SparedFromSynData(IReadOnlyList<string> sectionArguments)
    {
        bool byAddress = sectionArguments.Any(a => a.StartsWith("--ipset=", StringComparison.Ordinal));
        bool synData = sectionArguments.Any(a => a.StartsWith("--lua-desync=syndata", StringComparison.Ordinal));

        return byAddress && synData
            ? [.. sectionArguments, "--ipset-exclude-ip=" + string.Join(',', GoogleFrontEnds)]
            : sectionArguments;
    }

    /// <summary>
    /// Пустые пакеты к фронтам Google — в перехват: глобальный <c>--wf-tcp-empty=1</c>
    /// и условие в <c>--wf-raw-filter</c>, которое оставляет из пустых только эти.
    /// </summary>
    /// <returns><c>false</c> — фильтр пресета задан файлом, дописать некуда; профиль тогда без толку.</returns>
    public static bool CaptureEmptyAcks(List<string> arguments)
    {
        var condition = "(!tcp or tcp.Syn or tcp.Rst or tcp.Fin or tcp.PayloadLength>0 or "
            + string.Join(" or ", GoogleFrontEnds.Select(Range)) + ")";

        int index = arguments.FindIndex(a => a.StartsWith(RawFilterKey, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            var theirs = arguments[index][RawFilterKey.Length..];

            if (theirs.StartsWith('@'))
                return false;

            arguments[index] = $"{RawFilterKey}({theirs}) and {condition}";
        }
        else
        {
            arguments.Insert(0, RawFilterKey + condition);
        }

        arguments.RemoveAll(a => a.StartsWith("--wf-tcp-empty=", StringComparison.Ordinal));
        arguments.Insert(0, "--wf-tcp-empty=1");
        return true;
    }

    /// <summary><c>a.b.c.d/n</c> → условие WinDivert на адрес назначения.</summary>
    public static string Range(string cidr)
    {
        var (network, bits) = (System.Net.IPAddress.Parse(cidr[..cidr.IndexOf('/')]), int.Parse(cidr[(cidr.IndexOf('/') + 1)..]));
        var bytes = network.GetAddressBytes();
        uint first = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        uint last = first | (bits == 0 ? uint.MaxValue : (1u << (32 - bits)) - 1);

        static string Ip(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

        return $"(ip.DstAddr>={Ip(first)} and ip.DstAddr<={Ip(last)})";
    }
}
