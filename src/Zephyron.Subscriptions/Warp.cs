using Zephyron.Core;

namespace Zephyron.Subscriptions;

/// <summary>
/// Бесплатный выход Cloudflare WARP.
/// </summary>
/// <remarks>
/// <para>
/// Выход один — MASQUE, и от нас ему не нужно ничего: движок в поставке несёт
/// готовый клиент Cloudflare, сам регистрируется и сам хранит запись в своём
/// кэше. Нам остаётся добавить его к серверам подписки, когда включён.
/// </para>
/// <para>
/// Второй выход, по WireGuard, здесь был и убран. Он не мог работать
/// в принципе: Cloudflare кладёт идентификатор записи в поле <c>reserved</c>
/// каждого пакета, а sing-box 1.14 убрал это поле из настройки пира вовсе —
/// в структуре остались адрес, порт, ключи, allowed_ips и keepalive.
/// Проверено на живой сети: рукопожатие проходит, данные не возвращаются
/// ни на 2408, ни на 500, 4500, 1701, 908. В списке он держался вечным
/// «не отвечает», а закреплённый по ошибке — обрывал сеть целиком.
/// </para>
/// <para>
/// Вместе с ним ушла и вся обвязка: заведение ключей движком, регистрация
/// в Cloudflare, разрезанное приветствие TLS для обхода закрытого домена
/// регистрации и файл <c>config/warp.json</c>, где хранилась запись. Ничего
/// из этого MASQUE не требует — он делает это сам, внутри движка.
/// </para>
/// </remarks>
public static class Warp
{
    /// <summary>Имя выхода в списке серверов.</summary>
    public const string MasqueTag = "Cloudflare WARP";

    /// <summary>
    /// Домен, у которого движок заводит себе учётную запись.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Туда и только туда упирается весь WARP: пока регистрация не прошла,
    /// выход отвечает «tunnel not initialized» на каждое соединение. Запрос
    /// движок делает сам — <c>POST https://api.cloudflareclient.com/v0a4471/reg</c>, —
    /// и направить его нечем: из всех настроек выход MASQUE принимает только
    /// <c>detour</c> и <c>tls.server_name</c>. Проверено перебором схемы 19.09.2026:
    /// <c>server</c>, <c>server_port</c>, <c>license</c>, <c>fake_packets</c> и <c>tls.utls</c>
    /// движок объявляет незнакомыми полями и отказывается читать конфиг.
    /// </para>
    /// <para>
    /// <b>Десинк этому домену нужен, а не вреден</b> — и это замер, отменяющий
    /// прежнюю догадку. Вечером 19.09 проверены оба положения, с перезапуском
    /// движков между ними:
    /// </para>
    /// <list type="table">
    /// <item><term>секция #6 пресета применяется</term>
    /// <description>рукопожатие проходит, тело ответа не доходит:
    /// «context deadline exceeded … while reading body»</description></item>
    /// <item><term>домен внесён в исключения</term>
    /// <description>рукопожатия нет вовсе: «net/http: TLS handshake
    /// timeout»</description></item>
    /// </list>
    /// <para>
    /// То есть <c>multidisorder</c> из секции «Cloudflare WARP API» проводит
    /// рукопожатие сквозь фильтр, но дальше поток гибнет. Наше же приветствие
    /// — от Windows — проходит домен насквозь в ту же минуту: проба даёт
    /// TCP ок, TLS 1.3, тело получено; <c>GET /</c> отвечает 404 за 43 мс,
    /// <c>POST</c> на сам <c>/reg</c> — 400 за 196 мс.
    /// </para>
    /// <para>
    /// Значит дело в приветствии Go, а позиции разреза в рецепте считаются
    /// от содержимого приветствия — <c>host+2</c>, <c>sld+2</c>, <c>sniext+1</c>. То же
    /// наблюдалось в 0.5.4 на <c>hostfakesplit</c>: открывал домен для schannel
    /// и получал отказ у Go внутри sing-box. Лечится не исключением,
    /// а рецептом, выверенным на приветствии Go.
    /// </para>
    /// </remarks>
    public const string RegistrationHost = "api.cloudflareclient.com";

    /// <summary>
    /// Выходы, которые подмешиваются к серверам действующей подписки.
    /// </summary>
    /// <remarks>
    /// Подмешиваются, а не образуют свою подписку. Своей быть не может:
    /// конфиг движка собирается по одной ссылке, и WARP отдельной строкой
    /// в списке подписок занимал её место — выбор его выхода отключал рабочий
    /// VPN целиком.
    /// </remarks>
    public static IReadOnlyList<ProxyServer> Exits() => [FileServer() ?? MasqueServer()];

    /// <summary>Свой файл WARP (WireGuard/AmneziaWG), загруженный в карточке WARP.</summary>
    /// <remarks>
    /// Это источник WARP, а не ключ VPN: при включённом WARP туннель идёт через него вместо
    /// встроенного MASQUE, имя выхода то же (<see cref="MasqueTag"/>), и всё, что сверяет
    /// выход с WARP, продолжает работать. Без файла — встроенный.
    /// </remarks>
    public static string FilePath => Path.Combine("config", "warp.conf");

    /// <summary>Имя файла, как его выбрал человек; рядом с самим файлом.</summary>
    public static string FileNamePath => Path.Combine("config", "warp.name");

    /// <summary>Сервер из своего файла WARP; <c>null</c> — файла нет или он не читается.</summary>
    public static ProxyServer? FileServer(string? path = null)
    {
        try
        {
            path ??= FilePath;

            if (!File.Exists(path))
                return null;

            return Parse(File.ReadAllText(path), out var server, out _) ? server : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Разбирает файл WARP; принимает только WireGuard или AmneziaWG.</summary>
    public static bool Parse(string text, out ProxyServer? server, out string? error)
    {
        server = null;
        var found = KeyImport.FromText(text, MasqueTag);

        if (found.Keys.Count == 0)
        {
            error = found.Problem ?? "в файле нет настроек WireGuard";
            return false;
        }

        if (!ProxyUriParser.TryParse(found.Keys[0], out var parsed, out error) || parsed is null)
            return false;

        if (parsed.Protocol is not (ProxyProtocol.Wireguard))
        {
            error = "это не файл WARP: нужен WireGuard или AmneziaWG (.conf)";
            return false;
        }

        server = parsed with { Tag = MasqueTag };
        return true;
    }

    /// <summary>Сохраняет свой файл WARP; бросает, если он не читается.</summary>
    public static void SaveFile(string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);

        if (!Parse(text, out _, out var error))
            throw new InvalidDataException(error ?? "файл не читается");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        WriteAtomic(FilePath, text);
        WriteAtomic(FileNamePath, Path.GetFileName(sourcePath));
    }

    // Подмена файла целиком: оборванная запись не оставит обрубок вместо рабочего файла.
    private static void WriteAtomic(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Убирает свой файл: WARP снова встроенный.</summary>
    public static void RemoveFile()
    {
        File.Delete(FilePath);
        File.Delete(FileNamePath);
    }

    /// <summary>Имя своего файла WARP; <c>null</c> — используется встроенный.</summary>
    public static string? FileName()
    {
        try
        {
            return FileServer() is null ? null : File.Exists(FileNamePath) ? File.ReadAllText(FileNamePath).Trim() : "warp.conf";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Выходы туннеля: только WARP, когда он включён, иначе только подписки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одно из двух, а не вместе (владелец, 01.10). До того WARP подмешивался
    /// к серверам подписок запасным: в автоподбор входил, когда живых серверов
    /// подписки не оставалось. Со стороны это читалось плохо — включённый WARP
    /// почти никогда не работал, а выбранный руками соседствовал с подписками,
    /// и надзор при заминке уводил с него на автоподбор подписок.
    /// </para>
    /// <para>
    /// Теперь выключатель WARP — это выбор пути: включён — туннель идёт только
    /// через WARP, подписки на паузе и не запрашиваются; выключен — только
    /// подписки. Сам с одного на другое туннель не переходит: при мёртвом
    /// выходе работает обход, как и прежде.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ProxyServer> TunnelExits(AppSettings settings, IReadOnlyList<ProxyServer> subscriptions) =>
        settings.WarpEnabled ? Exits() : UsesFallback(settings) ? [.. subscriptions, .. Exits()] : subscriptions;

    /// <summary>
    /// WARP как запасной выход на пути VPN: есть подписка или ключи, путь не WARP, запасной не выключен.
    /// </summary>
    public static bool UsesFallback(AppSettings settings) =>
        !settings.WarpEnabled && settings.WarpFallback && settings.HasSubscriptionExit;

    /// <summary>Подходит ли сервер подписки на роль посредника для MASQUE.</summary>
    public static bool CanCarryWarp(ProxyServer server) =>
        server.IsUsableOutbound && !server.IsEndpoint && !server.IsSelfRegistering;

    /// <summary>
    /// Через какой сервер подписки выход MASQUE дойдёт до Cloudflare; <c>null</c> — напрямую.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прямое соединение с Cloudflare у части сетей обрывается ещё на рукопожатии,
    /// и движок не может завести себе учётную запись («TLS handshake timeout»).
    /// Через сервер подписки она заводится и ложится в кэш движка.
    /// </para>
    /// <para>
    /// Порядок: сервер, выбранный страной выхода (<see cref="AppSettings.WarpCountryTag"/>),
    /// затем закреплённый на вкладке, затем самый быстрый из не отмеченных мёртвыми,
    /// затем первый годный. Подписки нет — <c>null</c>, и всё остаётся как было.
    /// </para>
    /// </remarks>
    public static ProxyServer? Detour(
        AppSettings settings,
        IReadOnlyList<ProxyServer> subscriptions,
        IReadOnlySet<string> dead,
        IReadOnlyDictionary<string, double> latencies)
    {
        var fit = subscriptions.Where(CanCarryWarp).ToList();

        ProxyServer? Named(string? tag) => string.IsNullOrWhiteSpace(tag)
            ? null
            : fit.FirstOrDefault(s => s.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

        return Named(settings.WarpCountryTag)
            ?? Named(settings.PreferredServer)
            ?? fit.Where(s => !dead.Contains(s.Tag))
                .OrderBy(s => latencies.TryGetValue(s.Tag, out var ms) ? ms : double.MaxValue)
                .FirstOrDefault()
            ?? fit.FirstOrDefault();
    }

    /// <summary>
    /// Идёт ли через посредника весь туннель WARP: страна выбрана, и посредник — именно её сервер.
    /// </summary>
    public static bool ExitsThrough(AppSettings settings, ProxyServer? detour) =>
        detour is not null && !string.IsNullOrWhiteSpace(settings.WarpCountryTag)
        && detour.Tag.Equals(settings.WarpCountryTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Выход, который ставить в селектор первым: WARP, когда включён он, иначе выбранный сервер подписки.
    /// </summary>
    /// <remarks>
    /// Выбранный сервер подписки в настройках сохраняется и при включённом WARP:
    /// выключили WARP — туннель возвращается туда, где был.
    /// </remarks>
    public static string? PreferredExit(AppSettings settings) =>
        settings.WarpEnabled ? MasqueTag : settings.PreferredServer;

    /// <summary>
    /// Выход MASQUE: тот же WARP, поверх HTTP/2 на 443 порту (с 23.09; по QUIC не доходил).
    /// </summary>
    /// <remarks>
    /// Адрес и порт здесь условны: движок выбирает узел сам и полей для них
    /// не принимает вовсе. Держим их ради общей модели — показу нужно что-то
    /// написать в подписи строки.
    /// </remarks>
    public static ProxyServer MasqueServer() => new()
    {
        Protocol = ProxyProtocol.Masque,
        Tag = MasqueTag,
        Host = "cloudflareclient.com",
        Port = 443,
        Credential = string.Empty,
        Transport = "h2",
        Security = "tls",
    };
}
