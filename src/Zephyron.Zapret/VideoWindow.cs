namespace Zephyron.Zapret;

/// <summary>
/// Узлы видео YouTube под фильтром: малое окно приёма тем адресам, у которых ответ срезали, без туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Замер 10.11.2026 у владельца, узел 74.125.154.103: TCP проходит, но первый пакет ответа,
/// начатый заголовком записи TLS, не доходит. Окно приёма в один байт дробит ответ, и фильтр
/// его не узнаёт; вместе с десинком googlevideo узел ответил 204 за 1,1 с вместо тишины.
/// </para>
/// <para>
/// Окно без масштаба держит соединение на ≈8 Мбит/с, а режет фильтр не каждое соединение,
/// поэтому решает модуль <c>config/lua/video-window.lua</c> по адресу: срезали ответ — окно на
/// минуту, ещё раз — на час, удачное обычное соединение сбрасывает счёт. Первая версия (1.4.5)
/// ставила окно целым сетям по пробам сторожа и замедлила здоровые узлы и кэш провайдера.
/// </para>
/// <para>
/// Два места в строке winws2: профиль SYN по сетям Google (<see cref="SynProfile"/>) — раньше
/// своих рецептов и секций пресета, и шаг слежения в секциях googlevideo и youtube
/// (<see cref="Watched"/>): на ClientHello winws2 ищет профиль уже по имени.
/// </para>
/// <para>
/// Подтверждение рукопожатия идёт без данных, а такие пакеты winws2 по умолчанию не видит;
/// без окна на нём сервер получает полное окно. Поэтому пустые пакеты берутся в перехват,
/// но только к сетям фронтов Google (<see cref="CaptureEmptyAcks"/>).
/// </para>
/// </remarks>
public static class VideoWindow
{
    /// <summary>Модуль Lua в папке движка (<c>build.cmd</c> кладёт его с приставкой <c>zy-</c>).</summary>
    public const string Module = "zy-video-window.lua";

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

    /// <summary>Адрес в сетях фронтов Google (<see cref="GoogleFrontEnds"/>).</summary>
    public static bool IsGoogle(System.Net.IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        uint value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);

        foreach (var cidr in GoogleFrontEnds)
        {
            var net = System.Net.IPAddress.Parse(cidr[..cidr.IndexOf('/')]).GetAddressBytes();
            int bits = int.Parse(cidr[(cidr.IndexOf('/') + 1)..]);
            uint first = (uint)(net[0] << 24 | net[1] << 16 | net[2] << 8 | net[3]);
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);

            if ((value & mask) == first)
                return true;
        }

        return false;
    }

    /// <summary>Профиль SYN: сети Google, пока протокол не узнан (SYN и подтверждение рукопожатия).</summary>
    public static IReadOnlyList<string> SynProfile() =>
    [
        "--new",
        "--name=Zephyron: узлы видео (SYN)",
        "--filter-tcp=443",
        "--filter-l7=unknown",
        "--ipset-ip=" + string.Join(',', GoogleFrontEnds),
        "--out-range=-n4",
        "--payload=all",
        "--lua-desync=zy_video_window:syn",
    ];

    /// <summary>Секция ловит имена видео YouTube: в её списке googlevideo или youtube.</summary>
    public static bool IsVideoSection(IReadOnlyList<string> arguments) =>
        arguments.Any(a => a.StartsWith("--hostlist=", StringComparison.Ordinal)
            && (a.Contains("googlevideo", StringComparison.OrdinalIgnoreCase) || a.Contains("youtube", StringComparison.OrdinalIgnoreCase)))
        && arguments.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal))
        && !arguments.Any(a => a.StartsWith("--filter-udp=", StringComparison.Ordinal));

    /// <summary>
    /// Секция с шагом слежения первым: он видит все пакеты (<c>--payload=all</c>), а прежний
    /// отбор нагрузки секции возвращается сразу после него.
    /// </summary>
    public static IReadOnlyList<string> Watched(IReadOnlyList<string> arguments)
    {
        int first = arguments.ToList().FindIndex(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal));

        if (first < 0)
            return arguments;

        var payload = arguments.Take(first).LastOrDefault(a => a.StartsWith("--payload=", StringComparison.Ordinal));
        var result = arguments.Take(first).ToList();
        result.Add("--payload=all");
        result.Add("--lua-desync=zy_video_window");
        result.Add(payload ?? "--payload=all");
        result.AddRange(arguments.Skip(first));
        return result;
    }

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
