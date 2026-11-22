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
        SynStep,
    ];

    /// <summary>
    /// Шаг SYN: множитель окна занижается до 1, а не снимается — малое окно до 131 КБ вместо 64,
    /// вдвое быстрее (замер 13–14.11: 11,5–21,5 Мбит/с против 5,1–9,2 на тех же узлах; 22.11 у владельца
    /// SYN к узлу под окном ушёл с wscale 1, и видео с него шло в 2160p).
    /// Каждое окно такого соединения пересчитывает шаг <see cref="RampStep"/>.
    /// </summary>
    public const string SynStep = "--lua-desync=zy_video_window:syn:scale=1";

    /// <summary>Страховочный профиль пересчёта окна на адресах Google — последним в строке.</summary>
    public const string RampProfileName = "--name=Zephyron: узлы видео (окно)";

    /// <summary>Пересчёт окна соединения с заниженным множителем — последним в каждой секции TCP.</summary>
    public const string RampStep = "--lua-desync=zy_video_window:ramp";

    /// <summary>
    /// Дописывает пересчёт окна в конец каждой секции TCP, кроме секции SYN.
    /// </summary>
    /// <remarks>
    /// После приветствия соединение уходит в секцию по своему имени — googlevideo, youtube,
    /// свой рецепт, щит «напрямую», «Google (сервисы)», — и пересчитывать окно должна она:
    /// иначе сервер видит окно в 128 раз меньше и соединение почти встаёт (замер 11.11:
    /// 45 КБ за 30 с). Последним, под <c>--out-range=a</c> и <c>--payload=all</c>: шаг видит
    /// каждый исходящий пакет, пустые подтверждения тоже, и не сдвигает диапазоны рецептов
    /// секции. Прочим соединениям шаг не делает ничего и выключается на первом пакете.
    /// </remarks>
    /// <param name="listHasGoogle">
    /// Есть ли в списке имена Google; <c>null</c> — не проверять, пересчёт в каждую секцию TCP.
    /// Секция, отбирающая только по спискам без имён Google, соединение к узлу Google
    /// не возьмёт, и шаг ей ни к чему: 22.11 шаг в каждой из шестидесяти секций Adaptive
    /// (по 60 символов) вывел строку запуска за предел Windows, и winws2 не поднимался.
    /// </param>
    public static void Ramp(List<string> arguments, Func<string, bool>? listHasGoogle = null)
    {
        if (!arguments.Contains(SynStep))
            return;

        int end = arguments.Count;

        for (int i = arguments.Count - 1; i >= -1; i--)
        {
            if (i >= 0 && arguments[i] != "--new")
                continue;

            int start = i + 1;
            var section = arguments.GetRange(start, end - start);

            if (section.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal))
                && !section.Contains(SynStep)
                && !section.Contains(RampStep)
                && MaySeeGoogle(section, listHasGoogle))
            {
                arguments.InsertRange(end, ["--out-range=a", "--payload=all", RampStep]);
            }

            end = i;
        }

        // Страховка последним профилем: соединение к узлу Google с заниженным множителем, чьё имя
        // не взяла ни одна секция (обновления Chrome gvt1.com идут с тех же узлов, а общая секция
        // Adaptive ловит сайты по именам), осталось бы без пересчёта и почти встало бы.
        // Здесь — только пересчёт: другим соединениям шаг ничего не делает.
        if (arguments.Contains(RampProfileName))
            return;

        arguments.AddRange(
        [
            "--new",
            RampProfileName,
            "--filter-tcp=443",
            "--ipset-ip=" + string.Join(',', GoogleFrontEnds),
            "--out-range=a",
            "--payload=all",
            RampStep,
        ]);
    }

    /// <summary>Части имён, по которым видно Google: узлы видео, сайты и CDN Google, хранилище googleapis.</summary>
    private static readonly string[] GoogleMarks =
        ["google", "youtube", "youtu.be", "ytimg", "ggpht", "gstatic", "gvt1", "gvt2", "doubleclick"];

    /// <summary>Есть ли среди имён имя Google.</summary>
    public static bool HasGoogleName(IEnumerable<string> names) =>
        names.Any(n => GoogleMarks.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Может ли секция взять соединение к узлу Google: отбирает не по именам (адреса, порты,
    /// протокол) или в её списках есть имя Google.
    /// </summary>
    private static bool MaySeeGoogle(List<string> section, Func<string, bool>? listHasGoogle)
    {
        if (listHasGoogle is null)
            return true;

        var lists = section.Where(a => a.StartsWith("--hostlist=", StringComparison.Ordinal))
            .Select(a => a["--hostlist=".Length..].Trim('"'))
            .ToList();

        var inline = section.Where(a => a.StartsWith("--hostlist-domains=", StringComparison.Ordinal))
            .SelectMany(a => a["--hostlist-domains=".Length..].Split(','))
            .ToList();

        if (lists.Count == 0 && inline.Count == 0)
            return true;

        return HasGoogleName(inline) || lists.Any(listHasGoogle);
    }

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

    /// <summary>Порты HTTPS Discord и запасные HTTPS Cloudflare: ради них секции с подделкой SYN и стоят.</summary>
    public const string SynDataPorts = "80,443,1080,2053,2083,2087,2096,8443";

    /// <summary>
    /// Секции по адресам с подделкой SYN: не трогают фронты Google и не выходят за порты HTTPS.
    /// </summary>
    /// <remarks>
    /// «discord.com (IP fallback)» стоит на портах 80 и 443–65535 по <c>ipset-discord</c>, а в нём
    /// AWS, Google Cloud и Cloudflare целиком — там же серверы игр. Подделка SYN на их портах
    /// (жалобы 13.11: Heroes of Newerth не подключается и на «напрямую», Aion 2 не входит
    /// с Universal) ломала соединения, которым десинк вовсе не нужен. Для Discord и сайтов
    /// Cloudflare секция нужна на HTTPS и его запасных портах — их и оставляем.
    /// </remarks>
    public static IReadOnlyList<string> SparedFromSynData(IReadOnlyList<string> sectionArguments)
    {
        bool byAddress = sectionArguments.Any(a => a.StartsWith("--ipset=", StringComparison.Ordinal));
        bool synData = sectionArguments.Any(a => a.StartsWith("--lua-desync=syndata", StringComparison.Ordinal));

        if (!byAddress || !synData)
            return sectionArguments;

        var result = sectionArguments
            .Select(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal) && WideRange(a["--filter-tcp=".Length..])
                ? "--filter-tcp=" + SynDataPorts
                : a)
            .ToList();

        result.Add("--ipset-exclude-ip=" + string.Join(',', GoogleFrontEnds));
        return result;

        // Диапазон, уходящий за 8443, — «все порты» по сути.
        static bool WideRange(string ports) =>
            ports.Split(',').Any(p => p.Contains('-') && int.TryParse(p[(p.IndexOf('-') + 1)..], out var last) && last > 8443);
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
